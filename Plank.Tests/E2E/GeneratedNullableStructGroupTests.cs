using Plank.Dataset;
using Plank.Reading;
using Plank.Schema;
using Plank.Writing;

namespace Plank.Tests.E2E;

internal sealed class GeneratedNullableStructGroupTests
{
    [Test]
    [Arguments(ParquetDataPageVersion.V1, false)]
    [Arguments(ParquetDataPageVersion.V1, true)]
    [Arguments(ParquetDataPageVersion.V2, false)]
    [Arguments(ParquetDataPageVersion.V2, true)]
    public void NullableStructGroupsRoundTrip(ParquetDataPageVersion pageVersion, bool useCursor)
    {
        var expected = CreateRows();
        using var stream = new MemoryStream();
        using (var writer = NullableStructGroupRow.CreateRowWriter(stream, maxParallelism: 1,
                   new ParquetWriterOptions { Compression = CompressionKind.None, DataPageVersion = pageVersion }))
        {
            var cursor = writer.CreateCursor();
            foreach (var value in expected)
            {
                if (useCursor)
                {
                    cursor.NextRow();
                    cursor.Position = value.Position;
                    cursor.Details = value.Details;
                    cursor.Metrics = value.Metrics;
                }
                else
                {
                    var row = writer.GetRow();
                    row.Position = value.Position;
                    row.Details = value.Details;
                    row.Metrics = value.Metrics;
                }
            }
            writer.Complete();
        }

        AssertRows(stream.ToArray(), expected);
    }

    [Test]
    public void DatasetWriterRoundTripsNullableStructGroups()
    {
        var expected = CreateRows();
        using var file = new MemoryDatasetFile();
        using (var writer = NullableStructGroupRow.CreateDatasetWriter(Route, [file], new DatasetWriterOptions
               {
                   WriterOptions = new ParquetWriterOptions { Compression = CompressionKind.None }
               }))
            foreach (var value in expected)
                writer.Queue(value);

        AssertRows(file.ToArray(), expected);
    }

    static NullableStructGroupRow[] CreateRows()
    {
        NullableStructPoint?[] positions = [null, new() { X = 42, Y = -99 }, new NullableStructPoint(), null,
            new() { X = int.MinValue, Y = int.MaxValue }];
        return positions.Select((position, index) => new NullableStructGroupRow
        {
            Position = position,
            Details = new NullableStructContainer { Position = positions[positions.Length - index - 1], Id = index },
            Metrics = new NullableStructMetrics { Count = index % 2 == 0 ? null : index }
        }).ToArray();
    }

    static void AssertRows(byte[] bytes, NullableStructGroupRow[] expected)
    {
        using var stream = new MemoryStream(bytes);
        using var reader = NullableStructGroupRow.CreateRowReader(stream);
        var index = 0;
        foreach (var row in reader)
        {
            if (index >= expected.Length || !Nullable.Equals(row.Position, expected[index].Position) ||
                !Nullable.Equals(row.Details.Position, expected[index].Details.Position) ||
                row.Details.Id != expected[index].Details.Id || row.Metrics.Count != expected[index].Metrics.Count)
                throw new InvalidOperationException($"Nullable struct values or presence differ at row {index}.");
            index++;
        }
        if (index != expected.Length)
            throw new InvalidOperationException($"Expected {expected.Length} rows, read {index}.");
    }

    static ReadOnlySpan<byte> Route(NullableStructGroupRow row, IParquetBufferPool bufferPool,
        out ParquetBuffer? allocation)
    {
        allocation = null;
        return "nullable-struct.parquet"u8;
    }

    sealed class MemoryDatasetFile : IParquetReadSource, IParquetWriteSource
    {
        readonly MemoryStream _stream = new();

        public ulong Length => checked((ulong)_stream.Length);

        internal byte[] ToArray() => _stream.ToArray();

        public void Open(ReadOnlySpan<byte> path, FileMode mode)
        {
            if (mode is FileMode.Create or FileMode.CreateNew)
                _stream.SetLength(0);
        }

        public void Close() { }

        public void Flush() { }

        public void ReadExactly(ulong offset, Span<byte> destination)
        {
            _stream.Position = checked((long)offset);
            _stream.ReadExactly(destination);
        }

        public void Write(ulong offset, ReadOnlySpan<byte> source)
        {
            _stream.Position = checked((long)offset);
            _stream.Write(source);
        }

        public void SetLength(ulong length) => _stream.SetLength(checked((long)length));

        public void Dispose() => _stream.Dispose();
    }
}

[ParquetSchema(AllowAllocatingValues = true)]
internal sealed partial class NullableStructGroupRow
{
    public NullableStructPoint? Position { get; set; }

    public NullableStructContainer Details { get; set; }

    public NullableStructMetrics Metrics { get; set; }
}

internal struct NullableStructPoint
{
    [ParquetColumn("x_coordinate")]
    public int X { get; set; }

    public int Y { get; init; }
}

internal struct NullableStructContainer
{
    public NullableStructPoint? Position { get; set; }

    public int Id { get; set; }
}

internal struct NullableStructMetrics
{
    public int? Count { get; set; }
}
