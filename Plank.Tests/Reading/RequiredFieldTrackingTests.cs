using Plank.Reading;
using Plank.Reading.Physical;
using Plank.Schema;
using Plank.Writing;

namespace Plank.Tests.Reading;

internal sealed class RequiredFieldTrackingTests
{
    [Test]
    [Arguments(0, 1)] [Arguments(0, 2)] [Arguments(0, 3)]
    [Arguments(5, 1)] [Arguments(5, 2)] [Arguments(5, 3)] [Arguments(5, 4)]
    [Arguments(7, 1)] [Arguments(7, 2)]
    [Arguments(8, 1)] [Arguments(8, 2)] [Arguments(8, 3)]
    [Arguments(8, 4)] [Arguments(8, 5)] [Arguments(8, 6)]
    public void EveryPageHeaderRequiredFieldIsEnforced(int nestedField, int missing)
    {
        var bytes = new List<byte>();
        for (var id = 1; id <= 3; id++)
            if (nestedField != 0 || id != missing)
                WriteI32(bytes, id);
        if (nestedField != 0)
        {
            bytes.Add(0x0c); // explicit struct field id
            bytes.Add((byte)(nestedField * 2));
            var count = nestedField == 5 ? 4 : nestedField == 7 ? 2 : 6;
            for (var id = 1; id <= count; id++)
                if (id != missing) WriteI32(bytes, id);
            bytes.Add(0);
        }
        bytes.Add(0);
        var error = Assert.Throws<CorruptParquetException>(() => PageHeaderReader.Read(bytes.ToArray()));
        var structure = nestedField switch { 0 => "PageHeader", 5 => "DataPageHeader", 7 => "DictionaryPageHeader", _ => "DataPageHeaderV2" };
        if (!error.Message.Contains($"{structure}.", StringComparison.Ordinal))
            throw new InvalidOperationException($"Unexpected error: {error.Message}");
    }

    [Test]
    public void UnknownAndNegativeIdsCannotSatisfyRequiredFields()
    {
        var fields = new RequiredFields();
        fields.Add(-1);
        fields.Add(33);
        var error = Assert.Throws<CorruptParquetException>(() => fields.Validate(ParquetRequiredFields.KeyValue));
        if (!error.Message.Contains("KeyValue.key", StringComparison.Ordinal))
            throw new InvalidOperationException($"Unexpected error: {error.Message}");
    }

    [Test]
    public void IndexPageHeaderMustBeAStruct()
    {
        byte[] bytes = [0x15, 0x00, 0x15, 0x00, 0x15, 0x00, 0x37, 0, 0, 0, 0, 0, 0, 0, 0, 0];
        Assert.Throws<CorruptParquetException>(() => PageHeaderReader.Read(bytes));
    }

    [Test]
    public void RootSchemaElementMustHaveANameField()
    {
        var schema = new ParquetSchema([ColumnDefinition.RequiredLeaf("value", ParquetPhysicalType.Int32)]);
        using var stream = new MemoryStream();
        using (var writer = schema.CreateWriter(stream))
        {
            var column = writer.CreateSerializedColumn<int>(schema.LeafColumns[0]);
            column.Serialize([1]);
            writer.StartRowGroup().Write(column);
            writer.CloseFile();
        }
        var file = stream.ToArray();
        using var probe = new ParquetFileReader();
        probe.Reset(new MemoryStream(file, writable: false));
        var footerOffset = checked((int)probe.Metadata.FooterOffset);
        var protocol = new CompactProtocolReader(file.AsSpan(footerOffset));
        protocol.BeginRootStruct();
        while (protocol.TryReadFieldHeader(out var id, out var type, out var inlineBool))
        {
            if (id != 2) { protocol.Skip(type, inlineBool); continue; }
            var (_, elementType) = protocol.ReadListHeader(type);
            protocol.BeginStruct(elementType);
            while (true)
            {
                var offset = protocol.Offset;
                if (!protocol.TryReadFieldHeader(out id, out type, out inlineBool))
                    throw new InvalidOperationException("Root schema name not found.");
                if (id == 4)
                {
                    file[footerOffset + offset] = (byte)((file[footerOffset + offset] & 0x0f) | 0xf0);
                    using var reader = new ParquetFileReader();
                    var error = Assert.Throws<CorruptParquetException>(() => reader.Reset(new MemoryStream(file, writable: false)));
                    if (!error.Message.Contains("SchemaElement.name", StringComparison.Ordinal))
                        throw new InvalidOperationException($"Unexpected error: {error.Message}");
                    return;
                }
                protocol.Skip(type, inlineBool);
            }
        }
        throw new InvalidOperationException("Schema not found.");
    }

    static void WriteI32(List<byte> bytes, int id)
    {
        bytes.Add(0x05); // explicit i32 field id, zigzag-encoded
        bytes.Add((byte)(id * 2));
        bytes.Add(0);
    }
}
