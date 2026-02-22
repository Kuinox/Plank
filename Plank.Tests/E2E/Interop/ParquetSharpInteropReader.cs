using ParquetSharp;

namespace Plank.Tests.E2E.Interop;

sealed class ParquetSharpInteropReader : IParquetInteropReader
{
    public string Name => "ParquetSharp";

    public Task<ParquetFileReadResult> ReadExpectedSchemaAsync(string path)
    {
        using var reader = new ParquetFileReader(path);
        var rowGroupCount = checked((int)reader.FileMetaData.NumRowGroups);
        var rowGroups = new List<ParquetRowGroupReadResult>(rowGroupCount);
        for (var rowGroupIndex = 0; rowGroupIndex < rowGroupCount; rowGroupIndex++)
        {
            using var rowGroup = reader.RowGroup(rowGroupIndex);
            var rowCount = checked((int)rowGroup.MetaData.NumRows);
            using var int32Reader = rowGroup.Column(0).LogicalReader<int>();
            using var int64Reader = rowGroup.Column(1).LogicalReader<long>();
            using var doubleReader = rowGroup.Column(2).LogicalReader<double>();
            using var binaryReader = rowGroup.Column(3).LogicalReader<byte[]>();

            rowGroups.Add(new ParquetRowGroupReadResult
            {
                Int32Values = int32Reader.ReadAll(rowCount),
                Int64Values = int64Reader.ReadAll(rowCount),
                DoubleValues = doubleReader.ReadAll(rowCount),
                BinaryValues = binaryReader.ReadAll(rowCount)
            });
        }

        return Task.FromResult(new ParquetFileReadResult
        {
            RowGroups = rowGroups
        });
    }
}
