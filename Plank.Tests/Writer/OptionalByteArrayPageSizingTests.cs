using Plank.Schema;
using Plank.Writing;
using Plank.Writing.PageStrategy;
using PlankParquetSchema = Plank.Schema.ParquetSchema;

namespace Plank.Tests.Writer;

/// <summary>
/// The optional byte-array page sizer hands the payload size it measured to the page writer instead of
/// letting it walk the rows again. That only holds for variable-length rows, whose measurement includes
/// the plain length prefix the writer goes on to emit - a fixed-length column measures rows without one,
/// so it has to keep counting for itself. Both shapes are round-tripped here across several pages.
/// </summary>
internal sealed class OptionalByteArrayPageSizingTests
{
    [Test]
    [Arguments(EncodingKind.Plain)]
    [Arguments(EncodingKind.DeltaByteArray)]
    [Arguments(EncodingKind.DeltaLengthByteArray)]
    [Arguments(EncodingKind.RleDictionary)]
    public void VariableLengthValuesRoundTripAcrossPages(EncodingKind encoding)
    {
        var values = new byte[400][];
        for (var i = 0; i < values.Length; i++)
            values[i] = i < 128 || i % 5 == 0
                ? null!
                : System.Text.Encoding.UTF8.GetBytes($"value-{i % 4}-{new string('x', i % 4)}");

        var schema = new PlankParquetSchema([
            Plank.Schema.ColumnDefinition.Leaf("value", ParquetPhysicalType.ByteArray,
                new ColumnOptions(ParquetRepetition.Optional, [encoding]))
        ]);

        AssertRoundTrip(schema, values, 128, expectDictionary: encoding == EncodingKind.RleDictionary);
    }

    [Test]
    public void NullableMemoryDictionaryRoundTripsAcrossPagesAndRetainsStatistics()
    {
        byte[]?[] expected = new byte[400][];
        for (var i = 0; i < expected.Length; i++)
            expected[i] = i < 96 || i % 5 == 0
                ? null
                : System.Text.Encoding.UTF8.GetBytes($"value-{i % 4}");
        var values = expected
            .Select(static value => value is null ? (ReadOnlyMemory<byte>?)null : value)
            .ToArray();
        var schema = new PlankParquetSchema([
            Plank.Schema.ColumnDefinition.Leaf("value", ParquetPhysicalType.ByteArray,
                new ColumnOptions(ParquetRepetition.Optional, [EncodingKind.RleDictionary]))
        ]);

        AssertTypedRoundTrip(schema, values, expected, 128, expectDictionary: true);
    }

    [Test]
    public void FixedLengthValuesRoundTripAcrossPages()
    {
        var values = new byte[400][];
        for (var i = 0; i < values.Length; i++)
        {
            if (i % 5 == 0)
                continue;
            var value = new byte[16];
            for (var b = 0; b < value.Length; b++)
                value[b] = (byte)(i + b);
            values[i] = value;
        }

        var schema = new PlankParquetSchema([
            Plank.Schema.ColumnDefinition.Leaf("value", ParquetPhysicalType.FixedLenByteArray,
                new ColumnOptions(ParquetRepetition.Optional, typeLength: 16), new LogicalType.Uuid())
        ]);

        AssertRoundTrip(schema, values, 128);
    }

    [Test]
    [Arguments(ParquetDataPageVersion.V1)]
    [Arguments(ParquetDataPageVersion.V2)]
    public void FusedSizingAndDefinitionLevelsPreservePlainFileBytes(ParquetDataPageVersion dataPageVersion)
    {
        byte[][] values =
        [
            [0x11], null!, [0x22, 0x23, 0x24], [], [0x25],
            [0x31], null!, [0x32, 0x33, 0x34], [], [0x35],
            [0x41], null!, [0x42, 0x43, 0x44], [], [0x45]
        ];
        var targetSchema = new PlankParquetSchema([
            Plank.Schema.ColumnDefinition.Leaf("value", ParquetPhysicalType.ByteArray,
                new ColumnOptions(ParquetRepetition.Optional, [EncodingKind.Plain]))
        ]);
        var fixedSchema = new PlankParquetSchema([
            Plank.Schema.ColumnDefinition.Leaf("value", ParquetPhysicalType.ByteArray,
                new ColumnOptions(ParquetRepetition.Optional, [EncodingKind.Plain]),
                pageStrategy: new FixedRowsPageStrategy(5))
        ]);

        var fused = WriteFile(targetSchema, values, dataPageVersion, targetPageBytes: 26);
        var reference = WriteFile(fixedSchema, values, dataPageVersion, targetPageBytes: 26);
        if (!fused.AsSpan().SequenceEqual(reference))
            throw new InvalidOperationException(
                $"Fused optional Plain {dataPageVersion} sizing changed the Parquet bytes.");
    }

    [Test]
    [Arguments(EncodingKind.Plain, ParquetDataPageVersion.V1)]
    [Arguments(EncodingKind.Plain, ParquetDataPageVersion.V2)]
    [Arguments(EncodingKind.DeltaLengthByteArray, ParquetDataPageVersion.V1)]
    [Arguments(EncodingKind.DeltaLengthByteArray, ParquetDataPageVersion.V2)]
    [Arguments(EncodingKind.DeltaByteArray, ParquetDataPageVersion.V1)]
    [Arguments(EncodingKind.DeltaByteArray, ParquetDataPageVersion.V2)]
    public void FusedNullableMemorySizingAndDefinitionLevelsPreserveFileBytes(EncodingKind encoding,
        ParquetDataPageVersion dataPageVersion)
    {
        byte[][] source =
        [
            [0x11], null!, [0x22, 0x23, 0x24], [], [0x25],
            [0x31], null!, [0x32, 0x33, 0x34], [], [0x35],
            [0x41], null!, [0x42, 0x43, 0x44], [], [0x45]
        ];
        var values = source
            .Select(static value => value is null ? (ReadOnlyMemory<byte>?)null : value)
            .ToArray();
        var targetSchema = new PlankParquetSchema([
            Plank.Schema.ColumnDefinition.Leaf("value", ParquetPhysicalType.ByteArray,
                new ColumnOptions(ParquetRepetition.Optional, [encoding]))
        ]);
        var fixedSchema = new PlankParquetSchema([
            Plank.Schema.ColumnDefinition.Leaf("value", ParquetPhysicalType.ByteArray,
                new ColumnOptions(ParquetRepetition.Optional, [encoding]),
                pageStrategy: new FixedRowsPageStrategy(5))
        ]);

        var fused = WriteMemoryFile(targetSchema, values, dataPageVersion, targetPageBytes: 26);
        var reference = WriteMemoryFile(fixedSchema, values, dataPageVersion, targetPageBytes: 26);
        if (!fused.AsSpan().SequenceEqual(reference))
            throw new InvalidOperationException(
                $"Fused nullable memory {encoding} {dataPageVersion} sizing changed the Parquet bytes.");
    }

    static byte[] WriteFile(PlankParquetSchema schema, byte[][] values,
        ParquetDataPageVersion dataPageVersion, uint targetPageBytes)
    {
        using var stream = new MemoryStream();
        var writer = schema.CreateWriter(stream, new ParquetWriterOptions
        {
            Compression = CompressionKind.None,
            DataPageVersion = dataPageVersion,
            TargetDataPageSizeBytes = targetPageBytes
        });
        var column = writer.CreateSerializedColumn<byte[]?>(schema.LeafColumns[0]);
        column.Serialize(values);
        writer.StartRowGroup().Write(column);
        writer.CloseFile();
        return stream.ToArray();
    }

    static byte[] WriteMemoryFile(PlankParquetSchema schema, ReadOnlyMemory<byte>?[] values,
        ParquetDataPageVersion dataPageVersion, uint targetPageBytes)
    {
        using var stream = new MemoryStream();
        var writer = schema.CreateWriter(stream, new ParquetWriterOptions
        {
            Compression = CompressionKind.None,
            DataPageVersion = dataPageVersion,
            TargetDataPageSizeBytes = targetPageBytes
        });
        var column = writer.CreateSerializedColumn<ReadOnlyMemory<byte>?>(schema.LeafColumns[0]);
        column.Serialize(values);
        writer.StartRowGroup().Write(column);
        writer.CloseFile();
        return stream.ToArray();
    }

    static void AssertRoundTrip(PlankParquetSchema schema, byte[]?[] values, uint targetPageBytes,
        bool expectDictionary = false)
        => AssertTypedRoundTrip(schema, values, values, targetPageBytes, expectDictionary);

    static void AssertTypedRoundTrip<TRow>(PlankParquetSchema schema, TRow[] values, byte[]?[] expected,
        uint targetPageBytes, bool expectDictionary = false)
    {
        using var stream = new MemoryStream();
        var writer = schema.CreateWriter(stream, new ParquetWriterOptions
        {
            TargetDataPageSizeBytes = targetPageBytes
        });
        var column = writer.CreateSerializedColumn<TRow>(schema.LeafColumns[0]);
        column.Serialize(values);
        if (column.Pages.Count < 4)
            throw new InvalidOperationException(
                $"Expected the column to split into several pages, got {column.Pages.Count}.");
        if (expectDictionary && !HasDictionaryPage(column.Pages))
            throw new InvalidOperationException("Expected the low-cardinality column to retain dictionary encoding.");
        AssertStatistics(column.Statistics, expected);
        var rowOffset = 0;
        for (var i = 0; i < column.Pages.Count; i++)
        {
            ref var page = ref column.Pages[i];
            if (page.Kind != PageKind.DataV2)
                continue;
            var pageRowCount = checked((int)page.RowCount);
            AssertStatistics(page.Statistics, expected.AsSpan(rowOffset, pageRowCount));
            rowOffset += pageRowCount;
        }
        if (rowOffset != expected.Length)
            throw new InvalidOperationException(
                $"Page statistics covered {rowOffset} rows, expected {expected.Length}.");

        writer.StartRowGroup().Write(column);
        writer.CloseFile();

        using var readStream = new MemoryStream(stream.ToArray(), writable: false);
        using var reader = new ParquetSharp.ParquetFileReader(readStream, leaveOpen: false);
        using var rowGroup = reader.RowGroup(0);
        using var logicalReader = rowGroup.Column(0).LogicalReader();
        var actual = (Array)logicalReader.Apply(new ReadAllVisitor(expected.Length));
        for (var i = 0; i < expected.Length; i++)
        {
            var read = ToBytes(actual.GetValue(i));
            if (expected[i] is null)
            {
                if (read is not null)
                    throw new InvalidOperationException($"Row {i} came back non-null.");
                continue;
            }

            if (read is null || !read.AsSpan().SequenceEqual(expected[i]))
                throw new InvalidOperationException($"Row {i} round-tripped wrong.");
        }
    }

    static bool HasDictionaryPage(PageList pages)
    {
        for (var i = 0; i < pages.Count; i++)
            if (pages[i].Kind == PageKind.Dictionary)
                return true;
        return false;
    }

    static byte[]? ToBytes(object? value)
        => value switch
        {
            null => null,
            byte[] bytes => bytes,
            Guid guid => guid.ToByteArray(bigEndian: true),
            _ => throw new InvalidOperationException($"Unexpected read value type '{value.GetType()}'.")
        };

    static void AssertStatistics(ColumnStatistics statistics, ReadOnlySpan<byte[]?> values)
    {
        byte[]? min = null;
        byte[]? max = null;
        var nullCount = 0L;
        foreach (var value in values)
        {
            if (value is null)
            {
                nullCount++;
                continue;
            }

            if (min is null)
            {
                min = value;
                max = value;
            }
            else
            {
                if (value.AsSpan().SequenceCompareTo(min) < 0)
                    min = value;
                if (value.AsSpan().SequenceCompareTo(max) > 0)
                    max = value;
            }
        }

        if (statistics.NullCount != nullCount)
            throw new InvalidOperationException(
                $"Expected {nullCount} null statistics values, got {statistics.NullCount}.");
        if (min is null)
        {
            if (statistics.ValueKind != ColumnStatistics.ColumnStatisticsValueKind.None)
                throw new InvalidOperationException("All-null page unexpectedly has min/max statistics.");
            return;
        }

        if (!statistics.GetMinValue().SequenceEqual(min) || !statistics.GetMaxValue().SequenceEqual(max))
            throw new InvalidOperationException("Byte-array min/max statistics do not match their page rows.");
    }

    sealed class ReadAllVisitor(int count) : ParquetSharp.ILogicalColumnReaderVisitor<object>
    {
        public object OnLogicalColumnReader<TValue>(ParquetSharp.LogicalColumnReader<TValue> reader)
            => reader.ReadAll(count);
    }

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
