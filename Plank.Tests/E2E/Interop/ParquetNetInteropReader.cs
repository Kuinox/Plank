using Parquet;
using Parquet.Schema;

namespace Plank.Tests.E2E.Interop;

sealed class ParquetNetInteropReader : IParquetInteropReader
{
    public string Name => "Parquet.Net";

    public async Task<ParquetFileReadResult> ReadExpectedSchemaAsync(string path)
    {
        using var stream = File.OpenRead(path);
        await using var reader = await ParquetReader.CreateAsync(stream).ConfigureAwait(false);
        var fields = reader.Schema.GetDataFields();
        var int32Field = GetField(fields, WriterInteropSchema.Int32ColumnName);
        var int64Field = GetField(fields, WriterInteropSchema.Int64ColumnName);
        var doubleField = GetField(fields, WriterInteropSchema.DoubleColumnName);
        var binaryField = GetField(fields, WriterInteropSchema.BinaryColumnName);
        var rowGroups = new List<ParquetRowGroupReadResult>(reader.RowGroupCount);

        for (var rowGroupIndex = 0; rowGroupIndex < reader.RowGroupCount; rowGroupIndex++)
        {
            using var rowGroup = reader.OpenRowGroupReader(rowGroupIndex);
            var rowCount = checked((int)rowGroup.RowCount);
            var int32Values = new int[rowCount];
            var int64Values = new long[rowCount];
            var doubleValues = new double[rowCount];
            var binaryValues = new byte[rowCount][];

            await rowGroup.ReadAsync<int>(int32Field, int32Values, null, default).ConfigureAwait(false);
            await rowGroup.ReadAsync<long>(int64Field, int64Values, null, default).ConfigureAwait(false);
            await rowGroup.ReadAsync<double>(doubleField, doubleValues, null, default).ConfigureAwait(false);
            await rowGroup.ReadAsync(binaryField, binaryValues, null, default).ConfigureAwait(false);

            rowGroups.Add(new ParquetRowGroupReadResult
            {
                Int32Values = int32Values,
                Int64Values = int64Values,
                DoubleValues = doubleValues,
                BinaryValues = binaryValues
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
}
