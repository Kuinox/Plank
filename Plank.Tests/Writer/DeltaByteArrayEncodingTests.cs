using Plank.Schema;
using Plank.Writing;
using Plank.Writing.Encoding;

namespace Plank.Tests.Writer;

internal sealed class DeltaByteArrayEncodingTests
{
    static readonly Column DeltaByteArrayColumn = new("value", ParquetPhysicalType.ByteArray,
        new ColumnOptions(encodings: [EncodingKind.DeltaByteArray]));
    static readonly Column DeltaLengthByteArrayColumn = new("value", ParquetPhysicalType.ByteArray,
        new ColumnOptions(encodings: [EncodingKind.DeltaLengthByteArray]));

    [Test]
    public void DeltaByteArrayMatchesScalarReferenceAcrossRepresentationsAndNullability()
    {
        int[] counts = [0, 1, 31, 4_096];
        int[] prefixLengths = [0, 3, 63];

        foreach (var count in counts)
        foreach (var prefixLength in prefixLengths)
        {
            var values = CreateValues(count, prefixLength);
            var expected = EncodeDeltaByteArrayReference(values);
            AssertEqual(expected, EncodeRequiredByteArrays(values, EncodingKind.DeltaByteArray),
                $"required byte[] count={count}, prefix={prefixLength}");

            var memoryValues = Array.ConvertAll(values, static value => (ReadOnlyMemory<byte>)value);
            AssertEqual(expected, EncodeRequiredMemory(memoryValues, EncodingKind.DeltaByteArray),
                $"required memory count={count}, prefix={prefixLength}");

            var optionalValues = CreateOptionalValues(values);
            var denseValues = optionalValues.Where(static value => value is not null).ToArray()!;
            var optionalExpected = denseValues.Length == 0 ? [] : EncodeDeltaByteArrayReference(denseValues);
            AssertEqual(optionalExpected, EncodeOptionalByteArrays(optionalValues, EncodingKind.DeltaByteArray),
                $"optional byte[] count={count}, prefix={prefixLength}");

            var optionalMemory = Array.ConvertAll(optionalValues,
                static value => value is null ? (ReadOnlyMemory<byte>?)null : value);
            AssertEqual(optionalExpected, EncodeOptionalMemory(optionalMemory, EncodingKind.DeltaByteArray),
                $"optional memory count={count}, prefix={prefixLength}");
        }
    }

    [Test]
    public void DeltaLengthByteArrayMatchesReferenceAcrossRepresentationsAndNullability()
    {
        int[] counts = [0, 1, 31, 4_096];

        foreach (var count in counts)
        {
            var values = CreateValues(count, 3);
            var expected = EncodeDeltaLengthByteArrayReference(values);
            AssertEqual(expected, EncodeRequiredByteArrays(values, EncodingKind.DeltaLengthByteArray),
                $"required byte[] count={count}");

            var memoryValues = Array.ConvertAll(values, static value => (ReadOnlyMemory<byte>)value);
            AssertEqual(expected, EncodeRequiredMemory(memoryValues, EncodingKind.DeltaLengthByteArray),
                $"required memory count={count}");

            var optionalValues = CreateOptionalValues(values);
            var denseValues = optionalValues.Where(static value => value is not null).ToArray()!;
            var optionalExpected = denseValues.Length == 0 ? [] : EncodeDeltaLengthByteArrayReference(denseValues);
            AssertEqual(optionalExpected,
                EncodeOptionalByteArrays(optionalValues, EncodingKind.DeltaLengthByteArray),
                $"optional byte[] count={count}");

            var optionalMemory = Array.ConvertAll(optionalValues,
                static value => value is null ? (ReadOnlyMemory<byte>?)null : value);
            AssertEqual(optionalExpected, EncodeOptionalMemory(optionalMemory, EncodingKind.DeltaLengthByteArray),
                $"optional memory count={count}");
        }
    }

    [Test]
    public void OptionalAllNullValuesWriteNoBytes()
    {
        var byteArrays = new byte[31][];
        var memory = new ReadOnlyMemory<byte>?[31];

        foreach (var encoding in new[] { EncodingKind.DeltaByteArray, EncodingKind.DeltaLengthByteArray })
        {
            AssertEqual([], EncodeOptionalByteArrays(byteArrays, encoding), $"all-null byte[] {encoding}");
            AssertEqual([], EncodeOptionalMemory(memory, encoding), $"all-null memory {encoding}");
        }
    }

    [Test]
    public void RequiredByteArrayNullStillThrows()
    {
        byte[][] values = [[1, 2, 3], null!];

        foreach (var encoding in new[] { EncodingKind.DeltaByteArray, EncodingKind.DeltaLengthByteArray })
        {
            try
            {
                EncodeRequiredByteArrays(values, encoding);
            }
            catch (InvalidOperationException)
            {
                continue;
            }

            throw new InvalidOperationException($"Expected required {encoding} values to reject null.");
        }
    }

    static byte[] EncodeRequiredByteArrays(byte[][] values, EncodingKind encoding)
    {
        var factory = CreateFactory();
        var writer = CreateWriter();
        try
        {
            if (encoding == EncodingKind.DeltaByteArray)
                DeltaByteArrayEncoding.WriteValues(DeltaByteArrayColumn, values, factory, ref writer);
            else
                DeltaLengthByteArrayEncoding.WriteValues(DeltaLengthByteArrayColumn, values, factory, ref writer);
            return CopyWritten(ref writer);
        }
        finally
        {
            writer.Dispose();
        }
    }

    static byte[] EncodeRequiredMemory(ReadOnlyMemory<byte>[] values, EncodingKind encoding)
    {
        var factory = CreateFactory();
        var writer = CreateWriter();
        try
        {
            if (encoding == EncodingKind.DeltaByteArray)
                DeltaByteArrayEncoding.WriteValues(DeltaByteArrayColumn, values, factory, ref writer);
            else
                DeltaLengthByteArrayEncoding.WriteValues(DeltaLengthByteArrayColumn, values, factory, ref writer);
            return CopyWritten(ref writer);
        }
        finally
        {
            writer.Dispose();
        }
    }

    static byte[] EncodeOptionalByteArrays(byte[][] values, EncodingKind encoding)
    {
        var factory = CreateFactory();
        var writer = CreateWriter();
        try
        {
            if (encoding == EncodingKind.DeltaByteArray)
                DeltaByteArrayEncoding.WriteOptionalByteArrayValues(DeltaByteArrayColumn, values, factory, ref writer);
            else
                DeltaLengthByteArrayEncoding.WriteOptionalByteArrayValues(DeltaLengthByteArrayColumn, values, factory,
                    ref writer);
            return CopyWritten(ref writer);
        }
        finally
        {
            writer.Dispose();
        }
    }

    static byte[] EncodeOptionalMemory(ReadOnlyMemory<byte>?[] values, EncodingKind encoding)
    {
        var factory = CreateFactory();
        var writer = CreateWriter();
        try
        {
            if (encoding == EncodingKind.DeltaByteArray)
                DeltaByteArrayEncoding.WriteOptionalMemoryValues(DeltaByteArrayColumn, values, factory, ref writer);
            else
                DeltaLengthByteArrayEncoding.WriteOptionalMemoryValues(DeltaLengthByteArrayColumn, values, factory,
                    ref writer);
            return CopyWritten(ref writer);
        }
        finally
        {
            writer.Dispose();
        }
    }

    static byte[] EncodeDeltaByteArrayReference(byte[][] values)
    {
        var prefixLengths = new int[values.Length];
        var suffixLengths = new int[values.Length];
        ReadOnlySpan<byte> previous = [];

        for (var i = 0; i < values.Length; i++)
        {
            var current = values[i];
            var prefixLength = ScalarSharedPrefixLength(previous, current);
            prefixLengths[i] = prefixLength;
            suffixLengths[i] = current.Length - prefixLength;
            previous = current;
        }

        var writer = CreateWriter();
        try
        {
            DeltaBinaryPackedEncoding.WriteInt32(prefixLengths, ref writer);
            DeltaBinaryPackedEncoding.WriteInt32(suffixLengths, ref writer);
            for (var i = 0; i < values.Length; i++)
                writer.Write(values[i].AsSpan(prefixLengths[i]));
            return CopyWritten(ref writer);
        }
        finally
        {
            writer.Dispose();
        }
    }

    static byte[] EncodeDeltaLengthByteArrayReference(byte[][] values)
    {
        var lengths = Array.ConvertAll(values, static value => value.Length);
        var writer = CreateWriter();
        try
        {
            DeltaBinaryPackedEncoding.WriteInt32(lengths, ref writer);
            for (var i = 0; i < values.Length; i++)
                writer.Write(values[i]);
            return CopyWritten(ref writer);
        }
        finally
        {
            writer.Dispose();
        }
    }

    static byte[][] CreateValues(int count, int sharedPrefixLength)
    {
        var values = new byte[count][];
        var state = unchecked(0x9e3779b9u + (uint)sharedPrefixLength);
        int[] suffixLengths = [0, 1, 7, 16, 64];

        for (var i = 0; i < values.Length; i++)
        {
            var prefixLength = i == 0 ? 0 : sharedPrefixLength;
            var length = checked(prefixLength + suffixLengths[i % suffixLengths.Length]);
            var value = new byte[length];
            for (var j = 0; j < value.Length; j++)
                value[j] = (byte)NextRandom(ref state);
            if (prefixLength > 0)
            {
                value.AsSpan(0, prefixLength).Fill(0x5a);
                if (value.Length > prefixLength)
                    value[prefixLength] = unchecked((byte)i);
            }
            else if (value.Length > 0 && i > 0 && values[i - 1].Length > 0 && value[0] == values[i - 1][0])
                value[0]++;
            values[i] = value;
        }

        return values;
    }

    static uint NextRandom(ref uint state)
    {
        state ^= state << 13;
        state ^= state >> 17;
        state ^= state << 5;
        return state;
    }

    static byte[][] CreateOptionalValues(byte[][] values)
    {
        var optional = new byte[values.Length][];
        for (var i = 0; i < values.Length; i++)
            optional[i] = i % 4 == 0 ? null! : values[i];
        return optional;
    }

    static int ScalarSharedPrefixLength(ReadOnlySpan<byte> previous, ReadOnlySpan<byte> current)
    {
        var maxLength = Math.Min(previous.Length, current.Length);
        var index = 0;
        while (index < maxLength && previous[index] == current[index])
            index++;
        return index;
    }

    static BufferWriterFactory CreateFactory()
        => new(DefaultParquetBufferPool.Shared, 64 * 1024, 64 * 1024, 64 * 1024, 64 * 1024);

    static BufferWriter CreateWriter()
        => new(DefaultParquetBufferPool.Shared, 64 * 1024, 64 * 1024);

    static byte[] CopyWritten(ref BufferWriter writer)
    {
        var result = new byte[writer.WrittenLength];
        writer.CopyTo(result);
        return result;
    }

    static void AssertEqual(ReadOnlySpan<byte> expected, ReadOnlySpan<byte> actual, string scenario)
    {
        if (!expected.SequenceEqual(actual))
            throw new InvalidOperationException($"Encoded bytes differ for {scenario}.");
    }
}
