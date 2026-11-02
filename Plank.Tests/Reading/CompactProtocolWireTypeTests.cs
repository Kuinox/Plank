using System.Buffers.Binary;
using Plank.Reading;
using Plank.Reading.Physical;

namespace Plank.Tests.Reading;

internal sealed class CompactProtocolWireTypeTests
{
    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    [Arguments(3)]
    [Arguments(4)]
    [Arguments(5)]
    [Arguments(6)]
    [Arguments(7)]
    [Arguments(8)]
    [Arguments(9)]
    [Arguments(10)]
    [Arguments(11)]
    public void MistypedReadsRejectBeforeConsumingPayload(int operation)
    {
        // Even a partial page-header probe must report a type mismatch as corruption,
        // not request more bytes. This also exercises empty list/struct fields.
        var reader = new CompactProtocolReader([], bufferMayBeTruncated: true);
        try
        {
            switch (operation)
            {
                case 0: _ = reader.ReadI16(CompactProtocolType.I32); break;
                case 1: _ = reader.ReadI32(CompactProtocolType.I64); break;
                case 2: _ = reader.ReadI64(CompactProtocolType.I32); break;
                case 3: _ = reader.ReadByte(CompactProtocolType.I32); break;
                case 4: _ = reader.ReadDouble(CompactProtocolType.Binary); break;
                case 5: _ = reader.ReadBinary(CompactProtocolType.Double); break;
                case 6: _ = reader.ReadI32AsU32(CompactProtocolType.I64); break;
                case 7: _ = reader.ReadI64AsU64(CompactProtocolType.I32); break;
                case 8: _ = reader.ReadBool(CompactProtocolType.Byte, true); break;
                case 9: reader.BeginStruct(CompactProtocolType.List); break;
                case 10: _ = reader.ReadListHeader(CompactProtocolType.Set); break;
                case 11: reader.SkipStruct(CompactProtocolType.Binary); break;
            }
        }
        catch (CorruptParquetException)
        {
            if (reader.Offset != 0)
                throw new InvalidOperationException("A mistyped read consumed payload bytes.");
            return;
        }
        throw new InvalidOperationException("Expected a wire-type mismatch.");
    }

    [Test]
    [Arguments(1)]
    [Arguments(3)]
    [Arguments(4)]
    public void NestedDataPageHeaderRejectsWrongScalarType(int fieldId)
    {
        // PageHeader.data_page_header containing an inline bool instead of i32.
        byte[] header = [0x5c, checked((byte)((fieldId << 4) | 1)), 0, 0];
        ExpectWireTypeMismatch(() => PageHeaderReader.Read(header));
    }

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    [Arguments(3)]
    public void FooterRejectsWrongWireType(int variant)
    {
        byte[] footer = variant switch
        {
            // FileMetaData.schema must be a list, not a struct.
            0 => [0x2c, 0, 0],
            // SchemaElement.name must be binary, not i32.
            1 => [0x29, 0x1c, 0x45, 0, 0, 0],
            // SchemaElement.logicalType must be a struct, not a bool.
            2 => [0x29, 0x1c, 0xa1, 0, 0],
            // LogicalType.STRING is an empty struct, not a bool.
            _ => [0x29, 0x1c, 0xac, 0x11, 0, 0, 0]
        };
        var file = new byte[footer.Length + 12];
        "PAR1"u8.CopyTo(file);
        footer.CopyTo(file, 4);
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(file.Length - 8), footer.Length);
        "PAR1"u8.CopyTo(file.AsSpan(file.Length - 4));
        ExpectWireTypeMismatch(() =>
        {
            using var stream = new MemoryStream(file, writable: false);
            using var reader = new ParquetFileReader();
            reader.Reset(stream);
        });
    }

    [Test]
    [Arguments("733fb15eb8bf829d")]
    [Arguments("7f4e5c678afd0f14")]
    [Arguments("973b2bdef66deaf9")]
    [Arguments("be22ef37b6af600e")]
    public void OriginalMistypedPageFuzzInputsAreRejected(string name)
    {
        // PR #165 inputs, with only the fuzz route-selector byte removed.
        var file = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Reading", "Fixtures",
            $"MistypedPage-{name}.parquet"));
        ExpectWireTypeMismatch(() =>
        {
            using var stream = new MemoryStream(file, writable: false);
            using var reader = new ParquetFileReader();
            reader.Reset(stream);
            using var pages = reader.OpenPages(0, 0);
            while (pages.MoveNext()) { }
        });
    }

    static void ExpectWireTypeMismatch(Action action)
    {
        try
        {
            action();
        }
        catch (CorruptParquetException exception) when (
            exception.Message.StartsWith("Invalid compact protocol type '", StringComparison.Ordinal))
        {
            return;
        }
        throw new InvalidOperationException("Expected a centrally validated wire-type mismatch.");
    }

    [Test]
    public void BooleanFieldsAndListElementsHaveDifferentPayloadRules()
    {
        var reader = new CompactProtocolReader([1, 2]);
        if (!reader.ReadBool(CompactProtocolType.BooleanTrue, true) ||
            reader.ReadBool(CompactProtocolType.BooleanFalse, false) || reader.Offset != 0 ||
            !reader.ReadBool(CompactProtocolType.BooleanTrue, null) ||
            reader.ReadBool(CompactProtocolType.BooleanTrue, null) || reader.Offset != 2)
            throw new InvalidOperationException("Invalid inline/list boolean decoding.");
    }
}
