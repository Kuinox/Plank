using System.Numerics;
using System.Runtime.Intrinsics;
using Plank.Writing;
using Plank.Writing.Encoding;

namespace Plank.Tests.Writer;

/// <summary>
/// Every integer min/max scan goes through one fused vector pass, so these check it at the lengths
/// where its structure changes: below one vector, exactly one, inside the unrolled body, and the
/// lengths in between where the preloaded first and last vectors overlap the loop.
/// </summary>
internal sealed class ColumnStatisticsMinMaxTests
{
    const int MaxLength = 300;

    [Test]
    public void ByteStatisticsMatchAPlainScanAtEveryLength()
    {
        var random = new Random(20260817);
        for (var length = 1; length <= MaxLength; length++)
        {
            var values = new byte[length];
            random.NextBytes(values);
            var statistics = ColumnStatistics.CreateByte(values, 0);

            byte min = values[0], max = values[0];
            foreach (var value in values)
            {
                min = Math.Min(min, value);
                max = Math.Max(max, value);
            }

            AssertInt32Range(statistics, min, max, length, "byte");
        }
    }

    [Test]
    public void UInt16StatisticsMatchAPlainScanAtEveryLength()
    {
        var random = new Random(1);
        for (var length = 1; length <= MaxLength; length++)
        {
            var values = new ushort[length];
            for (var i = 0; i < length; i++)
                values[i] = (ushort)random.Next(ushort.MinValue, ushort.MaxValue + 1);
            var statistics = ColumnStatistics.CreateUInt16(values, 0);

            ushort min = values[0], max = values[0];
            foreach (var value in values)
            {
                min = Math.Min(min, value);
                max = Math.Max(max, value);
            }

            AssertInt32Range(statistics, min, max, length, "ushort");
        }
    }

    [Test]
    public void Int32StatisticsMatchAPlainScanAtEveryLength()
    {
        var random = new Random(2);
        for (var length = 1; length <= MaxLength; length++)
        {
            var values = new int[length];
            for (var i = 0; i < length; i++)
                values[i] = random.Next(int.MinValue, int.MaxValue);

            if (!ColumnStatistics.TryGetInt32MinMax(values, out var actualMin, out var actualMax))
                throw new InvalidOperationException($"int32 length {length} reported no range.");

            int min = values[0], max = values[0];
            foreach (var value in values)
            {
                min = Math.Min(min, value);
                max = Math.Max(max, value);
            }

            if (actualMin != min || actualMax != max)
                throw new InvalidOperationException(
                    $"int32 length {length}: expected [{min}, {max}], got [{actualMin}, {actualMax}].");
        }
    }

    [Test]
    public void Int64StatisticsMatchAPlainScanAtEveryLength()
    {
        var random = new Random(3);
        for (var length = 1; length <= MaxLength; length++)
        {
            var values = new long[length];
            for (var i = 0; i < length; i++)
                values[i] = random.NextInt64();
            var statistics = ColumnStatistics.Create(
                new Plank.Schema.Column("value", Plank.Schema.ParquetPhysicalType.Int64), (ReadOnlySpan<long>)values, 0);

            long min = values[0], max = values[0];
            foreach (var value in values)
            {
                min = Math.Min(min, value);
                max = Math.Max(max, value);
            }

            if (statistics.MinBits != min || statistics.MaxBits != max)
                throw new InvalidOperationException(
                    $"int64 length {length}: expected [{min}, {max}], got "
                    + $"[{statistics.MinBits}, {statistics.MaxBits}].");
        }
    }

    [Test]
    public void UInt64StatisticsMatchAPlainScanAtEveryLength()
    {
        var random = new Random(4);
        for (var length = 1; length <= MaxLength; length++)
        {
            var values = new ulong[length];
            for (var i = 0; i < length; i++)
                values[i] = (ulong)random.NextInt64();
            var statistics = ColumnStatistics.CreateUInt64(values, 0);

            ulong min = values[0], max = values[0];
            foreach (var value in values)
            {
                min = Math.Min(min, value);
                max = Math.Max(max, value);
            }

            if ((ulong)statistics.MinBits != min || (ulong)statistics.MaxBits != max)
                throw new InvalidOperationException(
                    $"uint64 length {length}: expected [{min}, {max}], got "
                    + $"[{(ulong)statistics.MinBits}, {(ulong)statistics.MaxBits}].");
        }
    }

    /// <summary>
    /// Values above the signed range are where an unsigned scan and a signed one disagree, so both
    /// extremes appear in one span.
    /// </summary>
    [Test]
    public void UnsignedStatisticsOrderValuesAboveTheSignedRange()
    {
        uint[] wide = [1, uint.MaxValue, 0, (uint)int.MaxValue + 1, 7, uint.MaxValue - 1];
        var uint32 = ColumnStatistics.CreateUInt32(wide, 0);
        if ((uint)uint32.MinBits != 0 || (uint)uint32.MaxBits != uint.MaxValue)
            throw new InvalidOperationException(
                $"Expected [0, {uint.MaxValue}], got [{(uint)uint32.MinBits}, {(uint)uint32.MaxBits}].");

        ulong[] wider = [1, ulong.MaxValue, 0, (ulong)long.MaxValue + 1, 7, ulong.MaxValue - 1];
        var uint64 = ColumnStatistics.CreateUInt64(wider, 0);
        if ((ulong)uint64.MinBits != 0 || (ulong)uint64.MaxBits != ulong.MaxValue)
            throw new InvalidOperationException(
                $"Expected [0, {ulong.MaxValue}], got [{(ulong)uint64.MinBits}, {(ulong)uint64.MaxBits}].");
    }

    [Test]
    public void DirectScansAndFusedCopiesMatchAtEveryVectorBoundary()
    {
        int[] lengths = [1, 2, 3, 4, 5, 7, 8, 9, 15, 16, 17, 31, 32, 33, 63, 64, 65,
            127, 128, 129, 255, 256, 257, 300, 1024];

        foreach (var length in lengths)
        {
            VerifyScanAndCopy(CreateValues(length, i => (byte)(i * 73 + length), byte.MinValue, byte.MaxValue));
            VerifyScanAndCopy(CreateValues(length, i => (ushort)(i * 4051 + length), ushort.MinValue,
                ushort.MaxValue));
            VerifyScanAndCopy(CreateValues(length, i => unchecked((int)((uint)i * 2654435761U + (uint)length)),
                int.MinValue, int.MaxValue));
            VerifyScanAndCopy(CreateValues(length, i => (uint)i * 2654435761U + (uint)length, uint.MinValue,
                uint.MaxValue));
            VerifyScanAndCopy(CreateValues(length,
                i => unchecked((long)((ulong)i * 11400714819323198485UL + (ulong)length)), long.MinValue,
                long.MaxValue));
            VerifyScanAndCopy(CreateValues(length,
                i => (ulong)i * 11400714819323198485UL + (ulong)length, ulong.MinValue, ulong.MaxValue));
        }
    }

    [Test]
    public void Vector128PlainPageWritersMatchExistingOutputAtEveryBoundary()
    {
        int[] lengths = [1, 2, 3, 4, 5, 7, 8, 9, 15, 16, 17, 31, 32, 33, 63, 64, 65,
            127, 128, 129, 255, 256, 257, 300, 1024];

        foreach (var length in lengths)
        {
            VerifyPlainWriters(CreateValues(length,
                i => unchecked((int)((uint)i * 2654435761U + (uint)length)), int.MinValue, int.MaxValue));
            VerifyPlainWriters(CreateValues(length,
                i => unchecked((long)((ulong)i * 11400714819323198485UL + (ulong)length)), long.MinValue,
                long.MaxValue));
        }
    }

    /// <summary>
    /// The scan seeds its accumulators from the first and last vectors, so an extreme sitting only in
    /// the overlap — or only in the very last element — still has to come out.
    /// </summary>
    [Test]
    public void ExtremesAtEitherEndSurvive()
    {
        for (var length = 1; length <= MaxLength; length++)
        {
            var first = new int[length];
            Array.Fill(first, 5);
            first[0] = -99;
            if (!ColumnStatistics.TryGetInt32MinMax(first, out var min, out var max) || min != -99
                || max != (length == 1 ? -99 : 5))
                throw new InvalidOperationException($"Leading extreme lost at length {length}.");

            var last = new int[length];
            Array.Fill(last, 5);
            last[^1] = 99;
            if (!ColumnStatistics.TryGetInt32MinMax(last, out min, out max)
                || max != 99 || min != (length == 1 ? 99 : 5))
                throw new InvalidOperationException($"Trailing extreme lost at length {length}.");
        }
    }

    [Test]
    public void EmptySpansProduceNoRange()
    {
        foreach (var statistics in (ReadOnlySpan<ColumnStatistics>)[
                     ColumnStatistics.CreateByte([], 3), ColumnStatistics.CreateUInt16([], 3),
                     ColumnStatistics.CreateUInt32([], 3), ColumnStatistics.CreateUInt64([], 3)])
            if (statistics.ValueKind != ColumnStatistics.ColumnStatisticsValueKind.None)
                throw new InvalidOperationException(
                    $"An empty span must not report a min or max, got {statistics.ValueKind}.");

        if (ColumnStatistics.TryGetInt32MinMax([], out _, out _))
            throw new InvalidOperationException("An empty int32 span must not report a range.");
    }

    static void AssertInt32Range(ColumnStatistics statistics, int min, int max, int length, string type)
    {
        if ((int)statistics.MinBits != min || (int)statistics.MaxBits != max)
            throw new InvalidOperationException(
                $"{type} length {length}: expected [{min}, {max}], got "
                + $"[{(int)statistics.MinBits}, {(int)statistics.MaxBits}].");
    }

    static T[] CreateValues<T>(int length, Func<int, T> valueFactory, T minimum, T maximum)
    {
        var values = new T[length];
        for (var i = 0; i < length; i++)
            values[i] = valueFactory(i);
        if (length > 1)
        {
            values[length / 3] = maximum;
            values[^1] = minimum;
        }
        return values;
    }

    static void VerifyScanAndCopy<T>(T[] values)
        where T : struct, INumber<T>
    {
        T expectedMin = values[0], expectedMax = values[0];
        foreach (var value in values)
        {
            if (value < expectedMin)
                expectedMin = value;
            if (value > expectedMax)
                expectedMax = value;
        }

        MinMaxScan.Compute(values, out var computedMin, out var computedMax);
        if (computedMin != expectedMin || computedMax != expectedMax)
            throw new InvalidOperationException(
                $"{typeof(T).Name} compute length {values.Length}: expected [{expectedMin}, {expectedMax}], "
                + $"got [{computedMin}, {computedMax}].");

        var destination = new T[values.Length + 3];
        MinMaxScan.CopyAndCompute(values, destination, out var copiedMin, out var copiedMax);
        if (!values.AsSpan().SequenceEqual(destination.AsSpan(0, values.Length)))
            throw new InvalidOperationException(
                $"{typeof(T).Name} copy length {values.Length}: destination differs from source.");
        if (copiedMin != expectedMin || copiedMax != expectedMax)
            throw new InvalidOperationException(
                $"{typeof(T).Name} copy length {values.Length}: expected [{expectedMin}, {expectedMax}], "
                + $"got [{copiedMin}, {copiedMax}].");

        if (values.Length < Vector128<T>.Count * 4)
            return;

        MinMaxScan.ComputeVector128Bulk(values, out var portableMin, out var portableMax);
        if (portableMin != expectedMin || portableMax != expectedMax)
            throw new InvalidOperationException(
                $"{typeof(T).Name} portable compute length {values.Length}: expected [{expectedMin}, "
                + $"{expectedMax}], got [{portableMin}, {portableMax}].");

        destination.AsSpan().Clear();
        MinMaxScan.CopyAndComputeVector128Bulk(values, destination, out portableMin, out portableMax);
        if (!values.AsSpan().SequenceEqual(destination.AsSpan(0, values.Length)))
            throw new InvalidOperationException(
                $"{typeof(T).Name} portable copy length {values.Length}: destination differs from source.");
        if (portableMin != expectedMin || portableMax != expectedMax)
            throw new InvalidOperationException(
                $"{typeof(T).Name} portable copy length {values.Length}: expected [{expectedMin}, {expectedMax}], "
                + $"got [{portableMin}, {portableMax}].");
    }

    static void VerifyPlainWriters(int[] values)
    {
        var capacity = checked((uint)(values.Length * sizeof(int) + 64));
        var baselineWriter = new BufferWriter(DefaultParquetBufferPool.Shared, capacity, capacity);
        var vectorWriter = new BufferWriter(DefaultParquetBufferPool.Shared, capacity, capacity);
        try
        {
            var baseline = PlainEncoding.WriteInt32PageWithStatistics(values, ref baselineWriter);
            var vector = PlainEncoding.WriteInt32PageWithVector128Statistics(values, ref vectorWriter);
            AssertPlainWriterParity(values.Length, baseline, vector, baselineWriter, vectorWriter);
        }
        finally
        {
            baselineWriter.Dispose();
            vectorWriter.Dispose();
        }
    }

    static void VerifyPlainWriters(long[] values)
    {
        var capacity = checked((uint)(values.Length * sizeof(long) + 64));
        var baselineWriter = new BufferWriter(DefaultParquetBufferPool.Shared, capacity, capacity);
        var vectorWriter = new BufferWriter(DefaultParquetBufferPool.Shared, capacity, capacity);
        try
        {
            var baseline = PlainEncoding.WriteInt64PageWithStatistics(values, ref baselineWriter);
            var vector = PlainEncoding.WriteInt64PageWithVector128Statistics(values, ref vectorWriter);
            AssertPlainWriterParity(values.Length, baseline, vector, baselineWriter, vectorWriter);
        }
        finally
        {
            baselineWriter.Dispose();
            vectorWriter.Dispose();
        }
    }

    static void AssertPlainWriterParity(int length, ColumnStatistics baseline, ColumnStatistics vector,
        BufferWriter baselineWriter, BufferWriter vectorWriter)
    {
        if (baseline.MinBits != vector.MinBits || baseline.MaxBits != vector.MaxBits
            || baseline.NullCount != vector.NullCount)
            throw new InvalidOperationException(
                $"PLAIN length {length}: baseline [{baseline.MinBits}, {baseline.MaxBits}] differs from "
                + $"Vector128 [{vector.MinBits}, {vector.MaxBits}].");
        if (!baselineWriter.TryGetSingleWrittenSpan(out var baselineBytes)
            || !vectorWriter.TryGetSingleWrittenSpan(out var vectorBytes)
            || !baselineBytes.SequenceEqual(vectorBytes))
            throw new InvalidOperationException($"PLAIN length {length}: Vector128 output bytes differ.");
    }
}
