using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace Plank.Benchmarks.Published;

/// <summary>
/// The order-sensitive hash every timed reader folds its decoded values into, so that no reader can
/// skip work the benchmark claims it did.
/// </summary>
/// <remarks>
/// The hash sits inside the timed region, so its own cost is charged to every implementation equally
/// — but only up to a point. A single FNV chain is one multiply deep per word and each multiply waits
/// on the last, so hashing 24M values used to cost more than a plain int32 column took to decode, and
/// the decoders it was meant to compare all measured the same. <see cref="Accumulator"/> keeps the
/// same per-value discrimination and spreads it across four lanes, so four multiplies are in flight at
/// once and the hash stops being the thing under measurement.
/// </remarks>
static class PublishedReadFingerprint
{
    const ulong Offset = 14695981039346656037UL;
    const ulong Prime = 1099511628211UL;

    /// <summary>
    /// A second odd multiplier, used only for absent values. Distinguishing null from present by which
    /// constant mixes it — rather than by an extra round — keeps a null and a value apart without
    /// paying for two multiplies on every value.
    /// </summary>
    const ulong AbsentPrime = 11400714819323198485UL;

    /// <summary>
    /// Four independent FNV lanes fed round-robin. A value's lane is decided by its position in the
    /// piece, so a reader that returns its values in differently sized chunks still lands on the same
    /// fingerprint, while reordering values across any distance still changes it.
    /// </summary>
    public struct Accumulator
    {
        ulong _lane0;
        ulong _lane1;
        ulong _lane2;
        ulong _lane3;

        public static Accumulator StartPiece(int columnIndex, int rowGroupIndex, int valueCount)
        {
            var accumulator = new Accumulator
            {
                _lane0 = Offset,
                _lane1 = Offset,
                _lane2 = Offset,
                _lane3 = Offset
            };
            accumulator.AddWord(unchecked((uint)columnIndex));
            accumulator.AddWord(unchecked((uint)rowGroupIndex));
            accumulator.AddWord(unchecked((uint)valueCount));
            return accumulator;
        }

        public ulong Finish()
        {
            var hash = Mix(_lane0, _lane1, Prime);
            hash = Mix(hash, _lane2, Prime);
            return Mix(hash, _lane3, Prime);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void AddWord(ulong value)
            => Rotate(Mix(_lane3, value, Prime));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void AddNull()
            => Rotate(Mix(_lane3, 0, AbsentPrime));

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
                throw new NotSupportedException($"Unsupported fingerprint value '{typeof(T)}'.");
        }

        public void AddValues<T>(ReadOnlySpan<T> values)
        {
            if (typeof(T) == typeof(int))
            {
                ref var first = ref MemoryMarshal.GetReference(values);
                AddInt32Values(MemoryMarshal.CreateReadOnlySpan(
                    ref Unsafe.As<T, int>(ref first), values.Length));
                return;
            }
            if (typeof(T) == typeof(int?))
            {
                ref var first = ref MemoryMarshal.GetReference(values);
                AddNullableInt32Values(MemoryMarshal.CreateReadOnlySpan(
                    ref Unsafe.As<T, int?>(ref first), values.Length));
                return;
            }
            if (typeof(T) == typeof(long))
            {
                ref var first = ref MemoryMarshal.GetReference(values);
                AddInt64Values(MemoryMarshal.CreateReadOnlySpan(
                    ref Unsafe.As<T, long>(ref first), values.Length));
                return;
            }
            if (typeof(T) == typeof(long?))
            {
                ref var first = ref MemoryMarshal.GetReference(values);
                AddNullableInt64Values(MemoryMarshal.CreateReadOnlySpan(
                    ref Unsafe.As<T, long?>(ref first), values.Length));
                return;
            }

            for (var index = 0; index < values.Length; index++)
                AddValue(values[index]);
        }

        public void AddBytes(ReadOnlySpan<byte> value)
        {
            AddWord(unchecked((uint)value.Length));
            AddByteValues(value);
        }

        public void AddString(string value)
        {
            var maximumByteCount = Encoding.UTF8.GetMaxByteCount(value.Length);
            byte[]? rented = null;
            Span<byte> bytes = maximumByteCount <= 256
                ? stackalloc byte[maximumByteCount]
                : (rented = ArrayPool<byte>.Shared.Rent(maximumByteCount));
            try
            {
                var byteCount = Encoding.UTF8.GetBytes(value, bytes);
                AddBytes(bytes[..byteCount]);
            }
            finally
            {
                if (rented is not null)
                    ArrayPool<byte>.Shared.Return(rented);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void AddNullable<T>(T? value)
            where T : struct
        {
            if (value.HasValue)
                AddValue(value.Value);
            else
                AddNull();
        }

        /// <summary>
        /// Shifts the freshly mixed lane to the front. The next value therefore mixes into the lane
        /// four positions back, which is what leaves the multiplies room to overlap.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void Rotate(ulong next)
        {
            _lane3 = _lane2;
            _lane2 = _lane1;
            _lane1 = _lane0;
            _lane0 = next;
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        void AddInt32Values(ReadOnlySpan<int> values)
        {
            ref var first = ref MemoryMarshal.GetReference(values);
            var index = 0;
            for (; index <= values.Length - 4; index += 4)
            {
                // Four Rotate calls restore the fields to their starting positions. Update each lane
                // directly with the value it would have received after those rotations. This is exact
                // from every starting position, so callers may split the same sequence into any chunks.
                _lane0 = Mix(_lane0, unchecked((uint)Unsafe.Add(ref first, index + 3)), Prime);
                _lane1 = Mix(_lane1, unchecked((uint)Unsafe.Add(ref first, index + 2)), Prime);
                _lane2 = Mix(_lane2, unchecked((uint)Unsafe.Add(ref first, index + 1)), Prime);
                _lane3 = Mix(_lane3, unchecked((uint)Unsafe.Add(ref first, index)), Prime);
            }

            for (; index < values.Length; index++)
                AddWord(unchecked((uint)Unsafe.Add(ref first, index)));
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        void AddNullableInt32Values(ReadOnlySpan<int?> values)
        {
            ref var first = ref MemoryMarshal.GetReference(values);
            var index = 0;
            for (; index <= values.Length - 4; index += 4)
            {
                _lane0 = MixNullableInt32(_lane0, Unsafe.Add(ref first, index + 3));
                _lane1 = MixNullableInt32(_lane1, Unsafe.Add(ref first, index + 2));
                _lane2 = MixNullableInt32(_lane2, Unsafe.Add(ref first, index + 1));
                _lane3 = MixNullableInt32(_lane3, Unsafe.Add(ref first, index));
            }

            for (; index < values.Length; index++)
                AddValue(Unsafe.Add(ref first, index));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static ulong MixNullableInt32(ulong lane, int? value)
            => value.HasValue
                ? Mix(lane, unchecked((uint)value.GetValueOrDefault()), Prime)
                : Mix(lane, 0, AbsentPrime);

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        void AddInt64Values(ReadOnlySpan<long> values)
        {
            ref var first = ref MemoryMarshal.GetReference(values);
            var index = 0;
            for (; index <= values.Length - 4; index += 4)
            {
                _lane0 = Mix(_lane0, unchecked((ulong)Unsafe.Add(ref first, index + 3)), Prime);
                _lane1 = Mix(_lane1, unchecked((ulong)Unsafe.Add(ref first, index + 2)), Prime);
                _lane2 = Mix(_lane2, unchecked((ulong)Unsafe.Add(ref first, index + 1)), Prime);
                _lane3 = Mix(_lane3, unchecked((ulong)Unsafe.Add(ref first, index)), Prime);
            }

            for (; index < values.Length; index++)
                AddWord(unchecked((ulong)Unsafe.Add(ref first, index)));
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        void AddNullableInt64Values(ReadOnlySpan<long?> values)
        {
            ref var first = ref MemoryMarshal.GetReference(values);
            var index = 0;
            for (; index <= values.Length - 4; index += 4)
            {
                _lane0 = MixNullableInt64(_lane0, Unsafe.Add(ref first, index + 3));
                _lane1 = MixNullableInt64(_lane1, Unsafe.Add(ref first, index + 2));
                _lane2 = MixNullableInt64(_lane2, Unsafe.Add(ref first, index + 1));
                _lane3 = MixNullableInt64(_lane3, Unsafe.Add(ref first, index));
            }

            for (; index < values.Length; index++)
                AddValue(Unsafe.Add(ref first, index));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static ulong MixNullableInt64(ulong lane, long? value)
            => value.HasValue
                ? Mix(lane, unchecked((ulong)value.GetValueOrDefault()), Prime)
                : Mix(lane, 0, AbsentPrime);

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        void AddByteValues(ReadOnlySpan<byte> values)
        {
            ref var first = ref MemoryMarshal.GetReference(values);
            var index = 0;
            for (; index <= values.Length - 4; index += 4)
            {
                _lane0 = Mix(_lane0, Unsafe.Add(ref first, index + 3), Prime);
                _lane1 = Mix(_lane1, Unsafe.Add(ref first, index + 2), Prime);
                _lane2 = Mix(_lane2, Unsafe.Add(ref first, index + 1), Prime);
                _lane3 = Mix(_lane3, Unsafe.Add(ref first, index), Prime);
            }

            for (; index < values.Length; index++)
                AddWord(Unsafe.Add(ref first, index));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static ulong Mix(ulong lane, ulong value, ulong multiplier)
        {
            lane ^= value;
            lane *= multiplier;
            return lane ^ (lane >> 29);
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
                var fingerprint = Accumulator.StartPiece(columnIndex, rowGroupIndex, values.Length);
                AddExpectedValues(ref fingerprint, column, rowGroupIndex);
                var piece = new PublishedReadResult(values.Length, fingerprint.Finish());
                aggregate = Combine(aggregate, piece);
                valueCount = checked(valueCount + values.Length);
            }
        return new PublishedReadResult(valueCount, aggregate);
    }

    public static ulong Start()
        => Offset;

    /// <summary>
    /// Folds one column piece into the run-wide aggregate. This runs once per column per row group, so
    /// it stays on the plain chain.
    /// </summary>
    public static ulong Combine(ulong aggregate, PublishedReadResult piece)
    {
        aggregate = AddUInt64(aggregate, unchecked((ulong)piece.ValueCount));
        return AddUInt64(aggregate, piece.Fingerprint);
    }

    static void AddExpectedValues(ref Accumulator fingerprint, PublishedBenchmarkDataSet.Column column,
        int rowGroupIndex)
    {
        switch (column.Kind, column.Nullable)
        {
            case (BenchmarkColumnKind.Boolean, false):
                AddValues(ref fingerprint, (bool[])column.Values[rowGroupIndex]);
                break;
            case (BenchmarkColumnKind.Boolean, true):
                AddValues(ref fingerprint, (bool?[])column.Values[rowGroupIndex]);
                break;
            case (BenchmarkColumnKind.Int32, false):
                AddValues(ref fingerprint, (int[])column.Values[rowGroupIndex]);
                break;
            case (BenchmarkColumnKind.Int32, true):
                AddValues(ref fingerprint, (int?[])column.Values[rowGroupIndex]);
                break;
            case (BenchmarkColumnKind.Int64, false):
                AddValues(ref fingerprint, (long[])column.Values[rowGroupIndex]);
                break;
            case (BenchmarkColumnKind.Int64, true):
                AddValues(ref fingerprint, (long?[])column.Values[rowGroupIndex]);
                break;
            case (BenchmarkColumnKind.Double, false):
                AddValues(ref fingerprint, (double[])column.Values[rowGroupIndex]);
                break;
            case (BenchmarkColumnKind.Double, true):
                AddValues(ref fingerprint, (double?[])column.Values[rowGroupIndex]);
                break;
            case (BenchmarkColumnKind.Timestamp, false):
                AddValues(ref fingerprint, (DateTime[])column.Values[rowGroupIndex]);
                break;
            case (BenchmarkColumnKind.Timestamp, true):
                AddValues(ref fingerprint, (DateTime?[])column.Values[rowGroupIndex]);
                break;
            case (BenchmarkColumnKind.String, _):
                AddBinaryValues(ref fingerprint, (byte[]?[])(column.Utf8Values
                    ?? throw new InvalidOperationException($"Column '{column.Name}' has no UTF-8 values."))[
                        rowGroupIndex]);
                break;
            default:
                throw new NotSupportedException($"Unsupported column kind '{column.Kind}'.");
        }
    }

    static void AddValues<T>(ref Accumulator fingerprint, ReadOnlySpan<T> values)
        => fingerprint.AddValues(values);

    static void AddBinaryValues(ref Accumulator fingerprint, ReadOnlySpan<byte[]?> values)
    {
        for (var valueIndex = 0; valueIndex < values.Length; valueIndex++)
            if (values[valueIndex] is { } value)
                fingerprint.AddBytes(value);
            else
                fingerprint.AddNull();
    }

    static ulong AddUInt64(ulong hash, ulong value)
    {
        for (var shift = 0; shift < 64; shift += 8)
        {
            hash ^= (byte)(value >> shift);
            hash *= Prime;
        }
        return hash;
    }
}
