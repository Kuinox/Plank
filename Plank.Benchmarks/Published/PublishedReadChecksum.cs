using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Plank.Benchmarks.Published;

/// <summary>
/// A deliberately cheap additive sink for decoded benchmark values.
/// </summary>
/// <remarks>
/// This is not intended to be a collision-resistant correctness check. It keeps the decoded values
/// observable to the JIT without putting a serial hash chain in the read benchmark's hot path.
/// Variable-length values contribute their byte length, which avoids charging the benchmark for a
/// second pass over every string payload.
/// </remarks>
static class PublishedReadChecksum
{
    const ulong Seed = 0x9E37_79B9_7F4A_7C15UL;
    const ulong NullValue = 0xD1B5_4A32_D192_ED03UL;

    public struct Accumulator
    {
        ulong _sum;

        public static Accumulator StartPiece(int columnIndex, int rowGroupIndex, int valueCount)
        {
            var accumulator = new Accumulator { _sum = Seed };
            accumulator.AddWord(unchecked((uint)columnIndex));
            accumulator.AddWord(unchecked((uint)rowGroupIndex));
            accumulator.AddWord(unchecked((uint)valueCount));
            return accumulator;
        }

        public ulong Finish()
            => _sum;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void AddWord(ulong value)
            => _sum = unchecked(_sum + value);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void AddNull()
            => AddWord(NullValue);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void AddValue<T>(T value)
        {
            if (typeof(T) == typeof(bool))
                AddWord(Unsafe.As<T, bool>(ref value) ? 1UL : 0UL);
            else if (typeof(T) == typeof(bool?))
                AddNullable(Unsafe.As<T, bool?>(ref value));
            else if (typeof(T) == typeof(int))
                AddWord(unchecked((uint)Unsafe.As<T, int>(ref value)));
            else if (typeof(T) == typeof(int?))
                AddNullable(Unsafe.As<T, int?>(ref value));
            else if (typeof(T) == typeof(long))
                AddWord(unchecked((ulong)Unsafe.As<T, long>(ref value)));
            else if (typeof(T) == typeof(long?))
                AddNullable(Unsafe.As<T, long?>(ref value));
            else if (typeof(T) == typeof(double))
                AddWord(unchecked((ulong)BitConverter.DoubleToInt64Bits(Unsafe.As<T, double>(ref value))));
            else if (typeof(T) == typeof(double?))
                AddNullable(Unsafe.As<T, double?>(ref value));
            else if (typeof(T) == typeof(DateTime))
                AddWord(unchecked((ulong)Unsafe.As<T, DateTime>(ref value).Ticks));
            else if (typeof(T) == typeof(DateTime?))
                AddNullable(Unsafe.As<T, DateTime?>(ref value));
            else if (typeof(T) == typeof(DateTimeOffset))
                AddWord(unchecked((ulong)Unsafe.As<T, DateTimeOffset>(ref value).UtcTicks));
            else if (typeof(T) == typeof(DateTimeOffset?))
                AddNullable(Unsafe.As<T, DateTimeOffset?>(ref value));
            else
                throw new NotSupportedException($"Unsupported checksum value '{typeof(T)}'.");
        }

        public void AddValues<T>(ReadOnlySpan<T> values)
        {
            if (typeof(T) == typeof(int))
            {
                ref var first = ref MemoryMarshal.GetReference(values);
                var typed = MemoryMarshal.CreateReadOnlySpan(
                    ref Unsafe.As<T, int>(ref first), values.Length);
                for (var index = 0; index < typed.Length; index++)
                    AddWord(unchecked((uint)typed[index]));
                return;
            }
            if (typeof(T) == typeof(int?))
            {
                ref var first = ref MemoryMarshal.GetReference(values);
                var typed = MemoryMarshal.CreateReadOnlySpan(
                    ref Unsafe.As<T, int?>(ref first), values.Length);
                for (var index = 0; index < typed.Length; index++)
                    AddNullable(typed[index]);
                return;
            }
            if (typeof(T) == typeof(long))
            {
                ref var first = ref MemoryMarshal.GetReference(values);
                var typed = MemoryMarshal.CreateReadOnlySpan(
                    ref Unsafe.As<T, long>(ref first), values.Length);
                for (var index = 0; index < typed.Length; index++)
                    AddWord(unchecked((ulong)typed[index]));
                return;
            }
            if (typeof(T) == typeof(long?))
            {
                ref var first = ref MemoryMarshal.GetReference(values);
                var typed = MemoryMarshal.CreateReadOnlySpan(
                    ref Unsafe.As<T, long?>(ref first), values.Length);
                for (var index = 0; index < typed.Length; index++)
                    AddNullable(typed[index]);
                return;
            }
            if (typeof(T) == typeof(double))
            {
                ref var first = ref MemoryMarshal.GetReference(values);
                var typed = MemoryMarshal.CreateReadOnlySpan(
                    ref Unsafe.As<T, double>(ref first), values.Length);
                for (var index = 0; index < typed.Length; index++)
                    AddWord(unchecked((ulong)BitConverter.DoubleToInt64Bits(typed[index])));
                return;
            }
            if (typeof(T) == typeof(double?))
            {
                ref var first = ref MemoryMarshal.GetReference(values);
                var typed = MemoryMarshal.CreateReadOnlySpan(
                    ref Unsafe.As<T, double?>(ref first), values.Length);
                for (var index = 0; index < typed.Length; index++)
                    AddNullable(typed[index]);
                return;
            }
            if (typeof(T) == typeof(DateTime))
            {
                ref var first = ref MemoryMarshal.GetReference(values);
                var typed = MemoryMarshal.CreateReadOnlySpan(
                    ref Unsafe.As<T, DateTime>(ref first), values.Length);
                for (var index = 0; index < typed.Length; index++)
                    AddWord(unchecked((ulong)typed[index].Ticks));
                return;
            }
            if (typeof(T) == typeof(DateTime?))
            {
                ref var first = ref MemoryMarshal.GetReference(values);
                var typed = MemoryMarshal.CreateReadOnlySpan(
                    ref Unsafe.As<T, DateTime?>(ref first), values.Length);
                for (var index = 0; index < typed.Length; index++)
                    AddNullable(typed[index]);
                return;
            }

            for (var index = 0; index < values.Length; index++)
                AddValue(values[index]);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void AddBytes(ReadOnlySpan<byte> value)
            => AddWord(unchecked((uint)value.Length));

        public void AddString(string value)
            => AddWord(unchecked((uint)System.Text.Encoding.UTF8.GetByteCount(value)));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void AddNullable<T>(T? value)
            where T : struct
        {
            if (value.HasValue)
                AddValue(value.Value);
            else
                AddNull();
        }
    }

    public static PublishedReadResult Expected(PublishedBenchmarkDataSet dataSet)
    {
        var aggregate = Start();
        long valueCount = 0;
        for (var rowGroupIndex = 0; rowGroupIndex < dataSet.RowGroupCount; rowGroupIndex++)
            for (var columnIndex = 0; columnIndex < dataSet.Columns.Count; columnIndex++)
            {
                var column = dataSet.Columns[columnIndex];
                var values = column.Values[rowGroupIndex];
                var checksum = Accumulator.StartPiece(columnIndex, rowGroupIndex, values.Length);
                AddExpectedValues(ref checksum, column, rowGroupIndex);
                var piece = new PublishedReadResult(values.Length, checksum.Finish());
                aggregate = Combine(aggregate, piece);
                valueCount = checked(valueCount + values.Length);
            }
        return new PublishedReadResult(valueCount, aggregate);
    }

    public static ulong Start()
        => Seed;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong Combine(ulong aggregate, PublishedReadResult piece)
        => unchecked(aggregate + (ulong)piece.ValueCount + piece.Checksum);

    static void AddExpectedValues(ref Accumulator checksum, PublishedBenchmarkDataSet.Column column,
        int rowGroupIndex)
    {
        switch (column.Kind, column.Nullable)
        {
            case (BenchmarkColumnKind.Boolean, false):
                checksum.AddValues((bool[])column.Values[rowGroupIndex]);
                break;
            case (BenchmarkColumnKind.Boolean, true):
                checksum.AddValues((bool?[])column.Values[rowGroupIndex]);
                break;
            case (BenchmarkColumnKind.Int32, false):
                checksum.AddValues((int[])column.Values[rowGroupIndex]);
                break;
            case (BenchmarkColumnKind.Int32, true):
                checksum.AddValues((int?[])column.Values[rowGroupIndex]);
                break;
            case (BenchmarkColumnKind.Int64, false):
                checksum.AddValues((long[])column.Values[rowGroupIndex]);
                break;
            case (BenchmarkColumnKind.Int64, true):
                checksum.AddValues((long?[])column.Values[rowGroupIndex]);
                break;
            case (BenchmarkColumnKind.Timestamp, false):
                checksum.AddValues((DateTime[])column.Values[rowGroupIndex]);
                break;
            case (BenchmarkColumnKind.Timestamp, true):
                checksum.AddValues((DateTime?[])column.Values[rowGroupIndex]);
                break;
            case (BenchmarkColumnKind.Double, false):
                checksum.AddValues((double[])column.Values[rowGroupIndex]);
                break;
            case (BenchmarkColumnKind.Double, true):
                checksum.AddValues((double?[])column.Values[rowGroupIndex]);
                break;
            case (BenchmarkColumnKind.String, _):
                AddBinaryValues(ref checksum, (byte[]?[])(column.Utf8Values
                    ?? throw new InvalidOperationException($"Column '{column.Name}' has no UTF-8 values."))[
                        rowGroupIndex]);
                break;
            default:
                throw new NotSupportedException($"Unsupported checksum column '{column.Kind}'.");
        }
    }

    static void AddBinaryValues(ref Accumulator checksum, ReadOnlySpan<byte[]?> values)
    {
        for (var valueIndex = 0; valueIndex < values.Length; valueIndex++)
            if (values[valueIndex] is { } value)
                checksum.AddBytes(value);
            else
                checksum.AddNull();
    }
}
