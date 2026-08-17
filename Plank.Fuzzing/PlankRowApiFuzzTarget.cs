using System.Text;

namespace Plank.Fuzzing;

/// <summary>
/// Writes rows through the generated row-oriented writer, then reads them back
/// through the generated row reader and checks they survived.
/// </summary>
/// <remarks>
/// The row API's write half was entirely unfuzzed, and not for want of trying:
/// it cannot be driven without generated code. RowWriterBase&lt;TSlot&gt; is
/// abstract over four methods the generator emits, and RowGroupWriterCore&lt;TSlot&gt;
/// is generic over a generated slot type, so a fuzzer has nothing to instantiate.
/// The writer fuzz target drives the columnar writer only, and the reader target
/// drives RowReaderCore directly — which covers the read states but never the
/// write pipeline. RowBufferSlot, RowValueSizeEstimator, PipelineRowWriterBase,
/// RowApiColumnWriteState and RowWriterBase all measured zero.
///
/// This closes that by giving the fuzzing project its own [ParquetSchema] row
/// types (see RowFuzzSchemas) and driving the generated pipeline over values the
/// fuzzer chooses. It is a round trip, so a value that comes back wrong is a
/// finding and not just a crash — the same standard the columnar writer target
/// holds itself to.
/// </remarks>
public static class PlankRowApiFuzzTarget
{
    // Rows are cheap but each one costs a write and a read-back, and AFL wants
    // many small executions rather than few large ones.
    const int MaxRows = 48;
    const int MaxBinaryLength = 24;

    public static void Execute(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
            return;

        try
        {
            var cursor = new Cursor(data[1..]);
            switch (data[0] % 3)
            {
                case 0: RunFixed(ref cursor); break;
                case 1: RunNullable(ref cursor); break;
                default: RunBinary(ref cursor); break;
            }
        }
        catch (Exception ex) when (ex is CorruptParquetException or NotSupportedException
            or InvalidOperationException)
        {
        }
    }

    static void RunFixed(ref Cursor cursor)
    {
        var count = cursor.NextInt(1, MaxRows + 1);
        var expected = new (bool Flag, int Id, long Sequence, double Measure)[count];
        for (var i = 0; i < count; i++)
            expected[i] = (cursor.NextInt(0, 2) == 0, cursor.NextInt32(), cursor.NextInt64(), cursor.NextDouble());

        using var stream = new MemoryStream();
        var writer = RowFuzzFixed.CreateRowWriter(stream);
        foreach (var (flag, id, sequence, measure) in expected)
        {
            var row = writer.GetRow();
            row.Flag = flag;
            row.Id = id;
            row.Sequence = sequence;
            row.Measure = measure;
            writer.Next();
        }

        writer.Complete();

        var index = 0;
        using var reader = RowFuzzFixed.CreateRowReader(new MemoryStream(stream.ToArray(), writable: false));
        while (reader.MoveNext())
        {
            var row = reader.Current;
            var want = expected[index];
            Check(row.Flag == want.Flag, "Flag", index);
            Check(row.Id == want.Id, "Id", index);
            Check(row.Sequence == want.Sequence, "Sequence", index);
            // Compared bitwise so NaN matches NaN, which equality does not.
            Check(BitConverter.DoubleToInt64Bits(row.Measure) == BitConverter.DoubleToInt64Bits(want.Measure),
                "Measure", index);
            index++;
        }

        Check(index == count, "row count", index);
    }

    static void RunNullable(ref Cursor cursor)
    {
        var count = cursor.NextInt(1, MaxRows + 1);
        var expected = new (int? Id, long? Sequence, double? Measure, bool? Flag)[count];
        for (var i = 0; i < count; i++)
            expected[i] = (
                cursor.NextInt(0, 4) == 0 ? null : cursor.NextInt32(),
                cursor.NextInt(0, 4) == 0 ? null : cursor.NextInt64(),
                cursor.NextInt(0, 4) == 0 ? null : cursor.NextDouble(),
                cursor.NextInt(0, 4) == 0 ? null : cursor.NextInt(0, 2) == 0);

        using var stream = new MemoryStream();
        var writer = RowFuzzNullable.CreateRowWriter(stream);
        foreach (var (id, sequence, measure, flag) in expected)
        {
            var row = writer.GetRow();
            row.Id = id;
            row.Sequence = sequence;
            row.Measure = measure;
            row.Flag = flag;
            writer.Next();
        }

        writer.Complete();

        var index = 0;
        using var reader = RowFuzzNullable.CreateRowReader(new MemoryStream(stream.ToArray(), writable: false));
        while (reader.MoveNext())
        {
            var row = reader.Current;
            var want = expected[index];
            Check(row.Id == want.Id, "Id", index);
            Check(row.Sequence == want.Sequence, "Sequence", index);
            Check(row.Flag == want.Flag, "Flag", index);
            Check(NullableDoubleMatches(row.Measure, want.Measure), "Measure", index);
            index++;
        }

        Check(index == count, "row count", index);
    }

    static void RunBinary(ref Cursor cursor)
    {
        var count = cursor.NextInt(1, MaxRows + 1);
        var expected = new (int Id, byte[] Payload, string Label)[count];
        for (var i = 0; i < count; i++)
            expected[i] = (cursor.NextInt32(), cursor.NextBytes(MaxBinaryLength), cursor.NextLabel());

        using var stream = new MemoryStream();
        var writer = RowFuzzBinary.CreateRowWriter(stream);
        foreach (var (id, payload, label) in expected)
        {
            var row = writer.GetRow();
            row.Id = id;
            row.Payload = payload;
            row.Label = label;
            writer.Next();
        }

        writer.Complete();

        var index = 0;
        using var reader = RowFuzzBinary.CreateRowReader(new MemoryStream(stream.ToArray(), writable: false));
        while (reader.MoveNext())
        {
            var row = reader.Current;
            var want = expected[index];
            Check(row.Id == want.Id, "Id", index);
            Check(row.Payload.SequenceEqual(want.Payload), "Payload", index);
            Check(string.Equals(row.Label, want.Label, StringComparison.Ordinal), "Label", index);
            index++;
        }

        Check(index == count, "row count", index);
    }

    static bool NullableDoubleMatches(double? actual, double? expected)
    {
        if (actual is null || expected is null)
            return actual is null && expected is null;
        return BitConverter.DoubleToInt64Bits(actual.Value) == BitConverter.DoubleToInt64Bits(expected.Value);
    }

    // A round-trip mismatch is a correctness bug, not corrupt input: the bytes
    // were produced by the writer moments earlier. It has to escape the target's
    // expected-exception filter to be recorded, so it is not one of those types.
    static void Check(bool condition, string what, int row)
    {
        if (!condition)
            throw new RowApiRoundTripException($"Row API round trip lost '{what}' at row {row}.");
    }

    /// <summary>Signals that a written row did not read back as written.</summary>
    public sealed class RowApiRoundTripException(string message) : Exception(message);

    // Deliberately not the writer target's ByteCursor: that one is private to it
    // and shaped for column specs. Running out of input yields zeros rather than
    // throwing, so a truncated case stays a valid smaller case.
    ref struct Cursor(ReadOnlySpan<byte> data)
    {
        readonly ReadOnlySpan<byte> _data = data;
        int _offset;

        byte NextByte()
            => _offset < _data.Length ? _data[_offset++] : (byte)0;

        internal int NextInt(int minInclusive, int maxExclusive)
        {
            var range = maxExclusive - minInclusive;
            return range <= 0 ? minInclusive : minInclusive + (NextByte() % range);
        }

        internal int NextInt32()
            => (NextByte() << 24) | (NextByte() << 16) | (NextByte() << 8) | NextByte();

        internal long NextInt64()
            => ((long)NextInt32() << 32) | (uint)NextInt32();

        // Bit patterns rather than a numeric range, so NaN, the infinities and
        // the denormals all come up.
        internal double NextDouble()
            => BitConverter.Int64BitsToDouble(NextInt64());

        internal byte[] NextBytes(int maxLength)
        {
            var bytes = new byte[NextInt(0, maxLength + 1)];
            for (var i = 0; i < bytes.Length; i++)
                bytes[i] = NextByte();
            return bytes;
        }

        // Built from scalar values rather than raw bytes: an unpaired surrogate
        // is not round-trippable through UTF-8 by any encoder, so comparing it
        // back would report a defect that is not one.
        internal string NextLabel()
        {
            var length = NextInt(0, 9);
            var builder = new StringBuilder(length);
            for (var i = 0; i < length; i++)
            {
                var pick = NextByte();
                builder.Append(pick switch
                {
                    < 0x40 => (char)('a' + (pick % 26)),      // ASCII
                    < 0x80 => (char)(0x00A1 + (pick % 0x40)), // two-byte UTF-8
                    < 0xC0 => (char)(0x0800 + (pick % 0x40)), // three-byte UTF-8
                    _ => '�'
                });
            }

            return builder.ToString();
        }
    }
}
