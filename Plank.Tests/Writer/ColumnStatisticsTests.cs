using System.Buffers.Binary;
using System.Collections.Immutable;
using ParquetSharp;
using Plank.Reading;
using Plank.Schema;
using Plank.Writing;
using Plank.Writing.PageStrategy;
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

    [Test]
    public void WriterEmitsPageIndexes()
    {
        var path = Path.Combine(Path.GetTempPath(), $"plank-page-index-{Guid.NewGuid():N}.parquet");
        var schema = new PlankParquetSchema([
            new PlankColumn("id", ParquetPhysicalType.Int32),
            new PlankColumn("optional_id", ParquetPhysicalType.Int32, new ColumnOptions(ParquetRepetition.Optional))
        ])
        {
            PageStrategiesByColumnName = ImmutableDictionary<string, IPageStrategy>.Empty
                .Add("id", new FixedRowsPageStrategy(2))
                .Add("optional_id", new FixedRowsPageStrategy(2))
        };

        try
        {
            using (var stream = File.Create(path))
            {
                var writer = schema.CreateWriter(stream, new ParquetWriterOptions());
                var idColumn = writer.CreateSerializedColumn<int>(schema.Columns[0]);
                var optionalIdColumn = writer.CreateSerializedColumn<int?>(schema.Columns[1]);
                var rowGroup = writer.StartRowGroup();
                idColumn.Serialize([10, 20, 30, 40, 50]);
                optionalIdColumn.Serialize([1, null, 3, 4, null]);
                rowGroup.Write(idColumn);
                rowGroup.Write(optionalIdColumn);
                writer.CloseFile();
            }

            var footer = ReadFooter(path);
            var columns = footer.RowGroups[0].Columns;
            var fileBytes = File.ReadAllBytes(path);
            AssertPageIndexMetadata(fileBytes, columns[0], expectedPageCount: 3);
            AssertPageIndexMetadata(fileBytes, columns[1], expectedPageCount: 3);

            using var reader = new ParquetFileReader(path);
            using var rowGroupMetadata = reader.RowGroup(0);
            AssertInt32Statistics(rowGroupMetadata, columnIndex: 0, min: 10, max: 50, nullCount: 0);
            AssertInt32Statistics(rowGroupMetadata, columnIndex: 1, min: 1, max: 4, nullCount: 2);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    static void AssertInt32Statistics(ParquetSharp.RowGroupReader rowGroupMetadata, int columnIndex, int min, int max,
        long nullCount)
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

    static void AssertHasMinMaxStatistics(ParquetSharp.RowGroupReader rowGroupMetadata, int columnIndex, long nullCount)
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

    static void AssertNoMinMaxStatistics(ParquetSharp.RowGroupReader rowGroupMetadata, int columnIndex, long nullCount)
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

    static void AssertPageIndexMetadata(byte[] fileBytes, InternalColumnChunkMetadata column, int expectedPageCount)
    {
        if (column.ColumnIndexOffset <= 0)
            throw new InvalidOperationException("Column index offset was not written.");
        if (column.ColumnIndexLength <= 0)
            throw new InvalidOperationException("Column index length was not written.");
        if (column.OffsetIndexOffset <= 0)
            throw new InvalidOperationException("Offset index offset was not written.");
        if (column.OffsetIndexLength <= 0)
            throw new InvalidOperationException("Offset index length was not written.");
        if (column.ColumnIndexOffset < column.ChunkOffset)
            throw new InvalidOperationException("Column index was written before the column chunk.");
        if (column.OffsetIndexOffset <= column.ColumnIndexOffset)
            throw new InvalidOperationException("Offset index was not written after the column index.");
        var actualStatisticsCount = ReadColumnIndexPageCount(fileBytes, column.ColumnIndexOffset, column.ColumnIndexLength);
        if (actualStatisticsCount != expectedPageCount)
            throw new InvalidOperationException(
                $"Column index page count mismatch. Expected {expectedPageCount}, got {actualStatisticsCount}.");
        var actualPageCount = ReadOffsetIndexPageCount(fileBytes, column.OffsetIndexOffset, column.OffsetIndexLength);
        if (actualPageCount != expectedPageCount)
            throw new InvalidOperationException(
                $"Offset index page count mismatch. Expected {expectedPageCount}, got {actualPageCount}.");
    }

    static InternalParquetFooter ReadFooter(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var trailer = bytes.AsSpan(bytes.Length - 8, 8);
        var footerLength = BinaryPrimitives.ReadInt32LittleEndian(trailer[..4]);
        var footerOffset = bytes.Length - 8 - footerLength;
        return ParquetMetadataThriftReader.Read(bytes.AsSpan(footerOffset, footerLength), footerOffset);
    }

    static int ReadColumnIndexPageCount(byte[] fileBytes, long offset, int length)
    {
        var reader = new CompactProtocolReader(fileBytes.AsSpan(checked((int)offset), length));
        var previousFieldId = 0;
        while (reader.TryReadFieldHeader(ref previousFieldId, out var fieldId, out var type, out var inlineBool))
        {
            if (fieldId != 1)
            {
                reader.Skip(type, inlineBool);
                continue;
            }

            var (count, elementType) = reader.ReadListHeader();
            if (elementType is not (CompactProtocolType.BooleanTrue or CompactProtocolType.BooleanFalse))
                throw new InvalidOperationException("Column index null_pages was not a bool list.");
            for (var i = 0; i < count; i++)
                _ = reader.ReadBool(inlineBool: null);
            return count;
        }

        throw new InvalidOperationException("Column index did not include null page flags.");
    }

    static int ReadOffsetIndexPageCount(byte[] fileBytes, long offset, int length)
    {
        var reader = new CompactProtocolReader(fileBytes.AsSpan(checked((int)offset), length));
        var previousFieldId = 0;
        while (reader.TryReadFieldHeader(ref previousFieldId, out var fieldId, out var type, out var inlineBool))
        {
            if (fieldId != 1)
            {
                reader.Skip(type, inlineBool);
                continue;
            }

            var (count, elementType) = reader.ReadListHeader();
            if (elementType != CompactProtocolType.Struct)
                throw new InvalidOperationException("Offset index page_locations was not a struct list.");
            return count;
        }

        throw new InvalidOperationException("Offset index did not include page locations.");
    }

    sealed class FixedRowsPageStrategy : IPageStrategy
    {
        readonly int _rowsPerPage;

        internal FixedRowsPageStrategy(int rowsPerPage)
            => _rowsPerPage = rowsPerPage;

        public DictionaryMode GetDictionaryMode()
            => DictionaryMode.Disabled;

        public DictionarySortOrder GetDictionarySortOrder()
            => DictionarySortOrder.Unknown;

        public void SetDictionarySortOrder(DictionarySortOrder sortOrder)
        {
        }

        public bool ShouldDropDictionary(int uniqueCount, int totalRowCount, int rowsSeen)
            => false;

        public bool ShouldStartNewDataPage(int totalRowCount, int rowsWritten, int currentPageRowCount)
            => currentPageRowCount >= _rowsPerPage;
    }
}
