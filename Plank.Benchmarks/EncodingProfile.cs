using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Plank.Schema;
using Plank.Writing;
using Plank.Writing.Encoding;

namespace Plank.Benchmarks;

/// <summary>
/// Throughput profile for every encoder entry point, measured at three working-set sizes so the
/// cache-resident cost and the DRAM-resident cost of each algorithm can be compared against a
/// <c>memcpy</c> roofline measured on the same machine.
///
/// This exists because hardware performance counters (branch mispredictions, cache misses) are not
/// available in every environment Plank is benchmarked in - most CI containers and KVM guests expose
/// no PMU. Comparing an encoder against the copy roofline at L1, L2/L3 and DRAM sizes recovers the
/// same conclusions: a kernel that keeps pace with <c>memcpy</c> at every size is bandwidth-bound and
/// has nothing left to win, while one that degrades relative to the roofline as the working set grows
/// is paying for its access pattern, and one that is far off the roofline even in L1 is compute or
/// branch bound.
///
/// Run with: dotnet run -c Release --project Plank.Benchmarks -- --encoding-profile
/// </summary>
static class EncodingProfile
{
    const int MeasurementRounds = 9;
    const double TargetBatchMilliseconds = 25;
    const double WarmupMilliseconds = 150;

    static readonly int[] FixedWidthValueCounts = [4_096, 262_144, 4_194_304];
    static readonly int[] ByteArrayValueCounts = [4_096, 262_144, 1_048_576];

    static readonly Column BooleanColumn = new("value", ParquetPhysicalType.Boolean);
    static readonly Column Int32Column = new("value", ParquetPhysicalType.Int32);
    static readonly Column Int64Column = new("value", ParquetPhysicalType.Int64);
    static readonly Column FloatColumn = new("value", ParquetPhysicalType.Float);
    static readonly Column DoubleColumn = new("value", ParquetPhysicalType.Double);
    static readonly Column ByteArrayColumn = new("value", ParquetPhysicalType.ByteArray);
    static readonly Column FixedLength16Column = new("value", ParquetPhysicalType.FixedLenByteArray,
        new ColumnOptions(typeLength: 16));

    sealed record Result(
        string Encoder,
        string Variant,
        string Input,
        int ValueCount,
        long InputBytes,
        long OutputBytes,
        double NanosecondsPerCall)
    {
        public double NanosecondsPerValue
            => NanosecondsPerCall / ValueCount;

        public double InputGigabytesPerSecond
            => InputBytes / NanosecondsPerCall;

        public double OutputGigabytesPerSecond
            => OutputBytes / NanosecondsPerCall;
    }

    internal static void Run()
    {
        var results = new List<Result>();
        var rooflines = new Dictionary<int, double>();

        foreach (var valueCount in FixedWidthValueCounts)
        {
            rooflines[valueCount] = MeasureCopyRoofline(valueCount * sizeof(long));
            MeasureFixedWidth(valueCount, results);
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        foreach (var valueCount in ByteArrayValueCounts)
        {
            MeasureByteArrays(valueCount, results);
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        foreach (var valueCount in FixedWidthValueCounts)
        {
            MeasureDefinitionLevels(valueCount, results);
            MeasureStatistics(valueCount, results);
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        WriteReport(results, rooflines);
    }

    static void MeasureFixedWidth(int valueCount, List<Result> results)
    {
        var random = new Random(42);

        var alternatingBooleans = new bool[valueCount];
        var constantBooleans = new bool[valueCount];
        var randomBooleans = new bool[valueCount];
        for (var i = 0; i < valueCount; i++)
        {
            alternatingBooleans[i] = (i & 1) == 0;
            constantBooleans[i] = true;
            randomBooleans[i] = random.Next(2) != 0;
        }

        var randomInt32 = new int[valueCount];
        random.NextBytes(MemoryMarshal.AsBytes(randomInt32.AsSpan()));
        var randomInt64 = new long[valueCount];
        random.NextBytes(MemoryMarshal.AsBytes(randomInt64.AsSpan()));
        var randomFloats = new float[valueCount];
        random.NextBytes(MemoryMarshal.AsBytes(randomFloats.AsSpan()));
        var randomDoubles = new double[valueCount];
        random.NextBytes(MemoryMarshal.AsBytes(randomDoubles.AsSpan()));
        var randomBytes = new byte[valueCount];
        random.NextBytes(randomBytes);
        var randomUInt16 = new ushort[valueCount];
        random.NextBytes(MemoryMarshal.AsBytes(randomUInt16.AsSpan()));

        var constantDeltaInt32 = new int[valueCount];
        var timestampDeltaInt32 = new int[valueCount];
        var constantDeltaInt64 = new long[valueCount];
        var timestampDeltaInt64 = new long[valueCount];
        for (var i = 0; i < valueCount; i++)
        {
            constantDeltaInt32[i] = i * 7;
            timestampDeltaInt32[i] = i * 3_000 + i % 7 * 1_000;
            constantDeltaInt64[i] = i * 7L;
            timestampDeltaInt64[i] = i * 3_000L + i % 7 * 1_000L;
        }

        var dictionaryIndexes11Bit = new int[valueCount];
        var dictionaryIndexes8Bit = new int[valueCount];
        var dictionaryIndexes16Bit = new int[valueCount];
        var dictionaryIndexes1Bit = new int[valueCount];
        var dictionaryIndexesRuns = new int[valueCount];
        var runValue = 0;
        var runRemaining = 0;
        for (var i = 0; i < valueCount; i++)
        {
            dictionaryIndexes11Bit[i] = random.Next(2048);
            dictionaryIndexes8Bit[i] = random.Next(256);
            dictionaryIndexes16Bit[i] = random.Next(65536);
            dictionaryIndexes1Bit[i] = i & 1;
            if (runRemaining == 0)
            {
                runValue = random.Next(256);
                runRemaining = random.Next(64, 512);
            }

            dictionaryIndexesRuns[i] = runValue;
            runRemaining--;
        }

        var writer = CreateWriter(checked((long)valueCount * 16 + 4096));
        try
        {
            var localWriter = writer;

            void Add(string encoder, string variant, string input, long inputBytes, RunEncoder run)
            {
                var result = Measure(encoder, variant, input, valueCount, inputBytes, ref localWriter, run);
                results.Add(result);
                Console.Error.WriteLine(
                    $"  {result.Encoder,-22} {result.Variant,-26} {result.Input,-22} n={valueCount,-9} " +
                    $"{result.NanosecondsPerValue,7:F3} ns/value  {result.InputGigabytesPerSecond,6:F2} GB/s");
            }

            Add("plain", "bitpack-simd", "bool alternating", valueCount,
                (ref BufferWriter w) => PlainEncoding.WriteValues(BooleanColumn, alternatingBooleans, ref w));
            Add("plain", "bitpack-simd", "bool random", valueCount,
                (ref BufferWriter w) => PlainEncoding.WriteValues(BooleanColumn, randomBooleans, ref w));
            Add("plain", "bitpack-simd", "bool all-true", valueCount,
                (ref BufferWriter w) => PlainEncoding.WriteValues(BooleanColumn, constantBooleans, ref w));
            Add("plain", "memcpy", "int32 random", (long)valueCount * sizeof(int),
                (ref BufferWriter w) => PlainEncoding.WriteValues(Int32Column, randomInt32, ref w));
            Add("plain", "memcpy", "int64 random", (long)valueCount * sizeof(long),
                (ref BufferWriter w) => PlainEncoding.WriteValues(Int64Column, randomInt64, ref w));
            Add("plain", "memcpy", "float random", (long)valueCount * sizeof(float),
                (ref BufferWriter w) => PlainEncoding.WriteValues(FloatColumn, randomFloats, ref w));
            Add("plain", "memcpy", "double random", (long)valueCount * sizeof(double),
                (ref BufferWriter w) => PlainEncoding.WriteValues(DoubleColumn, randomDoubles, ref w));
            Add("plain", "widen-byte-simd", "byte->int32", valueCount,
                (ref BufferWriter w) => PlainEncoding.WriteValues(Int32Column, randomBytes, ref w));
            Add("plain", "widen-uint16-simd", "ushort->int32", (long)valueCount * sizeof(ushort),
                (ref BufferWriter w) => PlainEncoding.WriteValues(Int32Column, randomUInt16, ref w));

            Add("byte_stream_split", "uint32-lanes-avx512", "int32 random", (long)valueCount * sizeof(int),
                (ref BufferWriter w) => ByteStreamSplitEncoding.WriteValues(Int32Column, randomInt32, ref w));
            Add("byte_stream_split", "uint64-lanes-avx512", "int64 random", (long)valueCount * sizeof(long),
                (ref BufferWriter w) => ByteStreamSplitEncoding.WriteValues(Int64Column, randomInt64, ref w));
            Add("byte_stream_split", "uint32-lanes-avx512", "float random", (long)valueCount * sizeof(float),
                (ref BufferWriter w) => ByteStreamSplitEncoding.WriteValues(FloatColumn, randomFloats, ref w));
            Add("byte_stream_split", "uint64-lanes-avx512", "double random", (long)valueCount * sizeof(double),
                (ref BufferWriter w) => ByteStreamSplitEncoding.WriteValues(DoubleColumn, randomDoubles, ref w));
            // Same 64-bit lane kernel, swapped element type and swapped source array: separates a codegen
            // difference between the int64 and double entry points from a source-buffer alignment effect.
            Add("byte_stream_split", "uint64-lanes-avx512", "int64 array as double", (long)valueCount * sizeof(long),
                (ref BufferWriter w) => ByteStreamSplitEncoding.WriteValues(DoubleColumn,
                    (ReadOnlySpan<double>)MemoryMarshal.Cast<long, double>(randomInt64), ref w));
            Add("byte_stream_split", "uint64-lanes-avx512", "double array as int64", (long)valueCount * sizeof(long),
                (ref BufferWriter w) => ByteStreamSplitEncoding.WriteValues(Int64Column,
                    (ReadOnlySpan<long>)MemoryMarshal.Cast<double, long>(randomDoubles), ref w));
            Add("byte_stream_split", "scalar-lanes", "byte->int32", valueCount,
                (ref BufferWriter w) => ByteStreamSplitEncoding.WriteValues(Int32Column, randomBytes, ref w));
            Add("byte_stream_split", "scalar-lanes", "ushort->int32", (long)valueCount * sizeof(ushort),
                (ref BufferWriter w) => ByteStreamSplitEncoding.WriteValues(Int32Column, randomUInt16, ref w));

            Add("delta_binary_packed", "pack-narrow", "int32 constant-delta", (long)valueCount * sizeof(int),
                (ref BufferWriter w) => DeltaBinaryPackedEncoding.WriteInt32(constantDeltaInt32, ref w));
            Add("delta_binary_packed", "pack-13bit", "int32 timestamp-like", (long)valueCount * sizeof(int),
                (ref BufferWriter w) => DeltaBinaryPackedEncoding.WriteInt32(timestampDeltaInt32, ref w));
            Add("delta_binary_packed", "pack-generic", "int32 random", (long)valueCount * sizeof(int),
                (ref BufferWriter w) => DeltaBinaryPackedEncoding.WriteInt32(randomInt32, ref w));
            Add("delta_binary_packed", "pack-narrow", "int64 constant-delta", (long)valueCount * sizeof(long),
                (ref BufferWriter w) => DeltaBinaryPackedEncoding.WriteInt64(constantDeltaInt64, ref w));
            Add("delta_binary_packed", "pack-13bit", "int64 timestamp-like", (long)valueCount * sizeof(long),
                (ref BufferWriter w) => DeltaBinaryPackedEncoding.WriteInt64(timestampDeltaInt64, ref w));
            Add("delta_binary_packed", "pack-generic", "int64 random", (long)valueCount * sizeof(long),
                (ref BufferWriter w) => DeltaBinaryPackedEncoding.WriteInt64(randomInt64, ref w));

            Add("rle_hybrid", "bitpack-w1", "indexes 2 unique", (long)valueCount * sizeof(int),
                (ref BufferWriter w) =>
                    RleBitPackingHybridEncoding.WriteWithBitWidthPrefixUnchecked(dictionaryIndexes1Bit, 1, ref w));
            Add("rle_hybrid", "bitpack-w8-aligned", "indexes 256 unique", (long)valueCount * sizeof(int),
                (ref BufferWriter w) =>
                    RleBitPackingHybridEncoding.WriteWithBitWidthPrefixUnchecked(dictionaryIndexes8Bit, 8, ref w));
            Add("rle_hybrid", "bitpack-w11-unaligned", "indexes 2048 unique", (long)valueCount * sizeof(int),
                (ref BufferWriter w) =>
                    RleBitPackingHybridEncoding.WriteWithBitWidthPrefixUnchecked(dictionaryIndexes11Bit, 11, ref w));
            Add("rle_hybrid", "bitpack-w16-aligned", "indexes 65536 unique", (long)valueCount * sizeof(int),
                (ref BufferWriter w) =>
                    RleBitPackingHybridEncoding.WriteWithBitWidthPrefixUnchecked(dictionaryIndexes16Bit, 16, ref w));
            Add("rle_hybrid", "rle-runs", "indexes long runs", (long)valueCount * sizeof(int),
                (ref BufferWriter w) =>
                    RleBitPackingHybridEncoding.WriteWithBitWidthPrefixUnchecked(dictionaryIndexesRuns, 8, ref w));
            Add("rle_hybrid", "bool-bitpack", "bool alternating", valueCount,
                (ref BufferWriter w) => RleBitPackingHybridEncoding.WriteBooleans(alternatingBooleans, ref w));
            Add("rle_hybrid", "bool-rle", "bool all-true", valueCount,
                (ref BufferWriter w) => RleBitPackingHybridEncoding.WriteBooleans(constantBooleans, ref w));
            Add("rle_hybrid", "bool-bitpack", "bool random", valueCount,
                (ref BufferWriter w) => RleBitPackingHybridEncoding.WriteBooleans(randomBooleans, ref w));

            writer = localWriter;
        }
        finally
        {
            writer.Dispose();
        }
    }

    static void MeasureByteArrays(int valueCount, List<Result> results)
    {
        var random = new Random(42);
        var shortValues = new byte[valueCount][];
        var sharedPrefixValues = new byte[valueCount][];
        var wideValues = new byte[valueCount][];
        long shortBytes = 0;
        long sharedPrefixBytes = 0;
        long wideBytes = 0;
        for (var i = 0; i < valueCount; i++)
        {
            shortValues[i] = System.Text.Encoding.UTF8.GetBytes($"val-{i % 2048}");
            sharedPrefixValues[i] = System.Text.Encoding.UTF8.GetBytes($"https://example.com/dataset/partition/{i:D9}");
            wideValues[i] = new byte[64];
            random.NextBytes(wideValues[i]);
            shortBytes += shortValues[i].Length;
            sharedPrefixBytes += sharedPrefixValues[i].Length;
            wideBytes += wideValues[i].Length;
        }

        var fixedLengthValues = new byte[valueCount][];
        for (var i = 0; i < valueCount; i++)
        {
            fixedLengthValues[i] = new byte[16];
            random.NextBytes(fixedLengthValues[i]);
        }

        var guidValues = new Guid[valueCount];
        for (var i = 0; i < valueCount; i++)
            guidValues[i] = Guid.NewGuid();

        var maximumBytes = Math.Max(Math.Max(shortBytes, sharedPrefixBytes), wideBytes) + (long)valueCount * 8 + 4096;
        var writer = CreateWriter(maximumBytes);
        var factory = new BufferWriterFactory(DefaultParquetBufferPool.Shared, 1 << 20, 1 << 20, 1 << 20, 1 << 16);
        try
        {
            var localWriter = writer;

            void Add(string encoder, string variant, string input, long inputBytes, RunEncoder run)
            {
                var result = Measure(encoder, variant, input, valueCount, inputBytes, ref localWriter, run);
                results.Add(result);
                Console.Error.WriteLine(
                    $"  {result.Encoder,-22} {result.Variant,-26} {result.Input,-22} n={valueCount,-9} " +
                    $"{result.NanosecondsPerValue,7:F3} ns/value  {result.InputGigabytesPerSecond,6:F2} GB/s");
            }

            Add("plain", "length-prefixed-copy", "byte[] ~8 bytes", shortBytes,
                (ref BufferWriter w) => PlainEncoding.WriteValues(ByteArrayColumn, shortValues, ref w));
            Add("plain", "length-prefixed-copy", "byte[] 64 bytes", wideBytes,
                (ref BufferWriter w) => PlainEncoding.WriteValues(ByteArrayColumn, wideValues, ref w));
            Add("plain", "flba-copy", "byte[16]", (long)valueCount * 16,
                (ref BufferWriter w) => PlainEncoding.WriteValues(FixedLength16Column, fixedLengthValues, ref w));
            Add("plain", "flba-guid", "Guid", (long)valueCount * 16,
                (ref BufferWriter w) => PlainEncoding.WriteValues(FixedLength16Column, guidValues, ref w));

            Add("byte_stream_split", "flba-lane-outer", "byte[16]", (long)valueCount * 16,
                (ref BufferWriter w) => ByteStreamSplitEncoding.WriteValues(FixedLength16Column, fixedLengthValues, ref w));
            Add("byte_stream_split", "flba-value-outer", "Guid", (long)valueCount * 16,
                (ref BufferWriter w) => ByteStreamSplitEncoding.WriteValues(FixedLength16Column, guidValues, ref w));

            var localFactory = factory;
            Add("delta_length_byte_array", "lengths+copy", "byte[] ~8 bytes", shortBytes,
                (ref BufferWriter w) =>
                    DeltaLengthByteArrayEncoding.WriteValues(ByteArrayColumn, shortValues, localFactory, ref w));
            Add("delta_length_byte_array", "lengths+copy", "byte[] 64 bytes", wideBytes,
                (ref BufferWriter w) =>
                    DeltaLengthByteArrayEncoding.WriteValues(ByteArrayColumn, wideValues, localFactory, ref w));

            Add("delta_byte_array", "prefix+suffix", "byte[] ~8 bytes", shortBytes,
                (ref BufferWriter w) =>
                    DeltaByteArrayEncoding.WriteValues(ByteArrayColumn, shortValues, localFactory, ref w));
            Add("delta_byte_array", "prefix+suffix", "byte[] shared prefix", sharedPrefixBytes,
                (ref BufferWriter w) =>
                    DeltaByteArrayEncoding.WriteValues(ByteArrayColumn, sharedPrefixValues, localFactory, ref w));
            Add("delta_byte_array", "prefix+suffix", "byte[] 64 bytes", wideBytes,
                (ref BufferWriter w) =>
                    DeltaByteArrayEncoding.WriteValues(ByteArrayColumn, wideValues, localFactory, ref w));

            writer = localWriter;
        }
        finally
        {
            writer.Dispose();
        }
    }

    /// <summary>
    /// Definition levels are encoded by the same RLE/bit-packing hybrid family as dictionary indexes, but
    /// through a separate run-only writer. Null layout is the input that decides which shape wins, so all
    /// three shapes are profiled: no nulls, nulls in long blocks, and nulls scattered per row.
    /// </summary>
    static void MeasureDefinitionLevels(int valueCount, List<Result> results)
    {
        var random = new Random(42);
        var noNulls = new int?[valueCount];
        var blockedNulls = new int?[valueCount];
        var scatteredNulls = new int?[valueCount];
        for (var i = 0; i < valueCount; i++)
        {
            noNulls[i] = i;
            blockedNulls[i] = i / 64 % 2 == 0 ? i : null;
            scatteredNulls[i] = random.Next(2) == 0 ? i : null;
        }

        foreach (var (name, values) in new (string, int?[])[]
                 {
                     ("optional int32 no nulls", noNulls),
                     ("optional int32 blocked nulls", blockedNulls),
                     ("optional int32 scattered nulls", scatteredNulls)
                 })
        {
            var definition = ColumnDefinition.Leaf("value", ParquetPhysicalType.Int32,
                new ColumnOptions(ParquetRepetition.Optional, [EncodingKind.Plain]));
            var schema = new Plank.Schema.ParquetSchema([definition]);
            using var stream = new MemoryStream(valueCount * 8 + 4096);
            var writer = schema.CreateWriter(stream, new ParquetWriterOptions { Compression = CompressionKind.None });
            var column = writer.CreateSerializedColumn<int?>(schema.LeafColumns[0]);

            void WriteOnce()
            {
                stream.Position = 0;
                stream.SetLength(0);
                writer.Reset(stream);
                var rowGroup = writer.StartRowGroup();
                column.Serialize(values);
                rowGroup.Write(column);
            }

            WriteOnce();
            var outputBytes = stream.Length;

            var stopwatch = Stopwatch.StartNew();
            var warmupCalls = 0;
            while (stopwatch.Elapsed.TotalMilliseconds < WarmupMilliseconds)
            {
                WriteOnce();
                warmupCalls++;
            }

            var nanosecondsPerWarmupCall = stopwatch.Elapsed.TotalMilliseconds * 1_000_000 / warmupCalls;
            var callsPerBatch = Math.Max(1,
                (int)Math.Min(int.MaxValue, TargetBatchMilliseconds * 1_000_000 / nanosecondsPerWarmupCall));

            var best = double.MaxValue;
            for (var round = 0; round < MeasurementRounds; round++)
            {
                var roundStopwatch = Stopwatch.StartNew();
                for (var call = 0; call < callsPerBatch; call++)
                    WriteOnce();
                roundStopwatch.Stop();
                var nanosecondsPerCall = roundStopwatch.Elapsed.TotalMilliseconds * 1_000_000 / callsPerBatch;
                if (nanosecondsPerCall < best)
                    best = nanosecondsPerCall;
            }

            var result = new Result("definition_levels", "rle-runs-only", name, valueCount,
                (long)valueCount * sizeof(int), outputBytes, best);
            results.Add(result);
            Console.Error.WriteLine(
                $"  {result.Encoder,-22} {result.Variant,-26} {result.Input,-32} n={valueCount,-9} " +
                $"{result.NanosecondsPerValue,7:F3} ns/value  {(double)outputBytes / valueCount,6:F2} out bytes/value");
        }
    }

    /// <summary>
    /// Page statistics run over the same values the encoder just wrote, so they are part of every
    /// encoding's real cost. Profiled separately because the end-to-end numbers show float and double
    /// columns costing far more than their identically sized integer counterparts.
    /// </summary>
    static void MeasureStatistics(int valueCount, List<Result> results)
    {
        var random = new Random(42);
        var int32Values = new int[valueCount];
        var int64Values = new long[valueCount];
        var floatsWithZero = new float[valueCount];
        var floatsWithoutZero = new float[valueCount];
        var doublesWithZero = new double[valueCount];
        var doublesWithoutZero = new double[valueCount];
        var shortByteArrays = new byte[valueCount][];
        for (var i = 0; i < valueCount; i++)
        {
            int32Values[i] = i % 100_000;
            int64Values[i] = i * 37L;
            floatsWithZero[i] = i % 10_000 / 3f;
            floatsWithoutZero[i] = 1f + i % 10_000 / 3f;
            doublesWithZero[i] = i % 10_000 / 7d;
            doublesWithoutZero[i] = 1d + i % 10_000 / 7d;
            shortByteArrays[i] = System.Text.Encoding.UTF8.GetBytes($"val-{i % 2048}");
        }

        void Add(string input, long inputBytes, Func<long> run)
        {
            var stopwatch = Stopwatch.StartNew();
            var warmupCalls = 0;
            while (stopwatch.Elapsed.TotalMilliseconds < WarmupMilliseconds)
            {
                run();
                warmupCalls++;
            }

            var nanosecondsPerWarmupCall = stopwatch.Elapsed.TotalMilliseconds * 1_000_000 / warmupCalls;
            var callsPerBatch = Math.Max(1,
                (int)Math.Min(int.MaxValue, TargetBatchMilliseconds * 1_000_000 / nanosecondsPerWarmupCall));

            var best = double.MaxValue;
            for (var round = 0; round < MeasurementRounds; round++)
            {
                var roundStopwatch = Stopwatch.StartNew();
                for (var call = 0; call < callsPerBatch; call++)
                    run();
                roundStopwatch.Stop();
                var nanosecondsPerCall = roundStopwatch.Elapsed.TotalMilliseconds * 1_000_000 / callsPerBatch;
                if (nanosecondsPerCall < best)
                    best = nanosecondsPerCall;
            }

            var result = new Result("statistics", "min/max", input, valueCount, inputBytes, 0, best);
            results.Add(result);
            Console.Error.WriteLine(
                $"  {result.Encoder,-22} {result.Variant,-26} {result.Input,-32} n={valueCount,-9} " +
                $"{result.NanosecondsPerValue,7:F3} ns/value  {result.InputGigabytesPerSecond,6:F2} GB/s");
        }

        Add("int32", (long)valueCount * sizeof(int),
            () => ColumnStatistics.Create(Int32Column, (ReadOnlySpan<int>)int32Values, 0).MinBits);
        Add("int64", (long)valueCount * sizeof(long),
            () => ColumnStatistics.Create(Int64Column, (ReadOnlySpan<long>)int64Values, 0).MinBits);
        Add("float (min is 0)", (long)valueCount * sizeof(float),
            () => ColumnStatistics.Create(FloatColumn, (ReadOnlySpan<float>)floatsWithZero, 0).MinBits);
        Add("float (min is not 0)", (long)valueCount * sizeof(float),
            () => ColumnStatistics.Create(FloatColumn, (ReadOnlySpan<float>)floatsWithoutZero, 0).MinBits);
        Add("double (min is 0)", (long)valueCount * sizeof(double),
            () => ColumnStatistics.Create(DoubleColumn, (ReadOnlySpan<double>)doublesWithZero, 0).MinBits);
        Add("double (min is not 0)", (long)valueCount * sizeof(double),
            () => ColumnStatistics.Create(DoubleColumn, (ReadOnlySpan<double>)doublesWithoutZero, 0).MinBits);
        Add("byte[] ~8 bytes", (long)valueCount * 8,
            () => ColumnStatistics.Create(ByteArrayColumn, (ReadOnlySpan<byte[]>)shortByteArrays, 0).MinValueLength);
    }

    delegate void RunEncoder(ref BufferWriter writer);

    static BufferWriter CreateWriter(long capacityBytes)
    {
        var capacity = checked((uint)Math.Min(capacityBytes, int.MaxValue));
        return new BufferWriter(DefaultParquetBufferPool.Shared, capacity, capacity);
    }

    static Result Measure(string encoder, string variant, string input, int valueCount, long inputBytes,
        ref BufferWriter writer, RunEncoder run)
    {
        writer.Reset();
        run(ref writer);
        var outputBytes = writer.WrittenLength;

        var stopwatch = Stopwatch.StartNew();
        var warmupCalls = 0;
        while (stopwatch.Elapsed.TotalMilliseconds < WarmupMilliseconds)
        {
            writer.Reset();
            run(ref writer);
            warmupCalls++;
        }

        var nanosecondsPerWarmupCall = stopwatch.Elapsed.TotalMilliseconds * 1_000_000 / warmupCalls;
        var callsPerBatch = Math.Max(1,
            (int)Math.Min(int.MaxValue, TargetBatchMilliseconds * 1_000_000 / nanosecondsPerWarmupCall));

        var best = double.MaxValue;
        for (var round = 0; round < MeasurementRounds; round++)
        {
            var roundStopwatch = Stopwatch.StartNew();
            for (var call = 0; call < callsPerBatch; call++)
            {
                writer.Reset();
                run(ref writer);
            }

            roundStopwatch.Stop();
            var nanosecondsPerCall = roundStopwatch.Elapsed.TotalMilliseconds * 1_000_000 / callsPerBatch;
            if (nanosecondsPerCall < best)
                best = nanosecondsPerCall;
        }

        return new Result(encoder, variant, input, valueCount, inputBytes, outputBytes, best);
    }

    /// <summary>
    /// Copy roofline for the same working-set size: the fastest an encoder that only moves bytes
    /// could possibly run on this machine at this size.
    /// </summary>
    static double MeasureCopyRoofline(long byteCount)
    {
        var source = new byte[byteCount];
        var destination = new byte[byteCount];
        new Random(42).NextBytes(source);

        var stopwatch = Stopwatch.StartNew();
        var warmupCalls = 0;
        while (stopwatch.Elapsed.TotalMilliseconds < WarmupMilliseconds)
        {
            source.AsSpan().CopyTo(destination);
            warmupCalls++;
        }

        var nanosecondsPerWarmupCall = stopwatch.Elapsed.TotalMilliseconds * 1_000_000 / warmupCalls;
        var callsPerBatch = Math.Max(1,
            (int)Math.Min(int.MaxValue, TargetBatchMilliseconds * 1_000_000 / nanosecondsPerWarmupCall));

        var best = double.MaxValue;
        for (var round = 0; round < MeasurementRounds; round++)
        {
            var roundStopwatch = Stopwatch.StartNew();
            for (var call = 0; call < callsPerBatch; call++)
                source.AsSpan().CopyTo(destination);
            roundStopwatch.Stop();
            var nanosecondsPerCall = roundStopwatch.Elapsed.TotalMilliseconds * 1_000_000 / callsPerBatch;
            if (nanosecondsPerCall < best)
                best = nanosecondsPerCall;
        }

        return byteCount / best;
    }

    static void WriteReport(List<Result> results, Dictionary<int, double> rooflines)
    {
        var report = new StringBuilder();
        report.AppendLine("# Encoding throughput profile");
        report.AppendLine();
        report.AppendLine(CultureInfo.InvariantCulture,
            $"Machine: {Environment.ProcessorCount} logical cores, {RuntimeInformation.ProcessArchitecture}, .NET {Environment.Version}.");
        report.AppendLine();
        report.AppendLine("Copy roofline (`Span.CopyTo`) measured on the same machine:");
        report.AppendLine();
        report.AppendLine("| Working set | GB/s |");
        report.AppendLine("| --- | ---: |");
        foreach (var (valueCount, gigabytesPerSecond) in rooflines.OrderBy(entry => entry.Key))
            report.AppendLine(CultureInfo.InvariantCulture,
                $"| {valueCount * sizeof(long) / 1024} KiB | {gigabytesPerSecond:F2} |");
        report.AppendLine();

        foreach (var encoderGroup in results.GroupBy(result => result.Encoder))
        {
            report.AppendLine(CultureInfo.InvariantCulture, $"## {encoderGroup.Key}");
            report.AppendLine();
            report.AppendLine("| Algorithm | Input | Values | ns/value | in GB/s | out GB/s | out/in bytes |");
            report.AppendLine("| --- | --- | ---: | ---: | ---: | ---: | ---: |");
            foreach (var result in encoderGroup)
                report.AppendLine(CultureInfo.InvariantCulture,
                    $"| {result.Variant} | {result.Input} | {result.ValueCount} | {result.NanosecondsPerValue:F3} | " +
                    $"{result.InputGigabytesPerSecond:F2} | {result.OutputGigabytesPerSecond:F2} | " +
                    $"{(double)result.OutputBytes / result.InputBytes:F2} |");
            report.AppendLine();
        }

        var outputDirectory = Path.Combine("artifacts", "benchmarks");
        Directory.CreateDirectory(outputDirectory);
        var outputPath = Path.Combine(outputDirectory, "encoding-profile.md");
        File.WriteAllText(outputPath, report.ToString());
        Console.WriteLine(report.ToString());
        Console.Error.WriteLine($"Wrote {outputPath}");
    }
}
