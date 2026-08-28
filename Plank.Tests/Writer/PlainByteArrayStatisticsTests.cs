using Plank.Schema;
using Plank.Writing;
using Plank.Writing.PageStrategy;
using PlankParquetSchema = Plank.Schema.ParquetSchema;

namespace Plank.Tests.Writer;

/// <summary>
/// The plain BYTE_ARRAY page writer decides the column's min and max while it copies, so these pin
/// the answers it has to keep producing: across page boundaries, for both row shapes, and only where
/// the column really does order its bytes lexicographically.
/// </summary>
internal sealed class PlainByteArrayStatisticsTests
{
    [Test]
    public void MinAndMaxSpanEveryPageOfTheColumn()
    {
        // 16 bytes a row against a 32-byte target puts two rows on a page, so the min sits on the
        // last page and the max on the first. A per-page accumulator that forgot the previous page
        // would report one of them wrong.
        var column = CreateByteArrayColumn(32);

        column.Serialize([
            "mmmmmmmmmmmm"u8.ToArray(),
            "zzzzzzzzzzzz"u8.ToArray(),
            "qqqqqqqqqqqq"u8.ToArray(),
            "rrrrrrrrrrrr"u8.ToArray(),
            "aaaaaaaaaaaa"u8.ToArray()
        ]);

        AssertMinMax(column.Statistics, "aaaaaaaaaaaa"u8, "zzzzzzzzzzzz"u8);
    }

    [Test]
    public void PrefixesCompareByLengthLikeASequenceComparison()
    {
        var column = CreateByteArrayColumn(1024);

        column.Serialize(["ab"u8.ToArray(), "abc"u8.ToArray(), "a"u8.ToArray(), "abcd"u8.ToArray()]);

        AssertMinMax(column.Statistics, "a"u8, "abcd"u8);
    }

    [Test]
    public void AnEmptyValueIsTheSmallestValue()
    {
        var column = CreateByteArrayColumn(1024);

        column.Serialize(["b"u8.ToArray(), [], "a"u8.ToArray()]);

        AssertMinMax(column.Statistics, ""u8, "b"u8);
    }

    [Test]
    public void RepeatedValuesReportTheSameMinAndMax()
    {
        var column = CreateByteArrayColumn(1024);

        column.Serialize(["same"u8.ToArray(), "same"u8.ToArray(), "same"u8.ToArray()]);

        AssertMinMax(column.Statistics, "same"u8, "same"u8);
        if (column.Statistics.DistinctCount != 1)
            throw new InvalidOperationException(
                $"Expected a single distinct value, got {column.Statistics.DistinctCount}.");
    }

    [Test]
    public void HighBytesCompareAsUnsigned()
    {
        var column = CreateByteArrayColumn(1024);

        column.Serialize([new byte[] { 0x7f }, new byte[] { 0xff }, new byte[] { 0x80 }, new byte[] { 0x01 }]);

        AssertMinMax(column.Statistics, [0x01], [0xff]);
    }

    [Test]
    public void MemoryRowShapeReportsTheSameMinAndMax()
    {
        var schema = new PlankParquetSchema([
            Plank.Schema.ColumnDefinition.Leaf("name", ParquetPhysicalType.ByteArray)
        ]);
        using var stream = new MemoryStream();
        var writer = schema.CreateWriter(stream, new ParquetWriterOptions
        {
            TargetDataPageSizeBytes = 32
        });
        var column = writer.CreateSerializedColumn<ReadOnlyMemory<byte>>(schema.LeafColumns[0]);

        column.Serialize([
            "mmmmmmmmmmmm"u8.ToArray(),
            "zzzzzzzzzzzz"u8.ToArray(),
            "qqqqqqqqqqqq"u8.ToArray(),
            "rrrrrrrrrrrr"u8.ToArray(),
            "aaaaaaaaaaaa"u8.ToArray()
        ]);

        AssertMinMax(column.Statistics, "aaaaaaaaaaaa"u8, "zzzzzzzzzzzz"u8);
    }

    [Test]
    public void DecimalColumnsKeepTheirSignedOrdering()
    {
        // Two's complement bytes: 0xFF is -1 and 0x01 is 1, so the decimal order is the reverse of
        // the lexicographic one the page writer tracks. This column has to fall back to the
        // statistics pass that knows the difference.
        var schema = new PlankParquetSchema([
            Plank.Schema.ColumnDefinition.Leaf("amount", ParquetPhysicalType.ByteArray,
                logicalType: new LogicalType.Decimal(9, 2))
        ]);
        using var stream = new MemoryStream();
        var writer = schema.CreateWriter(stream, new ParquetWriterOptions
        {
            TargetDataPageSizeBytes = 1024
        });
        var column = writer.CreateSerializedColumn<byte[]>(schema.LeafColumns[0]);

        column.Serialize([new byte[] { 0x01 }, new byte[] { 0xff }, new byte[] { 0x7f }]);

        AssertMinMax(column.Statistics, [0xff], [0x7f]);
    }

    [Test]
    public void RequiredPlainDecimalPagesKeepTheirSignedOrdering()
    {
        var schema = new PlankParquetSchema([
            Plank.Schema.ColumnDefinition.Leaf("amount", ParquetPhysicalType.ByteArray,
                logicalType: new LogicalType.Decimal(9, 2))
        ]);
        var writer = schema.CreateWriter(new MemoryStream(), new ParquetWriterOptions
        {
            TargetDataPageSizeBytes = 10
        });
        var column = writer.CreateSerializedColumn<byte[]>(schema.LeafColumns[0]);

        column.Serialize([[0x01], [0xff], [0x7f], [0x80]]);

        AssertMinMax(column.Statistics, [0x80], [0x7f]);
        AssertDataPageMinMax(column.Pages, pageIndex: 0, [0xff], [0x01]);
        AssertDataPageMinMax(column.Pages, pageIndex: 1, [0x80], [0x7f]);
    }

    [Test]
    public void OptionalMinAndMaxSpanEveryPageOfTheColumn()
    {
        // Same two-rows-per-page split as the required case, with the extremes on different pages,
        // plus the definition-level byte each row costs against the page budget.
        var column = CreateOptionalByteArrayColumn(36);

        column.Serialize([
            "mmmmmmmmmmmm"u8.ToArray(),
            null,
            "zzzzzzzzzzzz"u8.ToArray(),
            "qqqqqqqqqqqq"u8.ToArray(),
            null,
            "aaaaaaaaaaaa"u8.ToArray()
        ]);

        AssertMinMax(column.Statistics, "aaaaaaaaaaaa"u8, "zzzzzzzzzzzz"u8);
        AssertNullCount(column.Statistics, 2);
    }

    [Test]
    public void OptionalMemoryMinAndMaxAndPageStatisticsSpanEveryPage()
    {
        var column = CreateOptionalMemoryColumn(36);
        ReadOnlyMemory<byte>?[] values =
        [
            "mmmmmmmmmmmm"u8.ToArray(),
            null,
            "zzzzzzzzzzzz"u8.ToArray(),
            "qqqqqqqqqqqq"u8.ToArray(),
            null,
            "aaaaaaaaaaaa"u8.ToArray()
        ];

        column.Serialize(values);

        AssertMinMax(column.Statistics, "aaaaaaaaaaaa"u8, "zzzzzzzzzzzz"u8);
        AssertNullCount(column.Statistics, 2);
        AssertDataPageMinMax(column.Pages, pageIndex: 0, "mmmmmmmmmmmm"u8, "zzzzzzzzzzzz"u8);
        AssertDataPageMinMax(column.Pages, pageIndex: 1, "aaaaaaaaaaaa"u8, "qqqqqqqqqqqq"u8);
    }

    [Test]
    public void OptionalNullsDoNotBecomeTheSmallestValue()
    {
        var column = CreateOptionalByteArrayColumn(1024);

        column.Serialize([null, "b"u8.ToArray(), null, "a"u8.ToArray(), null]);

        AssertMinMax(column.Statistics, "a"u8, "b"u8);
        AssertNullCount(column.Statistics, 3);
    }

    [Test]
    public void AnAllNullOptionalColumnHasNoExtremes()
    {
        var column = CreateOptionalByteArrayColumn(1024);

        column.Serialize([null, null, null]);

        if (!column.Statistics.GetMinValue().IsEmpty || !column.Statistics.GetMaxValue().IsEmpty)
            throw new InvalidOperationException("An all-null column must not report a min or a max.");
        AssertNullCount(column.Statistics, 3);
    }

    [Test]
    public void OptionalDeltaByteArrayColumnsReportTheSameMinAndMax()
    {
        // The delta encodings measure their pages against the plain size too, so they take the same
        // extremes from the same pass.
        var schema = new PlankParquetSchema([
            Plank.Schema.ColumnDefinition.Leaf("name", ParquetPhysicalType.ByteArray,
                new ColumnOptions(ParquetRepetition.Optional, [EncodingKind.DeltaByteArray]))
        ]);
        var writer = schema.CreateWriter(new MemoryStream(), new ParquetWriterOptions
        {
            TargetDataPageSizeBytes = 1024
        });
        var column = writer.CreateSerializedColumn<byte[]?>(schema.LeafColumns[0]);

        column.Serialize(["banana"u8.ToArray(), null, "apple"u8.ToArray(), "cherry"u8.ToArray()]);

        AssertMinMax(column.Statistics, "apple"u8, "cherry"u8);
        AssertNullCount(column.Statistics, 1);
    }

    [Test]
    [Arguments(ParquetDataPageVersion.V1)]
    [Arguments(ParquetDataPageVersion.V2)]
    public void RequiredPlainSizingPreservesPageStatisticsAndFileBytes(
        ParquetDataPageVersion dataPageVersion)
    {
        byte[][] values =
        [
            "mmmmmmmmmm"u8.ToArray(),
            "nnnnnnnnnn"u8.ToArray(),
            "bbbbbbbbbb"u8.ToArray(),
            "yyyyyyyyyy"u8.ToArray(),
            "aaaaaaaaaa"u8.ToArray(),
            "dddddddddd"u8.ToArray(),
            "xxxxxxxxxx"u8.ToArray(),
            "cccccccccc"u8.ToArray(),
            "wwwwwwwwww"u8.ToArray(),
            "zzzzzzzzzz"u8.ToArray(),
            "qqqqqqqqqq"u8.ToArray()
        ];

        var sizedSchema = CreatePlainByteArraySchema();
        using var sizedStream = new MemoryStream();
        var sizedWriter = sizedSchema.CreateWriter(sizedStream, new ParquetWriterOptions
        {
            Compression = CompressionKind.None,
            DataPageVersion = dataPageVersion,
            TargetDataPageSizeBytes = 70
        });
        var sizedColumn = sizedWriter.CreateSerializedColumn<byte[]>(sizedSchema.LeafColumns[0]);
        sizedColumn.Serialize(values);

        AssertMinMax(sizedColumn.Statistics, "aaaaaaaaaa"u8, "zzzzzzzzzz"u8);
        AssertDataPageMinMax(sizedColumn.Pages, pageIndex: 0, "aaaaaaaaaa"u8, "yyyyyyyyyy"u8);
        AssertDataPageMinMax(sizedColumn.Pages, pageIndex: 1, "cccccccccc"u8, "zzzzzzzzzz"u8);
        AssertDataPageMinMax(sizedColumn.Pages, pageIndex: 2, "qqqqqqqqqq"u8, "qqqqqqqqqq"u8);
        sizedWriter.StartRowGroup().Write(sizedColumn);
        sizedWriter.CloseFile();

        var fixedSchema = CreatePlainByteArraySchema(new FixedRowsPageStrategy(5));
        using var fixedStream = new MemoryStream();
        var fixedWriter = fixedSchema.CreateWriter(fixedStream, new ParquetWriterOptions
        {
            Compression = CompressionKind.None,
            DataPageVersion = dataPageVersion,
            TargetDataPageSizeBytes = 70
        });
        var fixedColumn = fixedWriter.CreateSerializedColumn<byte[]>(fixedSchema.LeafColumns[0]);
        fixedColumn.Serialize(values);
        fixedWriter.StartRowGroup().Write(fixedColumn);
        fixedWriter.CloseFile();
        if (!sizedStream.ToArray().AsSpan().SequenceEqual(fixedStream.ToArray()))
            throw new InvalidOperationException(
                $"Fused Plain {dataPageVersion} page statistics changed the Parquet bytes.");
    }

    [Test]
    [Arguments(EncodingKind.DeltaByteArray, ParquetDataPageVersion.V1)]
    [Arguments(EncodingKind.DeltaByteArray, ParquetDataPageVersion.V2)]
    [Arguments(EncodingKind.DeltaLengthByteArray, ParquetDataPageVersion.V1)]
    [Arguments(EncodingKind.DeltaLengthByteArray, ParquetDataPageVersion.V2)]
    public void RequiredDeltaByteArraySizingPreservesPageStatisticsAndFileBytes(EncodingKind encoding,
        ParquetDataPageVersion dataPageVersion)
    {
        byte[][] values =
        [
            "mmmmmmmmmm"u8.ToArray(),
            "nnnnnnnnnn"u8.ToArray(),
            "bbbbbbbbbb"u8.ToArray(),
            "yyyyyyyyyy"u8.ToArray(),
            "aaaaaaaaaa"u8.ToArray(),
            "dddddddddd"u8.ToArray(),
            "xxxxxxxxxx"u8.ToArray(),
            "cccccccccc"u8.ToArray(),
            "wwwwwwwwww"u8.ToArray(),
            "zzzzzzzzzz"u8.ToArray(),
            "qqqqqqqqqq"u8.ToArray()
        ];

        var sizedSchema = CreateDeltaByteArraySchema(encoding);
        using var sizedStream = new MemoryStream();
        var sizedWriter = sizedSchema.CreateWriter(sizedStream, new ParquetWriterOptions
        {
            Compression = CompressionKind.None,
            DataPageVersion = dataPageVersion,
            TargetDataPageSizeBytes = 70
        });
        var sizedColumn = sizedWriter.CreateSerializedColumn<byte[]>(sizedSchema.LeafColumns[0]);
        sizedColumn.Serialize(values);

        AssertMinMax(sizedColumn.Statistics, "aaaaaaaaaa"u8, "zzzzzzzzzz"u8);
        AssertDataPageMinMax(sizedColumn.Pages, pageIndex: 0, "aaaaaaaaaa"u8, "yyyyyyyyyy"u8);
        AssertDataPageMinMax(sizedColumn.Pages, pageIndex: 1, "cccccccccc"u8, "zzzzzzzzzz"u8);
        AssertDataPageMinMax(sizedColumn.Pages, pageIndex: 2, "qqqqqqqqqq"u8, "qqqqqqqqqq"u8);
        sizedWriter.StartRowGroup().Write(sizedColumn);
        sizedWriter.CloseFile();
        var sizedFile = sizedStream.ToArray();

        // Five ten-byte payloads occupy exactly 70 plain bytes. A fixed five-row strategy therefore creates
        // the same boundaries without taking the fused target-size path, giving us a byte-for-byte
        // reference for the observable Parquet output.
        var fixedSchema = CreateDeltaByteArraySchema(encoding, new FixedRowsPageStrategy(5));
        using var fixedStream = new MemoryStream();
        var fixedWriter = fixedSchema.CreateWriter(fixedStream, new ParquetWriterOptions
        {
            Compression = CompressionKind.None,
            DataPageVersion = dataPageVersion,
            TargetDataPageSizeBytes = 70
        });
        var fixedColumn = fixedWriter.CreateSerializedColumn<byte[]>(fixedSchema.LeafColumns[0]);
        fixedColumn.Serialize(values);
        fixedWriter.StartRowGroup().Write(fixedColumn);
        fixedWriter.CloseFile();
        if (!sizedFile.AsSpan().SequenceEqual(fixedStream.ToArray()))
            throw new InvalidOperationException(
                $"Fused {encoding} {dataPageVersion} page sizing changed the Parquet bytes.");

        using var reader = new ParquetSharp.ParquetFileReader(new MemoryStream(sizedFile), leaveOpen: false);
        using var rowGroup = reader.RowGroup(0);
        using var logicalReader = rowGroup.Column(0).LogicalReader<string>();
        var actual = logicalReader.ReadAll(values.Length);
        for (var i = 0; i < values.Length; i++)
            if (!string.Equals(actual[i], System.Text.Encoding.UTF8.GetString(values[i]), StringComparison.Ordinal))
                throw new InvalidOperationException($"{encoding} {dataPageVersion} round-trip mismatch at row {i}.");
    }

    [Test]
    [Arguments(ParquetDataPageVersion.V1)]
    [Arguments(ParquetDataPageVersion.V2)]
    public void RequiredDeltaByteArrayPrecomputedLengthsResetAtPageBoundaries(
        ParquetDataPageVersion dataPageVersion)
    {
        // Every payload is 12 bytes, so a 48-byte plain-size target makes exact three-row pages.
        // The first value on pages two and three shares a long prefix with the preceding page's
        // final value. DELTA_BYTE_ARRAY must still start each page with a zero prefix length, and
        // repeated values exercise a zero-byte suffix in the retained column scratch spans.
        byte[][] values =
        [
            "shared-00000"u8.ToArray(),
            "shared-00001"u8.ToArray(),
            "shared-00001"u8.ToArray(),
            "shared-00002"u8.ToArray(),
            "shared-10002"u8.ToArray(),
            "shared-10003"u8.ToArray(),
            "shared-10003"u8.ToArray(),
            "shared-10000"u8.ToArray(),
            "shared-zzzzz"u8.ToArray()
        ];

        var sizedSchema = CreateDeltaByteArraySchema(EncodingKind.DeltaByteArray);
        using var sizedStream = new MemoryStream();
        var sizedWriter = sizedSchema.CreateWriter(sizedStream, new ParquetWriterOptions
        {
            Compression = CompressionKind.None,
            DataPageVersion = dataPageVersion,
            TargetDataPageSizeBytes = 48
        });
        var sizedColumn = sizedWriter.CreateSerializedColumn<byte[]>(sizedSchema.LeafColumns[0]);
        sizedColumn.Serialize(values);
        sizedWriter.StartRowGroup().Write(sizedColumn);
        sizedWriter.CloseFile();

        var fixedSchema = CreateDeltaByteArraySchema(EncodingKind.DeltaByteArray,
            new FixedRowsPageStrategy(3));
        using var fixedStream = new MemoryStream();
        var fixedWriter = fixedSchema.CreateWriter(fixedStream, new ParquetWriterOptions
        {
            Compression = CompressionKind.None,
            DataPageVersion = dataPageVersion,
            TargetDataPageSizeBytes = 48
        });
        var fixedColumn = fixedWriter.CreateSerializedColumn<byte[]>(fixedSchema.LeafColumns[0]);
        fixedColumn.Serialize(values);
        fixedWriter.StartRowGroup().Write(fixedColumn);
        fixedWriter.CloseFile();

        var sizedFile = sizedStream.ToArray();
        if (!sizedFile.AsSpan().SequenceEqual(fixedStream.ToArray()))
            throw new InvalidOperationException(
                $"Precomputed DELTA_BYTE_ARRAY {dataPageVersion} lengths changed the Parquet bytes.");

        using var reader = new ParquetSharp.ParquetFileReader(new MemoryStream(sizedFile), leaveOpen: false);
        using var rowGroup = reader.RowGroup(0);
        using var logicalReader = rowGroup.Column(0).LogicalReader<string>();
        var actual = logicalReader.ReadAll(values.Length);
        for (var i = 0; i < values.Length; i++)
            if (!string.Equals(actual[i], System.Text.Encoding.UTF8.GetString(values[i]), StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"Precomputed DELTA_BYTE_ARRAY {dataPageVersion} round-trip mismatch at row {i}.");
    }

    [Test]
    [Arguments(ParquetDataPageVersion.V1)]
    [Arguments(ParquetDataPageVersion.V2)]
    public void RequiredDeltaByteArrayLargeLengthsUseByteIdenticalFallback(
        ParquetDataPageVersion dataPageVersion)
    {
        byte[][] values =
        [
            Enumerable.Repeat((byte)'a', 300).ToArray(),
            Enumerable.Repeat((byte)'b', 300).ToArray(),
            Enumerable.Repeat((byte)'c', 300).ToArray(),
            Enumerable.Repeat((byte)'d', 300).ToArray()
        ];

        var sizedSchema = CreateDeltaByteArraySchema(EncodingKind.DeltaByteArray);
        using var sizedStream = new MemoryStream();
        var sizedWriter = sizedSchema.CreateWriter(sizedStream, new ParquetWriterOptions
        {
            Compression = CompressionKind.None,
            DataPageVersion = dataPageVersion,
            TargetDataPageSizeBytes = 608
        });
        var sizedColumn = sizedWriter.CreateSerializedColumn<byte[]>(sizedSchema.LeafColumns[0]);
        sizedColumn.Serialize(values);
        sizedWriter.StartRowGroup().Write(sizedColumn);
        sizedWriter.CloseFile();

        var fixedSchema = CreateDeltaByteArraySchema(EncodingKind.DeltaByteArray,
            new FixedRowsPageStrategy(2));
        using var fixedStream = new MemoryStream();
        var fixedWriter = fixedSchema.CreateWriter(fixedStream, new ParquetWriterOptions
        {
            Compression = CompressionKind.None,
            DataPageVersion = dataPageVersion,
            TargetDataPageSizeBytes = 608
        });
        var fixedColumn = fixedWriter.CreateSerializedColumn<byte[]>(fixedSchema.LeafColumns[0]);
        fixedColumn.Serialize(values);
        fixedWriter.StartRowGroup().Write(fixedColumn);
        fixedWriter.CloseFile();

        if (!sizedStream.ToArray().AsSpan().SequenceEqual(fixedStream.ToArray()))
            throw new InvalidOperationException(
                $"Large DELTA_BYTE_ARRAY {dataPageVersion} lengths changed the Parquet bytes.");
    }

    [Test]
    public void RequiredDeltaByteArrayDecimalsKeepSignedPageAndColumnOrdering()
    {
        var schema = new PlankParquetSchema([
            Plank.Schema.ColumnDefinition.Leaf("amount", ParquetPhysicalType.ByteArray,
                new ColumnOptions(encodings: [EncodingKind.DeltaByteArray]),
                new LogicalType.Decimal(3, 0))
        ]);
        using var stream = new MemoryStream();
        var writer = schema.CreateWriter(stream, new ParquetWriterOptions
        {
            TargetDataPageSizeBytes = 10
        });
        var column = writer.CreateSerializedColumn<byte[]>(schema.LeafColumns[0]);

        column.Serialize([[0x01], [0xff], [0x7f], [0x80]]);

        AssertMinMax(column.Statistics, [0x80], [0x7f]);
        AssertDataPageMinMax(column.Pages, pageIndex: 0, [0xff], [0x01]);
        AssertDataPageMinMax(column.Pages, pageIndex: 1, [0x80], [0x7f]);
    }

    [Test]
    public void OptionalDecimalColumnsKeepTheirSignedOrdering()
    {
        var schema = new PlankParquetSchema([
            Plank.Schema.ColumnDefinition.Leaf("amount", ParquetPhysicalType.ByteArray,
                new ColumnOptions(ParquetRepetition.Optional), new LogicalType.Decimal(9, 2))
        ]);
        var writer = schema.CreateWriter(new MemoryStream(), new ParquetWriterOptions
        {
            TargetDataPageSizeBytes = 1024
        });
        var column = writer.CreateSerializedColumn<byte[]?>(schema.LeafColumns[0]);

        column.Serialize([new byte[] { 0x01 }, null, new byte[] { 0xff }, new byte[] { 0x7f }]);

        AssertMinMax(column.Statistics, [0xff], [0x7f]);
        AssertNullCount(column.Statistics, 1);
    }

    static SerializedColumn<byte[]?> CreateOptionalByteArrayColumn(uint targetDataPageSizeBytes)
    {
        var schema = new PlankParquetSchema([
            Plank.Schema.ColumnDefinition.Leaf("name", ParquetPhysicalType.ByteArray,
                new ColumnOptions(ParquetRepetition.Optional))
        ]);
        var writer = schema.CreateWriter(new MemoryStream(), new ParquetWriterOptions
        {
            TargetDataPageSizeBytes = targetDataPageSizeBytes
        });
        return writer.CreateSerializedColumn<byte[]?>(schema.LeafColumns[0]);
    }

    static SerializedColumn<ReadOnlyMemory<byte>?> CreateOptionalMemoryColumn(uint targetDataPageSizeBytes)
    {
        var schema = new PlankParquetSchema([
            Plank.Schema.ColumnDefinition.Leaf("name", ParquetPhysicalType.ByteArray,
                new ColumnOptions(ParquetRepetition.Optional))
        ]);
        var writer = schema.CreateWriter(new MemoryStream(), new ParquetWriterOptions
        {
            TargetDataPageSizeBytes = targetDataPageSizeBytes
        });
        return writer.CreateSerializedColumn<ReadOnlyMemory<byte>?>(schema.LeafColumns[0]);
    }

    static void AssertNullCount(ColumnStatistics statistics, long expected)
    {
        if (statistics.NullCount != expected)
            throw new InvalidOperationException(
                $"Null count mismatch. Expected {expected}, got {statistics.NullCount}.");
    }

    static SerializedColumn<byte[]> CreateByteArrayColumn(uint targetDataPageSizeBytes)
    {
        var schema = new PlankParquetSchema([
            Plank.Schema.ColumnDefinition.Leaf("name", ParquetPhysicalType.ByteArray)
        ]);
        var writer = schema.CreateWriter(new MemoryStream(), new ParquetWriterOptions
        {
            TargetDataPageSizeBytes = targetDataPageSizeBytes
        });
        return writer.CreateSerializedColumn<byte[]>(schema.LeafColumns[0]);
    }

    static PlankParquetSchema CreateDeltaByteArraySchema(EncodingKind encoding, IPageStrategy? pageStrategy = null)
        => new([
            Plank.Schema.ColumnDefinition.Leaf("name", ParquetPhysicalType.ByteArray,
                new ColumnOptions(encodings: [encoding]), logicalType: new LogicalType.String(),
                pageStrategy: pageStrategy)
        ]);

    static PlankParquetSchema CreatePlainByteArraySchema(IPageStrategy? pageStrategy = null)
        => new([
            Plank.Schema.ColumnDefinition.Leaf("name", ParquetPhysicalType.ByteArray,
                new ColumnOptions(encodings: [EncodingKind.Plain]), logicalType: new LogicalType.String(),
                pageStrategy: pageStrategy)
        ]);

    static void AssertDataPageMinMax(PageList pages, int pageIndex, ReadOnlySpan<byte> min, ReadOnlySpan<byte> max)
    {
        var dataPageIndex = 0;
        for (var i = 0; i < pages.Count; i++)
        {
            ref var page = ref pages[i];
            if (page.Kind != PageKind.DataV2)
                continue;
            if (dataPageIndex == pageIndex)
            {
                AssertMinMax(page.Statistics, min, max);
                return;
            }
            dataPageIndex++;
        }

        throw new InvalidOperationException($"Data page {pageIndex} was not written.");
    }

    static void AssertMinMax(ColumnStatistics statistics, ReadOnlySpan<byte> min, ReadOnlySpan<byte> max)
    {
        if (statistics.ValueKind != ColumnStatistics.ColumnStatisticsValueKind.Binary)
            throw new InvalidOperationException($"Expected binary statistics, got {statistics.ValueKind}.");
        if (!statistics.GetMinValue().SequenceEqual(min))
            throw new InvalidOperationException(
                $"Min mismatch. Expected '{Describe(min)}', got '{Describe(statistics.GetMinValue())}'.");
        if (!statistics.GetMaxValue().SequenceEqual(max))
            throw new InvalidOperationException(
                $"Max mismatch. Expected '{Describe(max)}', got '{Describe(statistics.GetMaxValue())}'.");
    }

    static string Describe(ReadOnlySpan<byte> value) => Convert.ToHexString(value);

    sealed class FixedRowsPageStrategy(uint rowsPerPage) : IPageStrategy
    {
        public DictionaryMode GetDictionaryMode()
            => DictionaryMode.Disabled;

        public bool ShouldDropDictionary(uint uniqueCount, uint totalRowCount, uint rowsSeen)
            => false;

        public uint GetNextDataPageRowCount(uint totalRowCount, uint rowsWritten)
            => Math.Min(rowsPerPage, totalRowCount - rowsWritten);
    }
}
