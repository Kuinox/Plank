using Parquet.Schema;

namespace Plank.Benchmarks.Published;

sealed class ParquetNetPublishedBenchmarkReader : IPublishedBenchmarkReader
{
    readonly byte[] _fileBytes;
    readonly PublishedBenchmarkDataSet _dataSet;

    public ParquetNetPublishedBenchmarkReader(byte[] fileBytes, PublishedBenchmarkDataSet dataSet)
    {
        _fileBytes = fileBytes;
        _dataSet = dataSet;
    }

    public string ImplementationId
        => "parquetnet-single";

    public string Label
        => "Parquet.Net (1 thread)";

    public int Threads
        => 1;

    public bool IsSupported
        => _dataSet.Encoding switch
        {
            "rle" => false,
            "delta_binary_packed" or "byte_stream_split"
                when _dataSet.Columns.Any(static column => column.Kind == BenchmarkColumnKind.Timestamp) => false,
            _ => true
        };

    public string? UnavailableReason
        => IsSupported ? null : $"Parquet.Net 6.0.3 cannot decode {_dataSet.Encoding} for this data type.";

    public async ValueTask<PublishedReadResult> ReadAsync(CancellationToken cancellationToken)
    {
        if (!IsSupported)
            throw new NotSupportedException(UnavailableReason);

        using var stream = new MemoryStream(_fileBytes, writable: false);
        await using var reader = await Parquet.ParquetReader.CreateAsync(stream,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        var fields = reader.Schema.GetDataFields();
        if (fields.Length != _dataSet.Columns.Count)
            throw new InvalidDataException(
                $"Parquet.Net found {fields.Length} columns instead of {_dataSet.Columns.Count}.");

        var aggregate = PublishedReadChecksum.Start();
        long valueCount = 0;
        for (var rowGroupIndex = 0; rowGroupIndex < reader.RowGroupCount; rowGroupIndex++)
        {
            using var rowGroup = reader.OpenRowGroupReader(rowGroupIndex);
            var rowCount = checked((int)rowGroup.RowCount);
            for (var columnIndex = 0; columnIndex < fields.Length; columnIndex++)
            {
                var piece = await ReadColumnAsync(rowGroup, fields[columnIndex], _dataSet.Columns[columnIndex],
                    columnIndex, rowGroupIndex, rowCount, cancellationToken).ConfigureAwait(false);
                aggregate = PublishedReadChecksum.Combine(aggregate, piece);
                valueCount = checked(valueCount + piece.ValueCount);
            }
        }
        return new PublishedReadResult(valueCount, aggregate);
    }

    public void Dispose()
    {
    }

    static ValueTask<PublishedReadResult> ReadColumnAsync(Parquet.ParquetRowGroupReader rowGroup,
        DataField field, PublishedBenchmarkDataSet.Column column, int columnIndex, int rowGroupIndex,
        int rowCount, CancellationToken cancellationToken)
        => column.Kind switch
        {
            BenchmarkColumnKind.Boolean => ReadFixedAsync<bool>(rowGroup, field, column.Nullable,
                columnIndex, rowGroupIndex, rowCount, cancellationToken),
            BenchmarkColumnKind.Int32 => ReadFixedAsync<int>(rowGroup, field, column.Nullable,
                columnIndex, rowGroupIndex, rowCount, cancellationToken),
            BenchmarkColumnKind.Int64 => ReadFixedAsync<long>(rowGroup, field, column.Nullable,
                columnIndex, rowGroupIndex, rowCount, cancellationToken),
            BenchmarkColumnKind.Timestamp => ReadFixedAsync<DateTime>(rowGroup, field, column.Nullable,
                columnIndex, rowGroupIndex, rowCount, cancellationToken),
            BenchmarkColumnKind.Double => ReadFixedAsync<double>(rowGroup, field, column.Nullable,
                columnIndex, rowGroupIndex, rowCount, cancellationToken),
            BenchmarkColumnKind.String => ReadStringsAsync(rowGroup, field, columnIndex, rowGroupIndex,
                rowCount, cancellationToken),
            _ => throw new NotSupportedException($"Unsupported column kind '{column.Kind}'.")
        };

    static async ValueTask<PublishedReadResult> ReadFixedAsync<T>(Parquet.ParquetRowGroupReader rowGroup,
        DataField field, bool nullable, int columnIndex, int rowGroupIndex, int rowCount,
        CancellationToken cancellationToken)
        where T : struct
    {
        if (nullable)
        {
            var typed = new T?[rowCount];
            await rowGroup.ReadAsync<T>(field, typed, null, cancellationToken).ConfigureAwait(false);
            return Consume(typed, columnIndex, rowGroupIndex);
        }

        var values = new T[rowCount];
        await rowGroup.ReadAsync<T>(field, values, null, cancellationToken).ConfigureAwait(false);
        return Consume(values, columnIndex, rowGroupIndex);
    }

    static async ValueTask<PublishedReadResult> ReadStringsAsync(Parquet.ParquetRowGroupReader rowGroup,
        DataField field, int columnIndex, int rowGroupIndex, int rowCount, CancellationToken cancellationToken)
    {
        var values = new string?[rowCount];
        await rowGroup.ReadAsync(field, values, null, cancellationToken).ConfigureAwait(false);
        var checksum = PublishedReadChecksum.Accumulator.StartPiece(columnIndex, rowGroupIndex, values.Length);
        for (var valueIndex = 0; valueIndex < values.Length; valueIndex++)
            if (values[valueIndex] is { } value)
                checksum.AddString(value);
            else
                checksum.AddNull();
        return new PublishedReadResult(values.Length, checksum.Finish());
    }

    static PublishedReadResult Consume<T>(ReadOnlySpan<T> values, int columnIndex, int rowGroupIndex)
    {
        var checksum = PublishedReadChecksum.Accumulator.StartPiece(columnIndex, rowGroupIndex, values.Length);
        checksum.AddValues(values);
        return new PublishedReadResult(values.Length, checksum.Finish());
    }
}
