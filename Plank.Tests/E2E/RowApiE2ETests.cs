using Parquet;
using Parquet.Schema;
using ParquetSharp;
using Plank.Schema;
using Plank.Writing;
using PlankColumn = Plank.Schema.Column;
using PlankParquetSchema = Plank.Schema.ParquetSchema;
using PlankParquetWriter = Plank.Writing.ParquetWriter;
using PlankRowGroupWriter = Plank.Writing.RowGroupWriter;

namespace Plank.Tests.E2E;

internal sealed class RowApiE2ETests
{
    [Test]
    public async Task RowWriterBasePipelineWritesRowsReadableByParquetNetAndParquetSharp()
    {
        var path = Path.Combine(Path.GetTempPath(), $"plank-row-pipeline-{Guid.NewGuid():N}.parquet");
        var expected = new[] { 5, 8, 13, 21, 34, 55 };

        try
        {
            using (var stream = File.Create(path))
            {
                var writer = new TestIntPipelineWriter(stream, rowBatchSize: expected.Length, maxParallelism: 2, new ParquetWriterOptions
                {
                    Compression = CompressionKind.Snappy
                });
                for (var i = 0; i < expected.Length; i++)
                {
                    ref var value = ref writer.GetValue();
                    value = expected[i];
                    writer.Next();
                }

                writer.CompleteWriting();
            }

            AssertParquetSharp(path, expected);
            await AssertParquetNetAsync(path, expected).ConfigureAwait(false);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Test]
    public async Task RowWriterBasePipelineWritesPartialBatch()
    {
        var path = Path.Combine(Path.GetTempPath(), $"plank-row-pipeline-partial-{Guid.NewGuid():N}.parquet");
        var expected = new[] { 2, 3, 5 };

        try
        {
            using (var stream = File.Create(path))
            {
                var writer = new TestIntPipelineWriter(stream, rowBatchSize: 8, maxParallelism: 2, new ParquetWriterOptions());
                for (var i = 0; i < expected.Length; i++)
                {
                    ref var value = ref writer.GetValue();
                    value = expected[i];
                    writer.Next();
                }

                writer.CompleteWriting();
            }

            AssertParquetSharp(path, expected);
            await AssertParquetNetAsync(path, expected).ConfigureAwait(false);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Test]
    public async Task RowWriterBasePipelineWritesMultipleBatchesInOrder()
    {
        var path = Path.Combine(Path.GetTempPath(), $"plank-row-pipeline-multi-{Guid.NewGuid():N}.parquet");
        var expected = Enumerable.Range(1, 25).ToArray();

        try
        {
            using (var stream = File.Create(path))
            {
                var writer = new TestIntPipelineWriter(stream, rowBatchSize: 4, maxParallelism: 2, new ParquetWriterOptions
                {
                    Compression = CompressionKind.Snappy
                });
                for (var i = 0; i < expected.Length; i++)
                {
                    ref var value = ref writer.GetValue();
                    value = expected[i];
                    writer.Next();
                }

                writer.CompleteWriting();
                writer.CompleteWriting();
            }

            AssertParquetSharp(path, expected);
            await AssertParquetNetAsync(path, expected).ConfigureAwait(false);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Test]
    public async Task NextBlocksWhenAllWorkersAreBusy()
    {
        var path = Path.Combine(Path.GetTempPath(), $"plank-row-pipeline-blocking-{Guid.NewGuid():N}.parquet");
        var serializeStarted = new ManualResetEventSlim(false);
        var releaseSerialize = new ManualResetEventSlim(false);

        try
        {
            using (var stream = File.Create(path))
            {
                var writer = new BlockingTestIntPipelineWriter(stream, rowBatchSize: 1, maxParallelism: 1,
                    new ParquetWriterOptions(), serializeStarted, releaseSerialize);

                ref var value = ref writer.GetValue();
                value = 42;

                var nextTask = Task.Run(() => writer.Next());
                if (!serializeStarted.Wait(TimeSpan.FromSeconds(2)))
                    throw new InvalidOperationException("Timed out waiting for worker serialization to start.");

                await Task.Delay(150).ConfigureAwait(false);
                if (nextTask.IsCompleted)
                    throw new InvalidOperationException("Next() should block while all workers are busy.");

                releaseSerialize.Set();
                await nextTask.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                writer.CompleteWriting();
            }

            AssertParquetSharp(path, [42]);
            await AssertParquetNetAsync(path, [42]).ConfigureAwait(false);
        }
        finally
        {
            releaseSerialize.Set();
            serializeStarted.Dispose();
            releaseSerialize.Dispose();
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Test]
    public async Task NextBlocksWithTwoWorkersAndFiveColumnsWhenAllSlotsAreBusy()
    {
        var path = Path.Combine(Path.GetTempPath(), $"plank-row-pipeline-blocking-2w-5c-{Guid.NewGuid():N}.parquet");
        using var serializeStarted = new CountdownEvent(2);
        using var releaseSerialize = new ManualResetEventSlim(false);
        var expected = new[]
        {
            new[] { 10, 11, 12, 13, 14 },
            new[] { 20, 21, 22, 23, 24 }
        };

        try
        {
            using (var stream = File.Create(path))
            {
                var writer = new BlockingFiveColumnPipelineWriter(stream, rowBatchSize: 1, maxParallelism: 2,
                    new ParquetWriterOptions(), serializeStarted, releaseSerialize);

                writer.SetCurrentRow(expected[0][0]);
                writer.Next();

                writer.SetCurrentRow(expected[1][0]);
                var nextTask = Task.Run(() => writer.Next());

                if (!serializeStarted.Wait(TimeSpan.FromSeconds(2)))
                    throw new InvalidOperationException("Timed out waiting for both workers to start serialization.");

                await Task.Delay(150).ConfigureAwait(false);
                if (nextTask.IsCompleted)
                    throw new InvalidOperationException("Next() should block when two workers are busy on two slots.");

                releaseSerialize.Set();
                await nextTask.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                writer.CompleteWriting();
            }

            await AssertFiveColumnParquetNetAsync(path, expected).ConfigureAwait(false);
            AssertFiveColumnParquetSharp(path, expected);
        }
        finally
        {
            releaseSerialize.Set();
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    static async Task AssertParquetNetAsync(string path, int[] expected)
    {
        using var stream = File.OpenRead(path);
        using var reader = await ParquetReader.CreateAsync(stream).ConfigureAwait(false);
        var fields = reader.Schema.GetDataFields();
        if (fields.Length != 1)
            throw new InvalidOperationException($"Expected 1 column, got {fields.Length}.");
        var field = GetField(fields, "value");

        var values = new List<int>(expected.Length);
        for (var rowGroupIndex = 0; rowGroupIndex < reader.RowGroupCount; rowGroupIndex++)
        {
            using var rowGroup = reader.OpenRowGroupReader(rowGroupIndex);
            var column = await rowGroup.ReadColumnAsync(field).ConfigureAwait(false);
            if (column.Data is not int[] groupValues)
                throw new InvalidOperationException($"Unexpected Parquet.Net payload type '{column.Data.GetType()}'.");
            values.AddRange(groupValues);
        }

        if (!values.ToArray().AsSpan().SequenceEqual(expected))
            throw new InvalidOperationException("Parquet.Net values mismatch.");
    }

    static void AssertParquetSharp(string path, int[] expected)
    {
        using var reader = new ParquetFileReader(path);
        var values = new List<int>(expected.Length);
        for (var rowGroupIndex = 0; rowGroupIndex < reader.FileMetaData.NumRowGroups; rowGroupIndex++)
        {
            using var rowGroup = reader.RowGroup(rowGroupIndex);
            var rowCount = checked((int)rowGroup.MetaData.NumRows);
            values.AddRange(rowGroup.Column(0).LogicalReader<int>().ReadAll(rowCount));
        }

        if (!values.ToArray().AsSpan().SequenceEqual(expected))
            throw new InvalidOperationException("ParquetSharp values mismatch.");
    }

    static DataField GetField(DataField[] fields, string name)
    {
        for (var i = 0; i < fields.Length; i++)
            if (fields[i].Name == name)
                return fields[i];
        throw new InvalidOperationException($"Could not find field '{name}'.");
    }

    sealed class TestIntPipelineWriter : RowWriterBase<TestIntSlot>
    {
        internal static readonly PlankParquetSchema Schema =
            new([new PlankColumn("value", ParquetPhysicalType.Int32, new ColumnOptions(ParquetRepetition.Required))]);

        readonly int _rowBatchSize;
        TestIntSlot _active;
        bool _completed;

        internal TestIntPipelineWriter(Stream stream, int rowBatchSize, uint maxParallelism, ParquetWriterOptions options)
            : base(stream, Schema, maxParallelism, options)
        {
            if (rowBatchSize < 0)
                throw new ArgumentOutOfRangeException(nameof(rowBatchSize), rowBatchSize, "Row batch size must be non-negative.");

            _rowBatchSize = rowBatchSize;
            InitializeSlots();
            _active = TakeInitialSlot();
            _completed = false;
        }

        protected override TestIntSlot CreateSlot(PlankParquetWriter writer)
            => new(writer, _rowBatchSize);

        protected override void SerializeSlot(TestIntSlot slot)
            => slot.SerializeColumns();

        protected override void WriteSerializedSlot(TestIntSlot slot, PlankRowGroupWriter rowGroupWriter)
            => slot.WriteSerialized(rowGroupWriter);

        protected override void ResetSlotForReuse(TestIntSlot slot)
            => slot.ResetForReuse();

        internal ref int GetValue()
        {
            ThrowIfFaulted();
            if (_completed)
                throw new InvalidOperationException("Pipeline writer is already completed.");
            return ref _active.GetCurrent();
        }

        internal void Next()
        {
            ThrowIfFaulted();
            if (_completed)
                throw new InvalidOperationException("Pipeline writer is already completed.");

            _active.Next();
            if (!_active.IsFull)
                return;

            _active = EnqueueAndTakeFree(_active);
        }

        internal void CompleteWriting()
        {
            ThrowIfFaulted();
            if (_completed)
                return;

            Complete(_active, !_active.IsEmpty);
            _completed = true;
        }
    }

    sealed class TestIntSlot
    {
        int _index;
        readonly int _rowCount;
        readonly int[] _values;
        readonly SerializedColumn _serialized;

        internal TestIntSlot(PlankParquetWriter writer, int rowCount)
        {
            _ = writer ?? throw new ArgumentNullException(nameof(writer));
            _rowCount = rowCount;
            _index = 0;
            _values = rowCount == 0 ? [] : new int[rowCount];
            _serialized = writer.CreateSerializedColumn();
        }

        internal bool IsFull => _index == _rowCount;

        internal bool IsEmpty => _index == 0;

        internal ref int GetCurrent()
        {
            if (_index >= _rowCount)
                throw new InvalidOperationException("No more row slots are available.");
            return ref _values[_index];
        }

        internal void Next()
        {
            if (_index >= _rowCount)
                throw new InvalidOperationException("No more row slots are available.");
            _index++;
        }

        internal void SerializeColumns()
            => _serialized.Serialize(TestIntPipelineWriter.Schema.Columns[0], new ReadOnlySpan<int>(_values, 0, _index));

        internal void WriteSerialized(PlankRowGroupWriter rowGroupWriter)
            => rowGroupWriter.Write(_serialized);

        internal void ResetForReuse()
            => _index = 0;
    }

    sealed class BlockingTestIntPipelineWriter : RowWriterBase<BlockingTestIntSlot>
    {
        readonly int _rowBatchSize;
        readonly ManualResetEventSlim _serializeStarted;
        readonly ManualResetEventSlim _releaseSerialize;
        BlockingTestIntSlot _active;
        bool _completed;

        internal BlockingTestIntPipelineWriter(Stream stream, int rowBatchSize, uint maxParallelism,
            ParquetWriterOptions options, ManualResetEventSlim serializeStarted, ManualResetEventSlim releaseSerialize)
            : base(stream, TestIntPipelineWriter.Schema, maxParallelism, options)
        {
            _rowBatchSize = rowBatchSize;
            _serializeStarted = serializeStarted;
            _releaseSerialize = releaseSerialize;
            InitializeSlots();
            _active = TakeInitialSlot();
        }

        protected override BlockingTestIntSlot CreateSlot(PlankParquetWriter writer)
            => new(writer, _rowBatchSize, _serializeStarted, _releaseSerialize);

        protected override void SerializeSlot(BlockingTestIntSlot slot)
            => slot.SerializeColumns();

        protected override void WriteSerializedSlot(BlockingTestIntSlot slot, PlankRowGroupWriter rowGroupWriter)
            => slot.WriteSerialized(rowGroupWriter);

        protected override void ResetSlotForReuse(BlockingTestIntSlot slot)
            => slot.ResetForReuse();

        internal ref int GetValue()
            => ref _active.GetCurrent();

        internal void Next()
        {
            if (_completed)
                throw new InvalidOperationException("Pipeline writer is already completed.");

            _active.Next();
            if (!_active.IsFull)
                return;

            _active = EnqueueAndTakeFree(_active);
        }

        internal void CompleteWriting()
        {
            if (_completed)
                return;

            Complete(_active, !_active.IsEmpty);
            _completed = true;
        }
    }

    sealed class BlockingTestIntSlot
    {
        int _index;
        readonly int _rowCount;
        readonly int[] _values;
        readonly SerializedColumn _serialized;
        readonly ManualResetEventSlim _serializeStarted;
        readonly ManualResetEventSlim _releaseSerialize;

        internal BlockingTestIntSlot(PlankParquetWriter writer, int rowCount, ManualResetEventSlim serializeStarted,
            ManualResetEventSlim releaseSerialize)
        {
            _rowCount = rowCount;
            _values = rowCount == 0 ? [] : new int[rowCount];
            _serialized = writer.CreateSerializedColumn();
            _serializeStarted = serializeStarted;
            _releaseSerialize = releaseSerialize;
        }

        internal bool IsFull => _index == _rowCount;

        internal bool IsEmpty => _index == 0;

        internal ref int GetCurrent()
            => ref _values[_index];

        internal void Next()
            => _index++;

        internal void SerializeColumns()
        {
            _serializeStarted.Set();
            _releaseSerialize.Wait(TimeSpan.FromSeconds(5));
            _serialized.Serialize(TestIntPipelineWriter.Schema.Columns[0], new ReadOnlySpan<int>(_values, 0, _index));
        }

        internal void WriteSerialized(PlankRowGroupWriter rowGroupWriter)
            => rowGroupWriter.Write(_serialized);

        internal void ResetForReuse()
            => _index = 0;
    }

    sealed class BlockingFiveColumnPipelineWriter : RowWriterBase<BlockingFiveColumnSlot>
    {
        internal static readonly PlankParquetSchema Schema = new([
            new PlankColumn("c0", ParquetPhysicalType.Int32, new ColumnOptions(ParquetRepetition.Required)),
            new PlankColumn("c1", ParquetPhysicalType.Int32, new ColumnOptions(ParquetRepetition.Required)),
            new PlankColumn("c2", ParquetPhysicalType.Int32, new ColumnOptions(ParquetRepetition.Required)),
            new PlankColumn("c3", ParquetPhysicalType.Int32, new ColumnOptions(ParquetRepetition.Required)),
            new PlankColumn("c4", ParquetPhysicalType.Int32, new ColumnOptions(ParquetRepetition.Required))
        ]);

        readonly int _rowBatchSize;
        readonly CountdownEvent _serializeStarted;
        readonly ManualResetEventSlim _releaseSerialize;
        BlockingFiveColumnSlot _active;
        bool _completed;

        internal BlockingFiveColumnPipelineWriter(Stream stream, int rowBatchSize, uint maxParallelism,
            ParquetWriterOptions options, CountdownEvent serializeStarted, ManualResetEventSlim releaseSerialize)
            : base(stream, Schema, maxParallelism, options)
        {
            _rowBatchSize = rowBatchSize;
            _serializeStarted = serializeStarted;
            _releaseSerialize = releaseSerialize;
            InitializeSlots();
            _active = TakeInitialSlot();
        }

        protected override BlockingFiveColumnSlot CreateSlot(PlankParquetWriter writer)
            => new(writer, _rowBatchSize, _serializeStarted, _releaseSerialize);

        protected override void SerializeSlot(BlockingFiveColumnSlot slot)
            => slot.SerializeColumns();

        protected override void WriteSerializedSlot(BlockingFiveColumnSlot slot, PlankRowGroupWriter rowGroupWriter)
            => slot.WriteSerialized(rowGroupWriter);

        protected override void ResetSlotForReuse(BlockingFiveColumnSlot slot)
            => slot.ResetForReuse();

        internal void SetCurrentRow(int baseValue)
            => _active.SetCurrent(baseValue);

        internal void Next()
        {
            if (_completed)
                throw new InvalidOperationException("Pipeline writer is already completed.");
            _active.Next();
            if (!_active.IsFull)
                return;
            _active = EnqueueAndTakeFree(_active);
        }

        internal void CompleteWriting()
        {
            if (_completed)
                return;
            Complete(_active, !_active.IsEmpty);
            _completed = true;
        }
    }

    sealed class BlockingFiveColumnSlot
    {
        int _index;
        readonly int _rowCount;
        readonly int[] _c0;
        readonly int[] _c1;
        readonly int[] _c2;
        readonly int[] _c3;
        readonly int[] _c4;
        readonly SerializedColumn _s0;
        readonly SerializedColumn _s1;
        readonly SerializedColumn _s2;
        readonly SerializedColumn _s3;
        readonly SerializedColumn _s4;
        readonly CountdownEvent _serializeStarted;
        readonly ManualResetEventSlim _releaseSerialize;

        internal BlockingFiveColumnSlot(PlankParquetWriter writer, int rowCount, CountdownEvent serializeStarted,
            ManualResetEventSlim releaseSerialize)
        {
            _rowCount = rowCount;
            _c0 = rowCount == 0 ? [] : new int[rowCount];
            _c1 = rowCount == 0 ? [] : new int[rowCount];
            _c2 = rowCount == 0 ? [] : new int[rowCount];
            _c3 = rowCount == 0 ? [] : new int[rowCount];
            _c4 = rowCount == 0 ? [] : new int[rowCount];
            _s0 = writer.CreateSerializedColumn();
            _s1 = writer.CreateSerializedColumn();
            _s2 = writer.CreateSerializedColumn();
            _s3 = writer.CreateSerializedColumn();
            _s4 = writer.CreateSerializedColumn();
            _serializeStarted = serializeStarted;
            _releaseSerialize = releaseSerialize;
        }

        internal bool IsFull => _index == _rowCount;

        internal bool IsEmpty => _index == 0;

        internal void SetCurrent(int baseValue)
        {
            if (_index >= _rowCount)
                throw new InvalidOperationException("No more row slots are available.");
            _c0[_index] = baseValue;
            _c1[_index] = baseValue + 1;
            _c2[_index] = baseValue + 2;
            _c3[_index] = baseValue + 3;
            _c4[_index] = baseValue + 4;
        }

        internal void Next()
        {
            if (_index >= _rowCount)
                throw new InvalidOperationException("No more row slots are available.");
            _index++;
        }

        internal void SerializeColumns()
        {
            _serializeStarted.Signal();
            _releaseSerialize.Wait(TimeSpan.FromSeconds(5));
            _s0.Serialize(BlockingFiveColumnPipelineWriter.Schema.Columns[0], new ReadOnlySpan<int>(_c0, 0, _index));
            _s1.Serialize(BlockingFiveColumnPipelineWriter.Schema.Columns[1], new ReadOnlySpan<int>(_c1, 0, _index));
            _s2.Serialize(BlockingFiveColumnPipelineWriter.Schema.Columns[2], new ReadOnlySpan<int>(_c2, 0, _index));
            _s3.Serialize(BlockingFiveColumnPipelineWriter.Schema.Columns[3], new ReadOnlySpan<int>(_c3, 0, _index));
            _s4.Serialize(BlockingFiveColumnPipelineWriter.Schema.Columns[4], new ReadOnlySpan<int>(_c4, 0, _index));
        }

        internal void WriteSerialized(PlankRowGroupWriter rowGroupWriter)
        {
            rowGroupWriter.Write(_s0);
            rowGroupWriter.Write(_s1);
            rowGroupWriter.Write(_s2);
            rowGroupWriter.Write(_s3);
            rowGroupWriter.Write(_s4);
        }

        internal void ResetForReuse()
            => _index = 0;
    }

    static async Task AssertFiveColumnParquetNetAsync(string path, int[][] expectedRows)
    {
        using var stream = File.OpenRead(path);
        using var reader = await ParquetReader.CreateAsync(stream).ConfigureAwait(false);
        var fields = reader.Schema.GetDataFields();
        if (fields.Length != 5)
            throw new InvalidOperationException($"Expected 5 fields, got {fields.Length}.");

        var columns = new List<int[]>(5);
        for (var i = 0; i < 5; i++)
            columns.Add([]);

        for (var rg = 0; rg < reader.RowGroupCount; rg++)
        {
            using var rowGroup = reader.OpenRowGroupReader(rg);
            for (var i = 0; i < 5; i++)
            {
                var column = await rowGroup.ReadColumnAsync(GetField(fields, $"c{i}")).ConfigureAwait(false);
                if (column.Data is not int[] values)
                    throw new InvalidOperationException($"Unexpected payload type '{column.Data.GetType()}' for c{i}.");
                columns[i] = columns[i].Concat(values).ToArray();
            }
        }

        if (columns[0].Length != expectedRows.Length)
            throw new InvalidOperationException($"Expected {expectedRows.Length} rows, got {columns[0].Length}.");
        for (var row = 0; row < expectedRows.Length; row++)
            for (var col = 0; col < 5; col++)
                if (columns[col][row] != expectedRows[row][col])
                    throw new InvalidOperationException($"Parquet.Net mismatch at row {row}, col {col}.");
    }

    static void AssertFiveColumnParquetSharp(string path, int[][] expectedRows)
    {
        using var reader = new ParquetFileReader(path);
        var actualColumns = new List<int[]>(5);
        for (var i = 0; i < 5; i++)
            actualColumns.Add([]);

        for (var rg = 0; rg < reader.FileMetaData.NumRowGroups; rg++)
        {
            using var rowGroup = reader.RowGroup(rg);
            var rowCount = checked((int)rowGroup.MetaData.NumRows);
            for (var i = 0; i < 5; i++)
                actualColumns[i] = actualColumns[i].Concat(rowGroup.Column(i).LogicalReader<int>().ReadAll(rowCount)).ToArray();
        }

        if (actualColumns[0].Length != expectedRows.Length)
            throw new InvalidOperationException($"Expected {expectedRows.Length} rows, got {actualColumns[0].Length}.");
        for (var row = 0; row < expectedRows.Length; row++)
            for (var col = 0; col < 5; col++)
                if (actualColumns[col][row] != expectedRows[row][col])
                    throw new InvalidOperationException($"ParquetSharp mismatch at row {row}, col {col}.");
    }
}
