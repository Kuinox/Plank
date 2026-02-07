using BenchmarkDotNet.Attributes;
using Parquet;
using Parquet.Data;
using Parquet.Schema;
using ParquetSharp;
using Plank.Writing;
using PlankColumn = Plank.Schema.Column;
using PlankSchema = Plank.Schema.ParquetSchema;
using PlankColumnOptions = Plank.Schema.ColumnOptions;
using PlankEncodingKind = Plank.Schema.EncodingKind;
using PlankRepetition = Plank.Schema.ParquetRepetition;
using PlankPhysicalType = Plank.Schema.ParquetPhysicalType;

namespace Plank.Benchmarks;

[MemoryDiagnoser]
public class NycParquetWriteBenchmarks
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

    static readonly PlankColumnOptions OptionalPlain =
        new(PlankRepetition.Optional, [PlankEncodingKind.Plain]);

    PlankSchema _plankSchema = null!;
    DataField<int?> _vendorId = null!;
    DataField<DateTime?> _pickupDateTime = null!;
    DataField<DateTime?> _dropoffDateTime = null!;
    DataField<long?> _passengerCount = null!;
    DataField<double?> _tripDistance = null!;
    DataField<long?> _ratecodeId = null!;
    DataField<string?> _storeAndFwdFlag = null!;
    DataField<int?> _puLocationId = null!;
    DataField<int?> _doLocationId = null!;
    DataField<long?> _paymentType = null!;
    DataField<double?> _fareAmount = null!;
    DataField<double?> _extra = null!;
    DataField<double?> _mtaTax = null!;
    DataField<double?> _tipAmount = null!;
    DataField<double?> _tollsAmount = null!;
    DataField<double?> _improvementSurcharge = null!;
    DataField<double?> _totalAmount = null!;
    DataField<double?> _congestionSurcharge = null!;
    DataField<double?> _airportFee = null!;
    BenchmarkDataContext _context = null!;

    MemoryStream _plankStream = null!;
    MemoryStream _sharpStream = null!;
    MemoryStream _parquetNetStream = null!;

    Writing.ParquetWriter _plankWriter = null!;
    WriterProperties _sharpWriterProperties = null!;
    readonly ValueTask[] _parallelWrites = new ValueTask[19];
    readonly ValueTask[] _dedicatedWrites = new ValueTask[19];
    readonly ColumnWorker[] _workers = new ColumnWorker[19];
    readonly ParallelOptions _parallelOptions = new()
    {
        MaxDegreeOfParallelism = Math.Min(19, Environment.ProcessorCount)
    };
    NycTripData _workerTrip = null!;
    Writing.RowGroupWriter _workerRowGroup;

    [GlobalSetup]
    public void Setup()
    {
        if (NycBenchmarkContext.Current is null)
        {
            if (!NycBenchmarkContext.TryGetConfiguration(out var dataDirectory, out var fileCount))
                throw new InvalidOperationException("Benchmark data context was not initialized. Run Plank.Benchmarks program entry so files and configuration are prepared.");

            var manager = new NycDatasetManager();
            var files = manager.ResolveExistingFiles(dataDirectory!, fileCount);
            NycBenchmarkContext.Current = manager.LoadContext(files);
        }

        _context = NycBenchmarkContext.Current;
        _plankSchema = new PlankSchema([
            new PlankColumn("VendorID", PlankPhysicalType.Int32, OptionalPlain),
            new PlankColumn("tpep_pickup_datetime", PlankPhysicalType.Int64, OptionalPlain),
            new PlankColumn("tpep_dropoff_datetime", PlankPhysicalType.Int64, OptionalPlain),
            new PlankColumn("passenger_count", PlankPhysicalType.Int64, OptionalPlain),
            new PlankColumn("trip_distance", PlankPhysicalType.Double, OptionalPlain),
            new PlankColumn("RatecodeID", PlankPhysicalType.Int64, OptionalPlain),
            new PlankColumn("store_and_fwd_flag", PlankPhysicalType.ByteArray, OptionalPlain),
            new PlankColumn("PULocationID", PlankPhysicalType.Int32, OptionalPlain),
            new PlankColumn("DOLocationID", PlankPhysicalType.Int32, OptionalPlain),
            new PlankColumn("payment_type", PlankPhysicalType.Int64, OptionalPlain),
            new PlankColumn("fare_amount", PlankPhysicalType.Double, OptionalPlain),
            new PlankColumn("extra", PlankPhysicalType.Double, OptionalPlain),
            new PlankColumn("mta_tax", PlankPhysicalType.Double, OptionalPlain),
            new PlankColumn("tip_amount", PlankPhysicalType.Double, OptionalPlain),
            new PlankColumn("tolls_amount", PlankPhysicalType.Double, OptionalPlain),
            new PlankColumn("improvement_surcharge", PlankPhysicalType.Double, OptionalPlain),
            new PlankColumn("total_amount", PlankPhysicalType.Double, OptionalPlain),
            new PlankColumn("congestion_surcharge", PlankPhysicalType.Double, OptionalPlain),
            new PlankColumn("Airport_fee", PlankPhysicalType.Double, OptionalPlain)
        ]);

        _vendorId = new DataField<int?>("VendorID");
        _pickupDateTime = new DataField<DateTime?>("tpep_pickup_datetime");
        _dropoffDateTime = new DataField<DateTime?>("tpep_dropoff_datetime");
        _passengerCount = new DataField<long?>("passenger_count");
        _tripDistance = new DataField<double?>("trip_distance");
        _ratecodeId = new DataField<long?>("RatecodeID");
        _storeAndFwdFlag = new DataField<string?>("store_and_fwd_flag");
        _puLocationId = new DataField<int?>("PULocationID");
        _doLocationId = new DataField<int?>("DOLocationID");
        _paymentType = new DataField<long?>("payment_type");
        _fareAmount = new DataField<double?>("fare_amount");
        _extra = new DataField<double?>("extra");
        _mtaTax = new DataField<double?>("mta_tax");
        _tipAmount = new DataField<double?>("tip_amount");
        _tollsAmount = new DataField<double?>("tolls_amount");
        _improvementSurcharge = new DataField<double?>("improvement_surcharge");
        _totalAmount = new DataField<double?>("total_amount");
        _congestionSurcharge = new DataField<double?>("congestion_surcharge");
        _airportFee = new DataField<double?>("Airport_fee");

        var initialCapacity = Math.Max(32 * 1024 * 1024, _context.TotalRows * 96);
        _plankStream = new MemoryStream(capacity: initialCapacity);
        _sharpStream = new MemoryStream(capacity: initialCapacity);
        _parquetNetStream = new MemoryStream(capacity: initialCapacity);

        _sharpWriterProperties = new WriterPropertiesBuilder()
            .Compression(Compression.Uncompressed)
            .DisableDictionary()
            .Encoding(Encoding.Plain)
            .Build();

        var rowGroupRowCountHint = (uint)_context.TripsByFile.Max(static values => values.RowCount);
        _plankWriter = Writing.ParquetWriter.Create(_plankStream, _plankSchema, new ParquetWriterOptions
        {
            ExpectedRowGroupCount = checked((uint)_context.SourceFiles.Length),
            RowGroupRowCountHint = rowGroupRowCountHint,
            Compression = CompressionKind.None,
            DateTimeKindHandling = DateTimeKindHandling.PreserveClockTime,
            RowGroupOptions = new RowGroupOptions
            {
                MaxCompressedBytes = 32 * 1024 * 1024
            }
        });
        for (var i = 0; i < _workers.Length; i++)
            _workers[i] = new ColumnWorker(this, i);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        for (var i = 0; i < _workers.Length; i++)
            _workers[i]?.Dispose();
        _plankWriter.Dispose();
        _sharpWriterProperties.Dispose();
        _plankStream.Dispose();
        _sharpStream.Dispose();
        _parquetNetStream.Dispose();
    }

    [Benchmark(Baseline = true)]
    public void Plank_WriteNycSchema()
    {
        foreach (var trip in _context.TripsByFile)
        {
            var rowGroup = _plankWriter.StartRowGroup();
            EnsureSync(rowGroup.WriteAsync(_plankSchema.Columns[0], trip.VendorId));
            EnsureSync(rowGroup.WriteAsync(_plankSchema.Columns[1], trip.PickupDateTime));
            EnsureSync(rowGroup.WriteAsync(_plankSchema.Columns[2], trip.DropoffDateTime));
            EnsureSync(rowGroup.WriteAsync(_plankSchema.Columns[3], trip.PassengerCount));
            EnsureSync(rowGroup.WriteAsync(_plankSchema.Columns[4], trip.TripDistance));
            EnsureSync(rowGroup.WriteAsync(_plankSchema.Columns[5], trip.RatecodeId));
            EnsureSync(rowGroup.WriteAsync(_plankSchema.Columns[6], trip.StoreAndFwdFlag));
            EnsureSync(rowGroup.WriteAsync(_plankSchema.Columns[7], trip.PuLocationId));
            EnsureSync(rowGroup.WriteAsync(_plankSchema.Columns[8], trip.DoLocationId));
            EnsureSync(rowGroup.WriteAsync(_plankSchema.Columns[9], trip.PaymentType));
            EnsureSync(rowGroup.WriteAsync(_plankSchema.Columns[10], trip.FareAmount));
            EnsureSync(rowGroup.WriteAsync(_plankSchema.Columns[11], trip.Extra));
            EnsureSync(rowGroup.WriteAsync(_plankSchema.Columns[12], trip.MtaTax));
            EnsureSync(rowGroup.WriteAsync(_plankSchema.Columns[13], trip.TipAmount));
            EnsureSync(rowGroup.WriteAsync(_plankSchema.Columns[14], trip.TollsAmount));
            EnsureSync(rowGroup.WriteAsync(_plankSchema.Columns[15], trip.ImprovementSurcharge));
            EnsureSync(rowGroup.WriteAsync(_plankSchema.Columns[16], trip.TotalAmount));
            EnsureSync(rowGroup.WriteAsync(_plankSchema.Columns[17], trip.CongestionSurcharge));
            EnsureSync(rowGroup.WriteAsync(_plankSchema.Columns[18], trip.AirportFee));
        }

        _plankWriter.CloseFile();
        _plankStream.Position = 0;
        _plankStream.SetLength(0);
        _plankWriter.Reset(_plankStream);
    }

    [Benchmark]
    public async Task Plank_WriteNycSchema_ParallelColumns()
    {
        foreach (var trip in _context.TripsByFile)
        {
            var rowGroup = _plankWriter.StartRowGroup();
            Parallel.For(0, _parallelWrites.Length, _parallelOptions, i => _parallelWrites[i] = WriteColumn(rowGroup, trip, i));
            await AwaitWrites(_parallelWrites).ConfigureAwait(false);
        }

        _plankWriter.CloseFile();
        _plankStream.Position = 0;
        _plankStream.SetLength(0);
        _plankWriter.Reset(_plankStream);
    }

    [Benchmark]
    public async Task Plank_WriteNycSchema_DedicatedThreads()
    {
        foreach (var trip in _context.TripsByFile)
        {
            _workerRowGroup = _plankWriter.StartRowGroup();
            _workerTrip = trip;
            for (var i = 0; i < _workers.Length; i++)
                _workers[i].Schedule();
            for (var i = 0; i < _workers.Length; i++)
                _dedicatedWrites[i] = _workers[i].WaitAndGetWrite();
            await AwaitWrites(_dedicatedWrites).ConfigureAwait(false);
        }

        _plankWriter.CloseFile();
        _plankStream.Position = 0;
        _plankStream.SetLength(0);
        _plankWriter.Reset(_plankStream);
    }

    [Benchmark]
    public void ParquetSharp_WriteNycSchema()
    {
        _sharpStream.Position = 0;
        _sharpStream.SetLength(0);

        using var writer = new ParquetFileWriter(_sharpStream, SharpColumns, null, _sharpWriterProperties, null, true);
        foreach (var trip in _context.TripsByFile)
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

    [Benchmark]
    public async Task ParquetNet_WriteNycSchema()
    {
        _parquetNetStream.Position = 0;
        _parquetNetStream.SetLength(0);

        var schema = new ParquetSchema(
            _vendorId, _pickupDateTime, _dropoffDateTime, _passengerCount, _tripDistance, _ratecodeId, _storeAndFwdFlag,
            _puLocationId, _doLocationId, _paymentType, _fareAmount, _extra, _mtaTax, _tipAmount, _tollsAmount,
            _improvementSurcharge, _totalAmount, _congestionSurcharge, _airportFee);
        await using var writer = await Parquet.ParquetWriter.CreateAsync(schema, _parquetNetStream, ParquetNetOptions).ConfigureAwait(false);
        writer.CompressionMethod = CompressionMethod.None;

        foreach (var trip in _context.TripsByFile)
        {
            using var rowGroupWriter = writer.CreateRowGroup();
            await rowGroupWriter.WriteColumnAsync(new DataColumn(_vendorId, trip.VendorId)).ConfigureAwait(false);
            await rowGroupWriter.WriteColumnAsync(new DataColumn(_pickupDateTime, trip.PickupDateTime)).ConfigureAwait(false);
            await rowGroupWriter.WriteColumnAsync(new DataColumn(_dropoffDateTime, trip.DropoffDateTime)).ConfigureAwait(false);
            await rowGroupWriter.WriteColumnAsync(new DataColumn(_passengerCount, trip.PassengerCount)).ConfigureAwait(false);
            await rowGroupWriter.WriteColumnAsync(new DataColumn(_tripDistance, trip.TripDistance)).ConfigureAwait(false);
            await rowGroupWriter.WriteColumnAsync(new DataColumn(_ratecodeId, trip.RatecodeId)).ConfigureAwait(false);
            await rowGroupWriter.WriteColumnAsync(new DataColumn(_storeAndFwdFlag, trip.StoreAndFwdFlag)).ConfigureAwait(false);
            await rowGroupWriter.WriteColumnAsync(new DataColumn(_puLocationId, trip.PuLocationId)).ConfigureAwait(false);
            await rowGroupWriter.WriteColumnAsync(new DataColumn(_doLocationId, trip.DoLocationId)).ConfigureAwait(false);
            await rowGroupWriter.WriteColumnAsync(new DataColumn(_paymentType, trip.PaymentType)).ConfigureAwait(false);
            await rowGroupWriter.WriteColumnAsync(new DataColumn(_fareAmount, trip.FareAmount)).ConfigureAwait(false);
            await rowGroupWriter.WriteColumnAsync(new DataColumn(_extra, trip.Extra)).ConfigureAwait(false);
            await rowGroupWriter.WriteColumnAsync(new DataColumn(_mtaTax, trip.MtaTax)).ConfigureAwait(false);
            await rowGroupWriter.WriteColumnAsync(new DataColumn(_tipAmount, trip.TipAmount)).ConfigureAwait(false);
            await rowGroupWriter.WriteColumnAsync(new DataColumn(_tollsAmount, trip.TollsAmount)).ConfigureAwait(false);
            await rowGroupWriter.WriteColumnAsync(new DataColumn(_improvementSurcharge, trip.ImprovementSurcharge)).ConfigureAwait(false);
            await rowGroupWriter.WriteColumnAsync(new DataColumn(_totalAmount, trip.TotalAmount)).ConfigureAwait(false);
            await rowGroupWriter.WriteColumnAsync(new DataColumn(_congestionSurcharge, trip.CongestionSurcharge)).ConfigureAwait(false);
            await rowGroupWriter.WriteColumnAsync(new DataColumn(_airportFee, trip.AirportFee)).ConfigureAwait(false);
        }
    }

    static void EnsureSync(ValueTask write)
    {
        if (!write.IsCompletedSuccessfully)
            throw new InvalidOperationException("WriteAsync must complete synchronously in this benchmark.");
    }

    ValueTask WriteColumn(Writing.RowGroupWriter rowGroup, NycTripData trip, int index)
        => index switch
        {
            0 => rowGroup.WriteAsync(_plankSchema.Columns[0], trip.VendorId),
            1 => rowGroup.WriteAsync(_plankSchema.Columns[1], trip.PickupDateTime),
            2 => rowGroup.WriteAsync(_plankSchema.Columns[2], trip.DropoffDateTime),
            3 => rowGroup.WriteAsync(_plankSchema.Columns[3], trip.PassengerCount),
            4 => rowGroup.WriteAsync(_plankSchema.Columns[4], trip.TripDistance),
            5 => rowGroup.WriteAsync(_plankSchema.Columns[5], trip.RatecodeId),
            6 => rowGroup.WriteAsync(_plankSchema.Columns[6], trip.StoreAndFwdFlag),
            7 => rowGroup.WriteAsync(_plankSchema.Columns[7], trip.PuLocationId),
            8 => rowGroup.WriteAsync(_plankSchema.Columns[8], trip.DoLocationId),
            9 => rowGroup.WriteAsync(_plankSchema.Columns[9], trip.PaymentType),
            10 => rowGroup.WriteAsync(_plankSchema.Columns[10], trip.FareAmount),
            11 => rowGroup.WriteAsync(_plankSchema.Columns[11], trip.Extra),
            12 => rowGroup.WriteAsync(_plankSchema.Columns[12], trip.MtaTax),
            13 => rowGroup.WriteAsync(_plankSchema.Columns[13], trip.TipAmount),
            14 => rowGroup.WriteAsync(_plankSchema.Columns[14], trip.TollsAmount),
            15 => rowGroup.WriteAsync(_plankSchema.Columns[15], trip.ImprovementSurcharge),
            16 => rowGroup.WriteAsync(_plankSchema.Columns[16], trip.TotalAmount),
            17 => rowGroup.WriteAsync(_plankSchema.Columns[17], trip.CongestionSurcharge),
            18 => rowGroup.WriteAsync(_plankSchema.Columns[18], trip.AirportFee),
            _ => throw new ArgumentOutOfRangeException(nameof(index), index, "Column index is out of range.")
        };

    static async Task AwaitWrites(ValueTask[] writes)
    {
        foreach (var write in writes)
            await write.ConfigureAwait(false);
    }

    sealed class ColumnWorker : IDisposable
    {
        readonly NycParquetWriteBenchmarks _owner;
        readonly int _index;
        readonly Thread _thread;
        readonly ManualResetEventSlim _start = new(false);
        readonly ManualResetEventSlim _done = new(false);
        volatile bool _stop;
        ValueTask _write;
        Exception? _error;

        public ColumnWorker(NycParquetWriteBenchmarks owner, int index)
        {
            _owner = owner;
            _index = index;
            _thread = new Thread(Run)
            {
                IsBackground = true,
                Name = $"PlankBenchColumnWorker-{index}"
            };
            _thread.Start();
        }

        public void Schedule()
        {
            _done.Reset();
            _start.Set();
        }

        public ValueTask WaitAndGetWrite()
        {
            _done.Wait();
            if (_error is not null)
                throw new InvalidOperationException($"Worker {_index} failed.", _error);
            return _write;
        }

        public void Dispose()
        {
            _stop = true;
            _start.Set();
            _thread.Join();
            _start.Dispose();
            _done.Dispose();
        }

        void Run()
        {
            while (true)
            {
                _start.Wait();
                _start.Reset();
                if (_stop)
                {
                    _done.Set();
                    return;
                }

                try
                {
                    _write = _owner.WriteColumn(_owner._workerRowGroup, _owner._workerTrip, _index);
                    _error = null;
                }
                catch (Exception ex)
                {
                    _write = default;
                    _error = ex;
                }

                _done.Set();
            }
        }
    }
}
