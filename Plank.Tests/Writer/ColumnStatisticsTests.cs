using ParquetSharp;
using Plank.Schema;
using Plank.Writing;
using PlankColumn = Plank.Schema.Column;
using PlankLogicalType = Plank.Schema.LogicalType;
using PlankParquetSchema = Plank.Schema.ParquetSchema;

namespace Plank.Tests.Writer;

internal sealed class ColumnStatisticsTests
{
    [Test]
    public void WriterEmitsColumnChunkStatistics()
    {
        var path = Path.Combine(Path.GetTempPath(), $"plank-statistics-{Guid.NewGuid():N}.parquet");
        var schema = new PlankParquetSchema([
            new PlankColumn("id", ParquetPhysicalType.Int32),
            new PlankColumn("optional_id", ParquetPhysicalType.Int32, new ColumnOptions(ParquetRepetition.Optional)),
            new PlankColumn("name", ParquetPhysicalType.ByteArray, null, new PlankLogicalType.String())
        ]);

        try
        {
            using (var stream = File.Create(path))
            {
                var writer = schema.CreateWriter(stream, new ParquetWriterOptions());
                var idColumn = writer.CreateSerializedColumn<int>(schema.Columns[0]);
                var optionalIdColumn = writer.CreateSerializedColumn<int?>(schema.Columns[1]);
                var nameColumn = writer.CreateSerializedColumn<string>(schema.Columns[2]);
                var rowGroup = writer.StartRowGroup();
                idColumn.Serialize([30, 10, 20]);
                optionalIdColumn.Serialize([3, null, 1]);
                nameColumn.Serialize(["beta", "alpha", "gamma"]);
                rowGroup.Write(idColumn);
                rowGroup.Write(optionalIdColumn);
                rowGroup.Write(nameColumn);
                writer.CloseFile();
            }

            using var reader = new ParquetFileReader(path);
            using var rowGroupMetadata = reader.RowGroup(0);
            AssertInt32Statistics(rowGroupMetadata, columnIndex: 0, min: 10, max: 30, nullCount: 0);
            AssertInt32Statistics(rowGroupMetadata, columnIndex: 1, min: 1, max: 3, nullCount: 1);
            AssertHasMinMaxStatistics(rowGroupMetadata, columnIndex: 2, nullCount: 0);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Test]
    public void WriterEmitsAllNullColumnChunkStatistics()
    {
        var path = Path.Combine(Path.GetTempPath(), $"plank-statistics-all-null-{Guid.NewGuid():N}.parquet");
        var schema = new PlankParquetSchema([
            new PlankColumn("optional_id", ParquetPhysicalType.Int32, new ColumnOptions(ParquetRepetition.Optional))
        ]);

        try
        {
            using (var stream = File.Create(path))
            {
                var writer = schema.CreateWriter(stream, new ParquetWriterOptions());
                var optionalIdColumn = writer.CreateSerializedColumn<int?>(schema.Columns[0]);
                var rowGroup = writer.StartRowGroup();
                optionalIdColumn.Serialize([null, null, null]);
                rowGroup.Write(optionalIdColumn);
                writer.CloseFile();
            }

            using var reader = new ParquetFileReader(path);
            using var rowGroupMetadata = reader.RowGroup(0);
            AssertNoMinMaxStatistics(rowGroupMetadata, columnIndex: 0, nullCount: 3);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Test]
    public void WriterEmitsRepeatedColumnChunkStatistics()
    {
        var path = Path.Combine(Path.GetTempPath(), $"plank-statistics-repeated-{Guid.NewGuid():N}.parquet");
        var schema = new PlankParquetSchema([
            ColumnDef.List("numbers", ColumnDef.RequiredLeaf("element", ParquetPhysicalType.Int32),
                repetition: ParquetRepetition.Required)
        ]);

        int[][] rows =
        [
            [5, 7],
            [],
            [1, 9]
        ];

        try
        {
            using (var stream = File.Create(path))
            {
                var writer = schema.CreateWriter(stream, new ParquetWriterOptions());
                var numbersColumn = writer.CreateSerializedColumn<int[]>(schema.Columns[0]);
                var rowGroup = writer.StartRowGroup();
                numbersColumn.Serialize(rows);
                rowGroup.Write(numbersColumn);
                writer.CloseFile();
            }

            using var reader = new ParquetFileReader(path);
            using var rowGroupMetadata = reader.RowGroup(0);
            AssertInt32Statistics(rowGroupMetadata, columnIndex: 0, min: 1, max: 9, nullCount: 1);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    static void AssertInt32Statistics(RowGroupReader rowGroupMetadata, int columnIndex, int min, int max, long nullCount)
    {
        using var columnMetadata = rowGroupMetadata.MetaData.GetColumnChunkMetaData(columnIndex);
        using var statistics = columnMetadata.Statistics
            ?? throw new InvalidOperationException($"Column {columnIndex} did not write statistics.");
        if (!statistics.HasMinMax)
            throw new InvalidOperationException($"Column {columnIndex} statistics did not include min/max.");
        if (statistics.NullCount != nullCount)
            throw new InvalidOperationException(
                $"Column {columnIndex} null count mismatch. Expected {nullCount}, got {statistics.NullCount}.");
        if (statistics.MinUntyped is not int actualMin || actualMin != min)
            throw new InvalidOperationException(
                $"Column {columnIndex} min mismatch. Expected {min}, got {statistics.MinUntyped}.");
        if (statistics.MaxUntyped is not int actualMax || actualMax != max)
            throw new InvalidOperationException(
                $"Column {columnIndex} max mismatch. Expected {max}, got {statistics.MaxUntyped}.");
    }

    static void AssertHasMinMaxStatistics(RowGroupReader rowGroupMetadata, int columnIndex, long nullCount)
    {
        using var columnMetadata = rowGroupMetadata.MetaData.GetColumnChunkMetaData(columnIndex);
        using var statistics = columnMetadata.Statistics
            ?? throw new InvalidOperationException($"Column {columnIndex} did not write statistics.");
        if (!statistics.HasMinMax)
            throw new InvalidOperationException($"Column {columnIndex} statistics did not include min/max.");
        if (statistics.NullCount != nullCount)
            throw new InvalidOperationException(
                $"Column {columnIndex} null count mismatch. Expected {nullCount}, got {statistics.NullCount}.");
    }

    static void AssertNoMinMaxStatistics(RowGroupReader rowGroupMetadata, int columnIndex, long nullCount)
    {
        using var columnMetadata = rowGroupMetadata.MetaData.GetColumnChunkMetaData(columnIndex);
        using var statistics = columnMetadata.Statistics
            ?? throw new InvalidOperationException($"Column {columnIndex} did not write statistics.");
        if (statistics.HasMinMax)
            throw new InvalidOperationException($"Column {columnIndex} statistics unexpectedly included min/max.");
        if (statistics.NullCount != nullCount)
            throw new InvalidOperationException(
                $"Column {columnIndex} null count mismatch. Expected {nullCount}, got {statistics.NullCount}.");
    }
}
