using System.Buffers.Binary;
using Plank.Reading;
using Plank.Schema;
using Plank.Writing;

namespace Plank.Tests.Writer;

internal sealed class MapLogicalTypeMetadataTests
{
    [Test]
    public void MapFooterIncludesLegacyAndModernAnnotations()
    {
        var schema = new ParquetSchema([
            ColumnDefinition.Map("scores",
                ColumnDefinition.RequiredLeaf("key", ParquetPhysicalType.Int32),
                ColumnDefinition.RequiredLeaf("value", ParquetPhysicalType.Int32))
        ]);

        using var stream = new MemoryStream();
        var writer = schema.CreateWriter(stream, new ParquetWriterOptions
        {
            Compression = CompressionKind.None
        });
        var rowGroup = writer.StartRowGroup();
        var keys = rowGroup.CreateSerializedColumn<int[]>(schema.LeafColumns[0]);
        keys.Serialize([[1]]);
        rowGroup.Write(keys);
        var values = rowGroup.CreateSerializedColumn<int[]>(schema.LeafColumns[1]);
        values.Serialize([[10]]);
        rowGroup.Write(values);
        writer.CloseFile();

        var (hasConvertedMap, hasLogicalMap) = ReadMapAnnotations(stream.ToArray());
        if (!hasConvertedMap)
            throw new InvalidOperationException("MAP schema node omitted legacy ConvertedType.MAP.");
        if (!hasLogicalMap)
            throw new InvalidOperationException("MAP schema node omitted modern LogicalType.MAP.");
    }

    static (bool HasConvertedMap, bool HasLogicalMap) ReadMapAnnotations(ReadOnlySpan<byte> file)
    {
        var footerLength = BinaryPrimitives.ReadInt32LittleEndian(file.Slice(file.Length - 8, sizeof(int)));
        var footerOffset = checked(file.Length - 8 - footerLength);
        var reader = new CompactProtocolReader(file.Slice(footerOffset, footerLength));
        reader.BeginStruct();
        while (reader.TryReadFieldHeader(out var fieldId, out var type, out var inlineBool))
        {
            if (fieldId == 2)
            {
                var (count, elementType) = reader.ReadListHeader();
                if (count < 2 || elementType != CompactProtocolType.Struct)
                    throw new InvalidOperationException("Footer schema does not contain a root and MAP node.");

                _ = ReadSchemaNodeAnnotations(ref reader);
                return ReadSchemaNodeAnnotations(ref reader);
            }

            reader.Skip(type, inlineBool);
        }

        throw new InvalidOperationException("Footer did not contain a schema.");
    }

    static (bool HasConvertedMap, bool HasLogicalMap) ReadSchemaNodeAnnotations(
        ref CompactProtocolReader reader)
    {
        var hasConvertedMap = false;
        var hasLogicalMap = false;
        reader.BeginStruct();
        while (reader.TryReadFieldHeader(out var fieldId, out var type, out var inlineBool))
        {
            switch (fieldId)
            {
                case 6:
                    hasConvertedMap = reader.ReadI32() == 1;
                    break;
                case 10:
                    hasLogicalMap = ReadLogicalMapAnnotation(ref reader);
                    break;
                default:
                    reader.Skip(type, inlineBool);
                    break;
            }
        }

        return (hasConvertedMap, hasLogicalMap);
    }

    static bool ReadLogicalMapAnnotation(ref CompactProtocolReader reader)
    {
        var hasLogicalMap = false;
        reader.BeginStruct();
        while (reader.TryReadFieldHeader(out var fieldId, out var type, out var inlineBool))
        {
            if (fieldId == 2)
            {
                hasLogicalMap = true;
                reader.Skip(type, inlineBool);
            }
            else
                reader.Skip(type, inlineBool);
        }

        return hasLogicalMap;
    }
}
