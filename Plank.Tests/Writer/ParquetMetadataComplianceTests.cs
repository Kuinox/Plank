using System.Buffers.Binary;
using Plank.Reading;
using Plank.Schema;
using Plank.Writing;
using Plank.Writing.PageStrategy;

namespace Plank.Tests.Writer;

internal sealed class ParquetMetadataComplianceTests
{
    [Test]
    public void Lz4CompressionUsesRawCodecMetadata()
    {
        var file = WriteRequiredInt32File(new ParquetWriterOptions
        {
            Compression = CompressionKind.Lz4
        });

        var metadata = ReadMetadata(file);

        if (metadata.Column.Codec != 7)
            throw new InvalidOperationException(
                $"Expected LZ4_RAW codec 7, got codec {metadata.Column.Codec}.");
    }

    [Test]
    public void RepeatedColumnMetadataCountsEncodedValuesAndLevels()
    {
        var schema = new ParquetSchema([
            ColumnDefinition.List("numbers", ColumnDefinition.RequiredLeaf("element", ParquetPhysicalType.Int32))
        ]);
        var file = WriteFile(schema, new ParquetWriterOptions(), writer =>
        {
            var column = writer.CreateSerializedColumn<int[]>(schema.LeafColumns[0]);
            var rowGroup = writer.StartRowGroup();
            column.Serialize([
                [1, 2],
                [],
                [3, 4, 5]
            ]);
            rowGroup.Write(column);
        });

        var metadata = ReadMetadata(file);

        if (metadata.Column.ValueCount != 6)
            throw new InvalidOperationException(
                $"Expected repeated column metadata to report 6 encoded values/levels, got {metadata.Column.ValueCount}.");
    }

    [Test]
    public void FooterIncludesTypeDefinedColumnOrderForModernStatistics()
    {
        var file = WriteRequiredInt32File(new ParquetWriterOptions());

        var metadata = ReadMetadata(file);

        if (metadata.ColumnOrderCount != 1)
            throw new InvalidOperationException(
                $"Expected one type-defined column order, got {metadata.ColumnOrderCount}.");
    }

    [Test]
    public void AllNanNonNullPageOmitsColumnIndex()
    {
        var schema = new ParquetSchema([
            ColumnDefinition.RequiredLeaf("value", ParquetPhysicalType.Float)
        ]);
        var file = WriteFile(schema, new ParquetWriterOptions
        {
            WritePageIndexes = true
        }, writer =>
        {
            var column = writer.CreateSerializedColumn<float>(schema.LeafColumns[0]);
            var rowGroup = writer.StartRowGroup();
            column.Serialize([float.NaN, float.NaN]);
            rowGroup.Write(column);
        });
        var metadata = ReadMetadata(file);

        if (metadata.Column.ColumnIndexOffset != -1 || metadata.Column.ColumnIndexLength != -1)
            throw new InvalidOperationException(
                "A column index was emitted for an all-NaN page with no valid min/max bounds.");
    }

    [Test]
    public void UnsignedStatisticsAreOnlyWrittenToModernMinMaxFields()
    {
        var schema = new ParquetSchema([
            ColumnDefinition.RequiredLeaf("value", ParquetPhysicalType.Int32,
                logicalType: new LogicalType.Int(32, isSigned: false))
        ]);
        var file = WriteFile(schema, new ParquetWriterOptions(), writer =>
        {
            var column = writer.CreateSerializedColumn<uint>(schema.LeafColumns[0]);
            var rowGroup = writer.StartRowGroup();
            column.Serialize([0U, uint.MaxValue]);
            rowGroup.Write(column);
        });

        var statistics = ReadMetadata(file).Column.Statistics;

        if (statistics.HasLegacyMin || statistics.HasLegacyMax)
            throw new InvalidOperationException(
                "Unsigned bounds were emitted in legacy min/max fields, whose ordering is signed.");
        if (!statistics.HasModernMin || !statistics.HasModernMax)
            throw new InvalidOperationException("Unsigned bounds were not emitted in the modern min_value/max_value fields.");
    }

    [Test]
    public void DictionaryFirstChunkOffsetsPointToDictionaryPage()
    {
        var schema = new ParquetSchema([
            ColumnDefinition.RequiredLeaf("value", ParquetPhysicalType.Int32,
                pageStrategy: ForceDictionaryPageStrategy.Shared)
        ]);
        var file = WriteFile(schema, new ParquetWriterOptions(), writer =>
        {
            var column = writer.CreateSerializedColumn<int>(schema.LeafColumns[0]);
            var rowGroup = writer.StartRowGroup();
            column.Serialize([10, 20, 10, 20]);
            rowGroup.Write(column);
        });

        var metadata = ReadMetadata(file);

        if (metadata.Column.DictionaryPageOffset <= 0)
            throw new InvalidOperationException("Expected a dictionary page offset.");
        if (metadata.Column.DataPageOffset <= metadata.Column.DictionaryPageOffset)
            throw new InvalidOperationException("Expected the dictionary page to precede the data page.");
        if (metadata.RowGroupFileOffset != metadata.Column.DictionaryPageOffset)
            throw new InvalidOperationException(
                $"Expected row-group file_offset {metadata.Column.DictionaryPageOffset}, got {metadata.RowGroupFileOffset}.");
        if (metadata.Column.FileOffset != 0)
            throw new InvalidOperationException(
                $"Expected deprecated column-chunk file_offset 0, got {metadata.Column.FileOffset}.");
    }

    static byte[] WriteRequiredInt32File(ParquetWriterOptions options)
    {
        var schema = new ParquetSchema([
            ColumnDefinition.RequiredLeaf("value", ParquetPhysicalType.Int32)
        ]);
        return WriteFile(schema, options, writer =>
        {
            var column = writer.CreateSerializedColumn<int>(schema.LeafColumns[0]);
            var rowGroup = writer.StartRowGroup();
            column.Serialize([3, 1, 2]);
            rowGroup.Write(column);
        });
    }

    static byte[] WriteFile(ParquetSchema schema, ParquetWriterOptions options, Action<ParquetWriter> write)
    {
        using var stream = new MemoryStream();
        var writer = schema.CreateWriter(stream, options);
        write(writer);
        writer.CloseFile();
        return stream.ToArray();
    }

    static FileMetadata ReadMetadata(byte[] file)
    {
        var footerLength = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(file.Length - 8, sizeof(int)));
        var footerOffset = checked(file.Length - 8 - footerLength);
        var reader = new CompactProtocolReader(file.AsSpan(footerOffset, footerLength));
        reader.BeginStruct();

        RowGroupMetadata? rowGroup = null;
        var columnOrderCount = 0;
        while (reader.TryReadFieldHeader(out var fieldId, out var type, out var inlineBool))
        {
            switch (fieldId)
            {
                case 4:
                {
                    var (count, elementType) = reader.ReadListHeader();
                    if (count == 0 || elementType != CompactProtocolType.Struct)
                        throw new InvalidOperationException("Expected at least one row group.");
                    rowGroup = ReadRowGroup(ref reader);
                    for (var i = 1U; i < count; i++)
                        reader.Skip(CompactProtocolType.Struct);
                    break;
                }
                case 7:
                {
                    var (count, elementType) = reader.ReadListHeader();
                    if (elementType != CompactProtocolType.Struct)
                        throw new InvalidOperationException("Expected column_orders to contain structs.");
                    columnOrderCount = checked((int)count);
                    for (var i = 0U; i < count; i++)
                        reader.Skip(CompactProtocolType.Struct);
                    break;
                }
                default:
                    reader.Skip(type, inlineBool);
                    break;
            }
        }

        var actualRowGroup = rowGroup
            ?? throw new InvalidOperationException("Footer did not contain a row group.");
        return new FileMetadata(actualRowGroup.FileOffset, actualRowGroup.Column, columnOrderCount);
    }

    static RowGroupMetadata ReadRowGroup(ref CompactProtocolReader reader)
    {
        reader.BeginStruct();
        ColumnMetadata? column = null;
        var fileOffset = -1L;
        while (reader.TryReadFieldHeader(out var fieldId, out var type, out var inlineBool))
        {
            switch (fieldId)
            {
                case 1:
                {
                    var (count, elementType) = reader.ReadListHeader();
                    if (count == 0 || elementType != CompactProtocolType.Struct)
                        throw new InvalidOperationException("Expected at least one column chunk.");
                    column = ReadColumnChunk(ref reader);
                    for (var i = 1U; i < count; i++)
                        reader.Skip(CompactProtocolType.Struct);
                    break;
                }
                case 5:
                    fileOffset = reader.ReadI64();
                    break;
                default:
                    reader.Skip(type, inlineBool);
                    break;
            }
        }

        return new RowGroupMetadata(fileOffset,
            column ?? throw new InvalidOperationException("Row group did not contain a column chunk."));
    }

    static ColumnMetadata ReadColumnChunk(ref CompactProtocolReader reader)
    {
        reader.BeginStruct();
        var fileOffset = -1L;
        var columnIndexOffset = -1L;
        var columnIndexLength = -1;
        ColumnMetadata? metadata = null;
        while (reader.TryReadFieldHeader(out var fieldId, out var type, out var inlineBool))
        {
            switch (fieldId)
            {
                case 2:
                    fileOffset = reader.ReadI64();
                    break;
                case 3:
                    metadata = ReadColumnMetadata(ref reader);
                    break;
                case 6:
                    columnIndexOffset = reader.ReadI64();
                    break;
                case 7:
                    columnIndexLength = reader.ReadI32();
                    break;
                default:
                    reader.Skip(type, inlineBool);
                    break;
            }
        }

        var actual = metadata
            ?? throw new InvalidOperationException("Column chunk did not contain column metadata.");
        return actual with
        {
            FileOffset = fileOffset,
            ColumnIndexOffset = columnIndexOffset,
            ColumnIndexLength = columnIndexLength
        };
    }

    static ColumnMetadata ReadColumnMetadata(ref CompactProtocolReader reader)
    {
        reader.BeginStruct();
        var codec = -1;
        var valueCount = -1L;
        var dataPageOffset = -1L;
        var dictionaryPageOffset = -1L;
        var statistics = default(StatisticsMetadata);
        while (reader.TryReadFieldHeader(out var fieldId, out var type, out var inlineBool))
        {
            switch (fieldId)
            {
                case 4:
                    codec = reader.ReadI32();
                    break;
                case 5:
                    valueCount = reader.ReadI64();
                    break;
                case 9:
                    dataPageOffset = reader.ReadI64();
                    break;
                case 11:
                    dictionaryPageOffset = reader.ReadI64();
                    break;
                case 12:
                    statistics = ReadStatistics(ref reader);
                    break;
                default:
                    reader.Skip(type, inlineBool);
                    break;
            }
        }

        return new ColumnMetadata(codec, valueCount, dataPageOffset, dictionaryPageOffset, -1, -1, -1, statistics);
    }

    static StatisticsMetadata ReadStatistics(ref CompactProtocolReader reader)
    {
        reader.BeginStruct();
        var hasLegacyMax = false;
        var hasLegacyMin = false;
        var hasModernMax = false;
        var hasModernMin = false;
        while (reader.TryReadFieldHeader(out var fieldId, out var type, out var inlineBool))
        {
            switch (fieldId)
            {
                case 1:
                    hasLegacyMax = true;
                    break;
                case 2:
                    hasLegacyMin = true;
                    break;
                case 5:
                    hasModernMax = true;
                    break;
                case 6:
                    hasModernMin = true;
                    break;
            }
            reader.Skip(type, inlineBool);
        }

        return new StatisticsMetadata(hasLegacyMin, hasLegacyMax, hasModernMin, hasModernMax);
    }

    sealed record FileMetadata(long RowGroupFileOffset, ColumnMetadata Column, int ColumnOrderCount);

    sealed record RowGroupMetadata(long FileOffset, ColumnMetadata Column);

    sealed record ColumnMetadata(int Codec, long ValueCount, long DataPageOffset, long DictionaryPageOffset,
        long FileOffset, long ColumnIndexOffset, int ColumnIndexLength, StatisticsMetadata Statistics);

    readonly record struct StatisticsMetadata(
        bool HasLegacyMin,
        bool HasLegacyMax,
        bool HasModernMin,
        bool HasModernMax);
}
