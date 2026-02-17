using Parquet;
using Parquet.Schema;
using ParquetSharp;
using Plank.Schema;
using Plank.Writing;
using System.Diagnostics;
using Column = ParquetSharp.Column;
using CompressionMethod = Parquet.CompressionMethod;
using DataColumn = Parquet.Data.DataColumn;
using ParquetSchema = Parquet.Schema.ParquetSchema;
using PlankColumn = Plank.Schema.Column;
using PlankSchema = Plank.Schema.ParquetSchema;

namespace Plank.Benchmarks;

public static class ThroughputBenchmarkRunner
{
    static readonly Column[] SharpColumns =
    [
        new Column<int?>("VendorID"),
        new Column<DateTime?>("tpep_pickup_datetime"),
        new Column<DateTime?>("tpep_dropoff_datetime"),
        new Column<long?>("passenger_count"),
        new Column<double?>("trip_distance"),
        new Column<long?>("RatecodeID"),
        new Column<string?>("store_and_fwd_flag"),
        new Column<int?>("PULocationID"),
        new Column<int?>("DOLocationID"),
        new Column<long?>("payment_type"),
        new Column<double?>("fare_amount"),
        new Column<double?>("extra"),
        new Column<double?>("mta_tax"),
        new Column<double?>("tip_amount"),
        new Column<double?>("tolls_amount"),
        new Column<double?>("improvement_surcharge"),
        new Column<double?>("total_amount"),
        new Column<double?>("congestion_surcharge"),
        new Column<double?>("Airport_fee")
    ];

    static readonly ParquetOptions ParquetNetOptions = new()
    {
        UseDictionaryEncoding = false,
        UseDeltaBinaryPackedEncoding = false
    };

    static readonly ColumnOptions RequiredPlain = new(ParquetRepetition.Required, [EncodingKind.Plain]);

    static readonly DataField<int?> VendorIdField = new("VendorID");
    static readonly DataField<DateTime?> PickupDateTimeField = new("tpep_pickup_datetime");
    static readonly DataField<DateTime?> DropoffDateTimeField = new("tpep_dropoff_datetime");
    static readonly DataField<long?> PassengerCountField = new("passenger_count");
    static readonly DataField<double?> TripDistanceField = new("trip_distance");
    static readonly DataField<long?> RatecodeIdField = new("RatecodeID");
    static readonly DataField<string?> StoreAndFwdFlagField = new("store_and_fwd_flag");
    static readonly DataField<int?> PuLocationIdField = new("PULocationID");
    static readonly DataField<int?> DoLocationIdField = new("DOLocationID");
    static readonly DataField<long?> PaymentTypeField = new("payment_type");
    static readonly DataField<double?> FareAmountField = new("fare_amount");
    static readonly DataField<double?> ExtraField = new("extra");
    static readonly DataField<double?> MtaTaxField = new("mta_tax");
    static readonly DataField<double?> TipAmountField = new("tip_amount");
    static readonly DataField<double?> TollsAmountField = new("tolls_amount");
    static readonly DataField<double?> ImprovementSurchargeField = new("improvement_surcharge");
    static readonly DataField<double?> TotalAmountField = new("total_amount");
    static readonly DataField<double?> CongestionSurchargeField = new("congestion_surcharge");
    static readonly DataField<double?> AirportFeeField = new("Airport_fee");

    static readonly ParquetSchema ParquetNetSchema = new(
        VendorIdField,
        PickupDateTimeField,
        DropoffDateTimeField,
        PassengerCountField,
        TripDistanceField,
        RatecodeIdField,
        StoreAndFwdFlagField,
        PuLocationIdField,
        DoLocationIdField,
        PaymentTypeField,
        FareAmountField,
        ExtraField,
        MtaTaxField,
        TipAmountField,
        TollsAmountField,
        ImprovementSurchargeField,
        TotalAmountField,
        CongestionSurchargeField,
        AirportFeeField);

    static readonly PlankSchema PlankWriteSchema = new([
        new PlankColumn("VendorID", ParquetPhysicalType.Int32, RequiredPlain),
        new PlankColumn("tpep_pickup_datetime", ParquetPhysicalType.Int64, RequiredPlain),
        new PlankColumn("tpep_dropoff_datetime", ParquetPhysicalType.Int64, RequiredPlain),
        new PlankColumn("passenger_count", ParquetPhysicalType.Int64, RequiredPlain),
        new PlankColumn("trip_distance", ParquetPhysicalType.Double, RequiredPlain),
        new PlankColumn("RatecodeID", ParquetPhysicalType.Int64, RequiredPlain),
        new PlankColumn("store_and_fwd_flag", ParquetPhysicalType.ByteArray, RequiredPlain),
        new PlankColumn("PULocationID", ParquetPhysicalType.Int32, RequiredPlain),
        new PlankColumn("DOLocationID", ParquetPhysicalType.Int32, RequiredPlain),
        new PlankColumn("payment_type", ParquetPhysicalType.Int64, RequiredPlain),
        new PlankColumn("fare_amount", ParquetPhysicalType.Double, RequiredPlain),
        new PlankColumn("extra", ParquetPhysicalType.Double, RequiredPlain),
        new PlankColumn("mta_tax", ParquetPhysicalType.Double, RequiredPlain),
        new PlankColumn("tip_amount", ParquetPhysicalType.Double, RequiredPlain),
        new PlankColumn("tolls_amount", ParquetPhysicalType.Double, RequiredPlain),
        new PlankColumn("improvement_surcharge", ParquetPhysicalType.Double, RequiredPlain),
        new PlankColumn("total_amount", ParquetPhysicalType.Double, RequiredPlain),
        new PlankColumn("congestion_surcharge", ParquetPhysicalType.Double, RequiredPlain),
        new PlankColumn("Airport_fee", ParquetPhysicalType.Double, RequiredPlain)
    ]);

    public static async Task RunAsync(BenchmarkDataContext context, ThroughputBenchmarkOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(options);

        Directory.CreateDirectory(options.OutputDirectory);
        if (!string.IsNullOrWhiteSpace(options.MetricsDirectory))
            Directory.CreateDirectory(options.MetricsDirectory);

        var allScenarios = new[]
        {
            new Scenario("Plank", WritePlankAsync),
            new Scenario("encode_ahead", WritePlankEncodeAheadAsync),
            new Scenario("ParquetSharp", WriteParquetSharpAsync),
            new Scenario("Parquet.Net", WriteParquetNetAsync)
        };
        var libraries = new HashSet<string>(options.Libraries, StringComparer.OrdinalIgnoreCase);
        var scenarios = allScenarios
            .Where(static scenario => scenario.Name is "Plank" or "encode_ahead" or "ParquetSharp" or "Parquet.Net")
            .Where(scenario => IsSelected(scenario.Name, libraries))
            .ToArray();
        if (scenarios.Length == 0)
            throw new InvalidOperationException("No benchmark libraries selected. Use --library with one or more of: plank, encode_ahead, parquetsharp, parquet.net.");

        var results = new List<ThroughputScenarioResult>(scenarios.Length);
        foreach (var scenario in scenarios)
        {
            var result = await RunScenarioAsync(context, options, scenario, cancellationToken).ConfigureAwait(false);
            results.Add(result);
        }

        PrintResults(context, options, results);
    }

    static async Task<ThroughputScenarioResult> RunScenarioAsync(
        BenchmarkDataContext context,
        ThroughputBenchmarkOptions options,
        Scenario scenario,
        CancellationToken cancellationToken)
    {
        Console.WriteLine();
        Console.WriteLine($"{scenario.Name}:");

        var totalElapsed = TimeSpan.Zero;
        long totalBytes = 0;

        for (var i = 0; i < options.MeasureIterations; i++)
        {
            var filePath = Path.Combine(options.OutputDirectory, $"{NormalizeName(scenario.Name)}-run-{i + 1}.parquet");
            var metricsPath = GetMetricsPath(options, scenario.Name, i + 1);
            var run = await RunSingleAsync(context, scenario, filePath, metricsPath, cancellationToken).ConfigureAwait(false);
            totalElapsed += run.Elapsed;
            totalBytes = checked(totalBytes + run.BytesWritten);
            Console.WriteLine($"  run {i + 1}/{options.MeasureIterations}: {FormatRun(run, context.TotalRows)}");
            DeleteIfNeeded(filePath, options.KeepFiles);
        }

        var avgSeconds = totalElapsed.TotalSeconds / options.MeasureIterations;
        var avgBytes = (double)totalBytes / options.MeasureIterations;

        return new ThroughputScenarioResult
        {
            Name = scenario.Name,
            Iterations = options.MeasureIterations,
            TotalBytes = totalBytes,
            AverageElapsed = TimeSpan.FromSeconds(avgSeconds),
            AverageMegabytesPerSecond = (avgBytes / (1024d * 1024d)) / avgSeconds,
            AverageRowsPerSecond = context.TotalRows / avgSeconds
        };
    }

    static async Task<RunResult> RunSingleAsync(BenchmarkDataContext context, Scenario scenario, string outputPath, string? metricsPath, CancellationToken cancellationToken)
    {
        if (File.Exists(outputPath))
            File.Delete(outputPath);

        var started = Stopwatch.StartNew();
        await scenario.WriteAsync(context, outputPath, cancellationToken, metricsPath).ConfigureAwait(false);
        started.Stop();

        var bytes = new FileInfo(outputPath).Length;
        return new RunResult(started.Elapsed, bytes);
    }

    static async Task WritePlankAsync(BenchmarkDataContext context, string outputPath, CancellationToken cancellationToken, string? metricsPath)
    {
        var metricsLog = metricsPath is null ? null : new ThroughputPlankMetricsLog();
        await using var stream = CreateOutputStream(outputPath);
        var writer = Plank.Writing.ParquetWriter.Create(stream, PlankWriteSchema, CreatePlankOptions(context, metricsLog));
        var serializedColumns = CreateSerializedColumns(writer);

        foreach (var trip in context.TripsByFile)
        {
            var rowGroup = writer.StartRowGroup();
            SerializeTrip(serializedColumns, trip);
            WriteSerializedColumns(rowGroup, serializedColumns);
        }

        var flushStart = Stopwatch.GetTimestamp();
        writer.CloseFile();
        var flushEnd = Stopwatch.GetTimestamp();
        metricsLog?.FlushObserved(flushEnd - flushStart);
        if (metricsLog is not null && metricsPath is not null)
            await metricsLog.WriteParquetAsync(metricsPath, cancellationToken).ConfigureAwait(false);
    }

    static void WriteSerializedColumns(Plank.Writing.RowGroupWriter rowGroup, SerializedColumn[] serializedColumns)
    {
        for (var i = 0; i < serializedColumns.Length; i++)
            rowGroup.Write(serializedColumns[i]);
    }

    static async Task WriteParquetSharpAsync(BenchmarkDataContext context, string outputPath, CancellationToken cancellationToken, string? metricsPath)
    {
        _ = metricsPath;
        await using var stream = CreateOutputStream(outputPath);
        using var writerProperties = new WriterPropertiesBuilder()
            .Compression(Compression.Uncompressed)
            .DisableDictionary()
            .Encoding(Encoding.Plain)
            .Build();

        using (var writer = new ParquetFileWriter(stream, SharpColumns, null, writerProperties, null, true))
        {
            foreach (var trip in context.TripsByFile)
            {
                using var rowGroupWriter = writer.AppendRowGroup();
                using var c0 = rowGroupWriter.NextColumn().LogicalWriter<int?>();
                c0.WriteBatch(trip.VendorId);
                using var c1 = rowGroupWriter.NextColumn().LogicalWriter<DateTime?>();
                c1.WriteBatch(trip.PickupDateTime);
                using var c2 = rowGroupWriter.NextColumn().LogicalWriter<DateTime?>();
                c2.WriteBatch(trip.DropoffDateTime);
                using var c3 = rowGroupWriter.NextColumn().LogicalWriter<long?>();
                c3.WriteBatch(trip.PassengerCount);
                using var c4 = rowGroupWriter.NextColumn().LogicalWriter<double?>();
                c4.WriteBatch(trip.TripDistance);
                using var c5 = rowGroupWriter.NextColumn().LogicalWriter<long?>();
                c5.WriteBatch(trip.RatecodeId);
                using var c6 = rowGroupWriter.NextColumn().LogicalWriter<string?>();
                c6.WriteBatch(trip.StoreAndFwdFlag);
                using var c7 = rowGroupWriter.NextColumn().LogicalWriter<int?>();
                c7.WriteBatch(trip.PuLocationId);
                using var c8 = rowGroupWriter.NextColumn().LogicalWriter<int?>();
                c8.WriteBatch(trip.DoLocationId);
                using var c9 = rowGroupWriter.NextColumn().LogicalWriter<long?>();
                c9.WriteBatch(trip.PaymentType);
                using var c10 = rowGroupWriter.NextColumn().LogicalWriter<double?>();
                c10.WriteBatch(trip.FareAmount);
                using var c11 = rowGroupWriter.NextColumn().LogicalWriter<double?>();
                c11.WriteBatch(trip.Extra);
                using var c12 = rowGroupWriter.NextColumn().LogicalWriter<double?>();
                c12.WriteBatch(trip.MtaTax);
                using var c13 = rowGroupWriter.NextColumn().LogicalWriter<double?>();
                c13.WriteBatch(trip.TipAmount);
                using var c14 = rowGroupWriter.NextColumn().LogicalWriter<double?>();
                c14.WriteBatch(trip.TollsAmount);
                using var c15 = rowGroupWriter.NextColumn().LogicalWriter<double?>();
                c15.WriteBatch(trip.ImprovementSurcharge);
                using var c16 = rowGroupWriter.NextColumn().LogicalWriter<double?>();
                c16.WriteBatch(trip.TotalAmount);
                using var c17 = rowGroupWriter.NextColumn().LogicalWriter<double?>();
                c17.WriteBatch(trip.CongestionSurcharge);
                using var c18 = rowGroupWriter.NextColumn().LogicalWriter<double?>();
                c18.WriteBatch(trip.AirportFee);
            }

            writer.Close();
        }

        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    static async Task WriteParquetNetAsync(BenchmarkDataContext context, string outputPath, CancellationToken cancellationToken, string? metricsPath)
    {
        _ = metricsPath;
        await using var stream = CreateOutputStream(outputPath);
        await using var writer = await Parquet.ParquetWriter.CreateAsync(ParquetNetSchema, stream, ParquetNetOptions).ConfigureAwait(false);
        writer.CompressionMethod = CompressionMethod.None;

        foreach (var trip in context.TripsByFile)
        {
            using var rowGroupWriter = writer.CreateRowGroup();
            await rowGroupWriter.WriteColumnAsync(new DataColumn(VendorIdField, trip.VendorId)).ConfigureAwait(false);
            await rowGroupWriter.WriteColumnAsync(new DataColumn(PickupDateTimeField, trip.PickupDateTime)).ConfigureAwait(false);
            await rowGroupWriter.WriteColumnAsync(new DataColumn(DropoffDateTimeField, trip.DropoffDateTime)).ConfigureAwait(false);
            await rowGroupWriter.WriteColumnAsync(new DataColumn(PassengerCountField, trip.PassengerCount)).ConfigureAwait(false);
            await rowGroupWriter.WriteColumnAsync(new DataColumn(TripDistanceField, trip.TripDistance)).ConfigureAwait(false);
            await rowGroupWriter.WriteColumnAsync(new DataColumn(RatecodeIdField, trip.RatecodeId)).ConfigureAwait(false);
            await rowGroupWriter.WriteColumnAsync(new DataColumn(StoreAndFwdFlagField, trip.StoreAndFwdFlag)).ConfigureAwait(false);
            await rowGroupWriter.WriteColumnAsync(new DataColumn(PuLocationIdField, trip.PuLocationId)).ConfigureAwait(false);
            await rowGroupWriter.WriteColumnAsync(new DataColumn(DoLocationIdField, trip.DoLocationId)).ConfigureAwait(false);
            await rowGroupWriter.WriteColumnAsync(new DataColumn(PaymentTypeField, trip.PaymentType)).ConfigureAwait(false);
            await rowGroupWriter.WriteColumnAsync(new DataColumn(FareAmountField, trip.FareAmount)).ConfigureAwait(false);
            await rowGroupWriter.WriteColumnAsync(new DataColumn(ExtraField, trip.Extra)).ConfigureAwait(false);
            await rowGroupWriter.WriteColumnAsync(new DataColumn(MtaTaxField, trip.MtaTax)).ConfigureAwait(false);
            await rowGroupWriter.WriteColumnAsync(new DataColumn(TipAmountField, trip.TipAmount)).ConfigureAwait(false);
            await rowGroupWriter.WriteColumnAsync(new DataColumn(TollsAmountField, trip.TollsAmount)).ConfigureAwait(false);
            await rowGroupWriter.WriteColumnAsync(new DataColumn(ImprovementSurchargeField, trip.ImprovementSurcharge)).ConfigureAwait(false);
            await rowGroupWriter.WriteColumnAsync(new DataColumn(TotalAmountField, trip.TotalAmount)).ConfigureAwait(false);
            await rowGroupWriter.WriteColumnAsync(new DataColumn(CongestionSurchargeField, trip.CongestionSurcharge)).ConfigureAwait(false);
            await rowGroupWriter.WriteColumnAsync(new DataColumn(AirportFeeField, trip.AirportFee)).ConfigureAwait(false);
        }

        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    static async Task WritePlankEncodeAheadAsync(BenchmarkDataContext context, string outputPath, CancellationToken cancellationToken, string? metricsPath)
    {
        var metricsLog = metricsPath is null ? null : new ThroughputPlankMetricsLog();
        await using var stream = CreateOutputStream(outputPath);
        var writer = Plank.Writing.ParquetWriter.Create(stream, PlankWriteSchema, CreatePlankOptions(context, metricsLog));
        var serializedColumns = CreateSerializedColumns(writer);
        var serializeTicks = new long[PlankWriteSchema.Columns.Length];

        foreach (var trip in context.TripsByFile)
        {
            for (var i = 0; i < serializedColumns.Length; i++)
            {
                var started = Stopwatch.GetTimestamp();
                SerializePlankColumnAhead(serializedColumns[i], trip, i);
                serializeTicks[i] = Stopwatch.GetTimestamp() - started;
            }
            if (metricsLog is not null)
                for (var i = 0; i < serializedColumns.Length; i++)
                    metricsLog.ColumnWriteMetricsObserved(
                        PlankWriteSchema.Columns[i].Name,
                        trip.RowCount,
                        trip.RowCount,
                        0,
                        serializeTicks[i],
                        0,
                        0,
                        0);
            var rowGroup = writer.StartRowGroup();
            WriteSerializedColumns(rowGroup, serializedColumns);
        }

        var flushStart = Stopwatch.GetTimestamp();
        writer.CloseFile();
        var flushEnd = Stopwatch.GetTimestamp();
        metricsLog?.FlushObserved(flushEnd - flushStart);
        if (metricsLog is not null && metricsPath is not null)
            await metricsLog.WriteParquetAsync(metricsPath, cancellationToken).ConfigureAwait(false);
    }

    static void SerializePlankColumnAhead(SerializedColumn serialized, NycTripData trip, int index)
    {
        switch (index)
        {
            case 0:
                serialized.Serialize(PlankWriteSchema.Columns[0], ToRequiredInt32(trip.VendorId));
                return;
            case 1:
                serialized.Serialize(PlankWriteSchema.Columns[1], ToRequiredTicks(trip.PickupDateTime));
                return;
            case 2:
                serialized.Serialize(PlankWriteSchema.Columns[2], ToRequiredTicks(trip.DropoffDateTime));
                return;
            case 3:
                serialized.Serialize(PlankWriteSchema.Columns[3], ToRequiredInt64(trip.PassengerCount));
                return;
            case 4:
                serialized.Serialize(PlankWriteSchema.Columns[4], ToRequiredDouble(trip.TripDistance));
                return;
            case 5:
                serialized.Serialize(PlankWriteSchema.Columns[5], ToRequiredInt64(trip.RatecodeId));
                return;
            case 6:
                serialized.Serialize(PlankWriteSchema.Columns[6], ToRequiredUtf8(trip.StoreAndFwdFlag));
                return;
            case 7:
                serialized.Serialize(PlankWriteSchema.Columns[7], ToRequiredInt32(trip.PuLocationId));
                return;
            case 8:
                serialized.Serialize(PlankWriteSchema.Columns[8], ToRequiredInt32(trip.DoLocationId));
                return;
            case 9:
                serialized.Serialize(PlankWriteSchema.Columns[9], ToRequiredInt64(trip.PaymentType));
                return;
            case 10:
                serialized.Serialize(PlankWriteSchema.Columns[10], ToRequiredDouble(trip.FareAmount));
                return;
            case 11:
                serialized.Serialize(PlankWriteSchema.Columns[11], ToRequiredDouble(trip.Extra));
                return;
            case 12:
                serialized.Serialize(PlankWriteSchema.Columns[12], ToRequiredDouble(trip.MtaTax));
                return;
            case 13:
                serialized.Serialize(PlankWriteSchema.Columns[13], ToRequiredDouble(trip.TipAmount));
                return;
            case 14:
                serialized.Serialize(PlankWriteSchema.Columns[14], ToRequiredDouble(trip.TollsAmount));
                return;
            case 15:
                serialized.Serialize(PlankWriteSchema.Columns[15], ToRequiredDouble(trip.ImprovementSurcharge));
                return;
            case 16:
                serialized.Serialize(PlankWriteSchema.Columns[16], ToRequiredDouble(trip.TotalAmount));
                return;
            case 17:
                serialized.Serialize(PlankWriteSchema.Columns[17], ToRequiredDouble(trip.CongestionSurcharge));
                return;
            case 18:
                serialized.Serialize(PlankWriteSchema.Columns[18], ToRequiredDouble(trip.AirportFee));
                return;
            default:
                throw new ArgumentOutOfRangeException(nameof(index), index, "Column index is out of range.");
        }
    }

    static SerializedColumn[] CreateSerializedColumns(Plank.Writing.ParquetWriter writer)
    {
        var result = new SerializedColumn[PlankWriteSchema.Columns.Length];
        for (var i = 0; i < result.Length; i++)
            result[i] = writer.CreateSerializedColumn();
        return result;
    }

    static void SerializeTrip(SerializedColumn[] serializedColumns, NycTripData trip)
    {
        for (var i = 0; i < serializedColumns.Length; i++)
            SerializePlankColumnAhead(serializedColumns[i], trip, i);
    }

    static ParquetWriterOptions CreatePlankOptions(BenchmarkDataContext context, ThroughputPlankMetricsLog? metricsLog)
    {
        _ = context;
        _ = metricsLog;
        return new ParquetWriterOptions
        {
            Compression = CompressionKind.None
        };
    }

    static int[] ToRequiredInt32(int?[] values)
    {
        var result = new int[values.Length];
        for (var i = 0; i < values.Length; i++)
            result[i] = values[i] ?? default;
        return result;
    }

    static long[] ToRequiredInt64(long?[] values)
    {
        var result = new long[values.Length];
        for (var i = 0; i < values.Length; i++)
            result[i] = values[i] ?? default;
        return result;
    }

    static long[] ToRequiredTicks(DateTime?[] values)
    {
        var result = new long[values.Length];
        for (var i = 0; i < values.Length; i++)
            result[i] = values[i]?.Ticks ?? default;
        return result;
    }

    static double[] ToRequiredDouble(double?[] values)
    {
        var result = new double[values.Length];
        for (var i = 0; i < values.Length; i++)
            result[i] = values[i] ?? default;
        return result;
    }

    static byte[][] ToRequiredUtf8(string?[] values)
    {
        var result = new byte[values.Length][];
        for (var i = 0; i < values.Length; i++)
            result[i] = System.Text.Encoding.UTF8.GetBytes(values[i] ?? string.Empty);
        return result;
    }

    static FileStream CreateOutputStream(string outputPath)
        => new(
            outputPath,
            new FileStreamOptions
            {
                Mode = FileMode.Create,
                Access = FileAccess.Write,
                Share = FileShare.None,
                BufferSize = 1024 * 1024,
                Options = FileOptions.Asynchronous
            });

    static string FormatRun(RunResult run, int totalRows)
    {
        var seconds = run.Elapsed.TotalSeconds;
        var mbPerSecond = (run.BytesWritten / (1024d * 1024d)) / seconds;
        var rowsPerSecond = totalRows / seconds;
        return $"{run.Elapsed.TotalMilliseconds,8:F2} ms, {run.BytesWritten / (1024d * 1024d),8:F2} MiB, {mbPerSecond,8:F2} MiB/s, {rowsPerSecond,12:F0} rows/s";
    }

    static void PrintResults(BenchmarkDataContext context, ThroughputBenchmarkOptions options, List<ThroughputScenarioResult> results)
    {
        Console.WriteLine();
        Console.WriteLine("Summary:");
        Console.WriteLine($"  rows per run: {context.TotalRows:N0}");
        Console.WriteLine($"  measured iterations: {options.MeasureIterations}");
        Console.WriteLine();
        Console.WriteLine($"{"Library",-14} {"Avg Time",12} {"Avg MiB/s",12} {"Avg Rows/s",14} {"Avg Size",12}");

        foreach (var result in results.OrderByDescending(static r => r.AverageMegabytesPerSecond))
        {
            var avgSizeBytes = (double)result.TotalBytes / result.Iterations;
            Console.WriteLine($"{result.Name,-14} {result.AverageElapsed.TotalMilliseconds,12:F2} {result.AverageMegabytesPerSecond,12:F2} {result.AverageRowsPerSecond,14:F0} {avgSizeBytes / (1024d * 1024d),12:F2}");
        }
    }

    static void DeleteIfNeeded(string path, bool keepFiles)
    {
        if (keepFiles)
            return;
        if (File.Exists(path))
            File.Delete(path);
    }

    static string NormalizeName(string value)
        => value.Replace(".", string.Empty, StringComparison.Ordinal).ToLowerInvariant();

    static bool IsSelected(string scenarioName, HashSet<string> libraries)
        => scenarioName switch
        {
            "Plank" => libraries.Contains("plank"),
            "encode_ahead" => libraries.Contains("encode_ahead") || libraries.Contains("encodeahead"),
            "ParquetSharp" => libraries.Contains("parquetsharp"),
            "Parquet.Net" => libraries.Contains("parquet.net") || libraries.Contains("parquetnet"),
            _ => false
        };

    static string? GetMetricsPath(ThroughputBenchmarkOptions options, string scenarioName, int runNumber)
    {
        if (scenarioName is not ("Plank" or "encode_ahead"))
            return null;
        if (string.IsNullOrWhiteSpace(options.MetricsDirectory))
            return null;
        var prefix = scenarioName == "Plank" ? "plank" : "plank-encode-ahead";
        return Path.Combine(options.MetricsDirectory, $"{prefix}-run-{runNumber}-metrics.parquet");
    }

    readonly record struct Scenario(string Name, Func<BenchmarkDataContext, string, CancellationToken, string?, Task> WriteAsync);

    readonly record struct RunResult(TimeSpan Elapsed, long BytesWritten);
}
