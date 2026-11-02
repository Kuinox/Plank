using System.Collections.Immutable;
using Plank.Reading;
using Plank.Reading.Physical;
using Plank.Schema;
using Plank.Writing;

namespace Plank.Tests.Reading;

internal sealed class RowGroupWireTypeTests
{
    [Test]
    public void RejectsColumnListEncodedAsSet()
    {
        var file = CreateFile();
        using var probe = new ParquetFileReader();
        probe.Reset(new MemoryStream(file, writable: false));
        var footerOffset = checked((int)probe.Metadata.FooterOffset);
        // FileMetaData.row_groups: list of one struct; RowGroup.columns:
        // list of one struct. Change only the latter's compact type to SET.
        ReadOnlySpan<byte> pattern = [0x19, 0x1c, 0x19, 0x1c, 0x26];
        var position = file.AsSpan(footerOffset).IndexOf(pattern);
        if (position < 0 || file.AsSpan(footerOffset + position + 1).IndexOf(pattern) >= 0)
            throw new InvalidOperationException("Expected a unique row group sequence.");
        file[footerOffset + position + 2] = 0x1a;
        using var reader = new ParquetFileReader();
        Assert.Throws<CorruptParquetException>(() => reader.Reset(new MemoryStream(file, writable: false)));
    }

    [Test]
    [Arguments(1, false)]
    [Arguments(2, false)]
    [Arguments(2, true)]
    [Arguments(3, false)]
    [Arguments(3, true)]
    public void RequiredFieldMustBePresentWithCorrectType(int field, bool wrongType)
    {
        var file = CreateFile();
        using var probe = new ParquetFileReader();
        probe.Reset(new MemoryStream(file, writable: false));
        var offset = checked((int)probe.Metadata.RowGroup(0).MetadataOffset);
        var protocol = new CompactProtocolReader(file.AsSpan(offset));
        protocol.BeginRootStruct();
        var fieldOffset = -1;
        while (true)
        {
            var headerOffset = protocol.Offset;
            if (!protocol.TryReadFieldHeader(out var id, out var type, out var inlineBool))
                break;
            if (id == field)
                fieldOffset = headerOffset;
            protocol.Skip(type, inlineBool);
        }
        if (fieldOffset < 0)
            throw new InvalidOperationException($"RowGroup field {field} not found.");
        // Move the field id beyond the known fields, or change its I64 type to I32.
        // Values remain well-formed so the missing-field check is reached.
        var original = file[offset + fieldOffset];
        file[offset + fieldOffset] = wrongType ? (byte)((original & 0xf0) | 0x05) :
            (byte)((original & 0x0f) | 0x70);
        using var reader = new ParquetFileReader();
        var error = Assert.Throws<CorruptParquetException>(() => reader.Reset(new MemoryStream(file, writable: false)));
        var name = field switch { 1 => "columns", 2 => "total_byte_size", _ => "num_rows" };
        if (!wrongType && !error.Message.Contains($"RowGroup.{name}", StringComparison.Ordinal))
            throw new InvalidOperationException($"Unexpected error: {error.Message}");
    }

    static byte[] CreateFile()
    {
        var schema = new ParquetSchema([
            ColumnDefinition.RequiredLeaf("value", ParquetPhysicalType.Int32,
                new ColumnOptions(encodings: ImmutableArray.Create(EncodingKind.Plain)))
        ]);
        using var stream = new MemoryStream();
        using (var writer = schema.CreateWriter(stream, new ParquetWriterOptions
        {
            Compression = CompressionKind.None,
            WritePageIndexes = false
        }))
        {
            var column = writer.CreateSerializedColumn<int>(schema.LeafColumns[0]);
            column.Serialize([1, 2, 3, 4]);
            writer.StartRowGroup().Write(column);
            writer.CloseFile();
        }
        return stream.ToArray();
    }
}
