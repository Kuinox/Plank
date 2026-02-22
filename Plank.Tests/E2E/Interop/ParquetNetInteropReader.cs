using Parquet;
using Parquet.Schema;

namespace Plank.Tests.E2E.Interop;

sealed class ParquetNetInteropReader : IParquetInteropReader
{
    public string Name => "Parquet.Net";

    public async Task<ParquetFileReadResult> ReadExpectedSchemaAsync(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = await ParquetReader.CreateAsync(stream).ConfigureAwait(false);
        var fields = reader.Schema.GetDataFields();
        var int32Field = GetField(fields, WriterInteropSchema.Int32ColumnName);
        var int64Field = GetField(fields, WriterInteropSchema.Int64ColumnName);
        var doubleField = GetField(fields, WriterInteropSchema.DoubleColumnName);
        var binaryField = GetField(fields, WriterInteropSchema.BinaryColumnName);
        var rowGroups = new List<ParquetRowGroupReadResult>(reader.RowGroupCount);

        for (var rowGroupIndex = 0; rowGroupIndex < reader.RowGroupCount; rowGroupIndex++)
        {
            using var rowGroup = reader.OpenRowGroupReader(rowGroupIndex);
            var int32Column = await rowGroup.ReadColumnAsync(int32Field).ConfigureAwait(false);
            var int64Column = await rowGroup.ReadColumnAsync(int64Field).ConfigureAwait(false);
            var doubleColumn = await rowGroup.ReadColumnAsync(doubleField).ConfigureAwait(false);
            var binaryColumn = await rowGroup.ReadColumnAsync(binaryField).ConfigureAwait(false);

            rowGroups.Add(new ParquetRowGroupReadResult
            {
                Int32Values = RequireArray<int[]>(int32Column.Data, WriterInteropSchema.Int32ColumnName),
                Int64Values = RequireArray<long[]>(int64Column.Data, WriterInteropSchema.Int64ColumnName),
                DoubleValues = RequireArray<double[]>(doubleColumn.Data, WriterInteropSchema.DoubleColumnName),
                BinaryValues = RequireArray<byte[][]>(binaryColumn.Data, WriterInteropSchema.BinaryColumnName)
            });
        }

        return new ParquetFileReadResult
        {
            RowGroups = rowGroups
        };
    }

    static DataField GetField(DataField[] fields, string name)
    {
        for (var i = 0; i < fields.Length; i++)
            if (fields[i].Name == name)
                return fields[i];

        throw new InvalidOperationException($"Could not find Parquet field '{name}'.");
    }

    static T RequireArray<T>(Array values, string fieldName)
        where T : class
    {
        if (values is T typed)
            return typed;

        throw new InvalidOperationException(
            $"Parquet.Net returned unexpected value array type '{values.GetType()}' for field '{fieldName}'.");
    }
}
