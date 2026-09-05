using System.Buffers.Binary;
using System.Text;
using Plank.Reading.Logical;
using Plank.Schema;
using Plank.Writing;

namespace Plank.Tests.Reading;

internal sealed class LegacyTwoLevelListDiscoveryTests
{
    [Test]
    public void ValidLegacyTwoLevelListSchemaCanBeRead()
    {
        using var stream = new MemoryStream(CreatePrimitiveListFile());
        using var reader = new ParquetReader();

        reader.Reset(stream);

        var list = reader.Schema.Definitions.Single();
        if (list.Kind != NodeKind.List ||
            list.Repetition != ParquetRepetition.Optional ||
            list.Children is not [{ Kind: NodeKind.Leaf, PhysicalType: ParquetPhysicalType.Int32 }])
            throw new InvalidOperationException("The valid legacy two-level LIST schema was not preserved.");
    }

    [Test]
    public void DiscoveredLegacySchemaCanBeReusedForStrictReading()
    {
        var file = CreatePrimitiveListFile();
        ParquetSchema discovered;
        using (var reader = new ParquetReader())
        {
            reader.Reset(new MemoryStream(file));
            discovered = reader.Schema;
        }

        using var strictReader = discovered.CreateReader(new MemoryStream(file));
        if (strictReader.Schema.LeafColumns.Length != 1)
            throw new InvalidOperationException("The discovered legacy LIST schema could not be reused.");
    }

    [Test]
    public async Task DiscoveredLegacySchemaCannotRewriteThePhysicalLayoutOfExistingPages()
    {
        var file = CreatePrimitiveListFile();
        var discovered = DiscoverSchema(file);
        using var source = new MemoryStream();
        source.Write(file);
        Assert.Throws<InvalidOperationException>(() => discovered.CreateAppender(source));
        await Assert.That(source.ToArray().AsSpan().SequenceEqual(file)).IsTrue();

        using var destination = new MemoryStream();
        destination.Write([1, 2, 3]);
        var before = destination.ToArray();
        using var readSource = new Plank.Reading.StreamReadSource(source);
        using var writeSource = new StreamParquetSource(destination);
        Assert.Throws<InvalidOperationException>(() => discovered.CreateMerger(readSource, writeSource));
        await Assert.That(destination.ToArray().AsSpan().SequenceEqual(before)).IsTrue();
    }

    [Test]
    public void DiscoveredLegacySchemaPreservesLevelsAndRoundTripsData()
    {
        var discovered = DiscoverSchema(CreatePrimitiveListFile());
        var projection = discovered.LeafProjectionInfos.Single();
        if (projection is not
            {
                IsList: true,
                ListOptional: true,
                ElementOptional: false,
                MaxRepetitionLevel: 1,
                MaxDefinitionLevel: 2
            })
            throw new InvalidOperationException("The legacy LIST projection levels were not normalized correctly.");

        int[][] expected = [[1, 2], [], [3]];
        var path = Path.Combine(Path.GetTempPath(), $"plank-legacy-list-{Guid.NewGuid():N}.parquet");
        try
        {
            using (var destination = File.Create(path))
            {
                var writer = discovered.CreateWriter(destination);
                var rowGroup = writer.StartRowGroup();
                var serialized = rowGroup.CreateSerializedColumn<int[]>(discovered.LeafColumns[0]);
                serialized.Serialize(expected);
                rowGroup.Write(serialized);
                writer.CloseFile();
            }

            using var reader = new ParquetSharp.ParquetFileReader(path);
            using var rowGroupReader = reader.RowGroup(0);
            using var logical = rowGroupReader.Column(0).LogicalReader<int[]>();
            var actual = logical.ReadAll(expected.Length);
            if (actual.Length != expected.Length)
                throw new InvalidOperationException($"Expected {expected.Length} rows, got {actual.Length}.");
            for (var i = 0; i < expected.Length; i++)
                if (!actual[i].AsSpan().SequenceEqual(expected[i]))
                    throw new InvalidOperationException($"Row {i} did not round-trip.");
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Test]
    public void LegacyRepeatedTupleIsPreservedAsRequiredGroupElement()
    {
        var file = CreateFile(
            ListNode("values", ParquetRepetition.Optional, childCount: 1),
            GroupNode("items", ParquetRepetition.Repeated, childCount: 2),
            LeafNode("left", ParquetRepetition.Required, ParquetPhysicalType.Int32),
            LeafNode("right", ParquetRepetition.Optional, ParquetPhysicalType.ByteArray));

        using var reader = new ParquetReader();
        reader.Reset(new MemoryStream(file));

        var list = reader.Schema.Definitions.Single();
        if (list.Children is not
            [
                {
                    Kind: NodeKind.Group,
                    Repetition: ParquetRepetition.Required,
                    Children:
                    [
                        { Name: "left", PhysicalType: ParquetPhysicalType.Int32 },
                        { Name: "right", PhysicalType: ParquetPhysicalType.ByteArray }
                    ]
                }
            ])
            throw new InvalidOperationException("The legacy repeated tuple was not preserved as the list element.");
    }

    [Test]
    public void LegacyNestedListWrapperIsPreservedAsRequiredListElement()
    {
        var file = CreateFile(
            ListNode("values", ParquetRepetition.Optional, childCount: 1),
            ListNode("array", ParquetRepetition.Repeated, childCount: 1),
            LeafNode("array", ParquetRepetition.Repeated, ParquetPhysicalType.Int32));

        using var reader = new ParquetReader();
        reader.Reset(new MemoryStream(file));

        var list = reader.Schema.Definitions.Single();
        if (list.Children is not
            [
                {
                    Kind: NodeKind.List,
                    Repetition: ParquetRepetition.Required,
                    Children:
                    [
                        {
                            Kind: NodeKind.Leaf,
                            Repetition: ParquetRepetition.Required,
                            PhysicalType: ParquetPhysicalType.Int32
                        }
                    ]
                }
            ])
            throw new InvalidOperationException("The nested legacy LIST wrapper was incorrectly unwrapped.");

        var projection = reader.Schema.LeafProjectionInfos.Single();
        if (projection is not { MaxRepetitionLevel: 2, MaxDefinitionLevel: 3 })
            throw new InvalidOperationException("The nested legacy LIST projection levels were not preserved.");
    }

    [Test]
    public void LegacyNamedTupleWrapperIsPreservedButOrdinaryWrapperIsUnwrapped()
    {
        var namedTupleFile = CreateFile(
            ListNode("values", ParquetRepetition.Optional, childCount: 1),
            GroupNode("array", ParquetRepetition.Repeated, childCount: 1),
            LeafNode("value", ParquetRepetition.Required, ParquetPhysicalType.Int32));
        using (var reader = new ParquetReader())
        {
            reader.Reset(new MemoryStream(namedTupleFile));
            if (reader.Schema.Definitions.Single().Children is not
                [
                    {
                        Kind: NodeKind.Group,
                        Repetition: ParquetRepetition.Required,
                        Children: [{ Name: "value" }]
                    }
                ])
                throw new InvalidOperationException("The named legacy tuple wrapper was incorrectly unwrapped.");
        }

        var ordinaryWrapperFile = CreateFile(
            ListNode("values", ParquetRepetition.Optional, childCount: 1),
            GroupNode("entries", ParquetRepetition.Repeated, childCount: 1),
            LeafNode("value", ParquetRepetition.Optional, ParquetPhysicalType.Int32));
        using var ordinaryReader = new ParquetReader();
        ordinaryReader.Reset(new MemoryStream(ordinaryWrapperFile));
        if (ordinaryReader.Schema.Definitions.Single().Children is not
            [
                {
                    Kind: NodeKind.Leaf,
                    Repetition: ParquetRepetition.Optional,
                    PhysicalType: ParquetPhysicalType.Int32
                }
            ])
            throw new InvalidOperationException("An ordinary three-level LIST wrapper was not unwrapped.");
    }

    [Test]
    public void TopLevelRepeatedListIsRejected()
        => Assert.Throws<CorruptParquetException>(() =>
            DiscoverSchema(CreateFile(
                ListNode("values", ParquetRepetition.Repeated, childCount: 1),
                LeafNode("element", ParquetRepetition.Repeated, ParquetPhysicalType.Int32))));

    [Test]
    public void ListWithoutOuterRepetitionIsRejected()
        => Assert.Throws<CorruptParquetException>(() =>
            DiscoverSchema(CreateFile(
                ListNode("values", repetition: null, childCount: 1),
                LeafNode("element", ParquetRepetition.Repeated, ParquetPhysicalType.Int32))));

    static byte[] CreatePrimitiveListFile()
        => CreateFile(
            ListNode("values", ParquetRepetition.Optional, childCount: 1),
            LeafNode("element", ParquetRepetition.Repeated, ParquetPhysicalType.Int32));

    static ParquetSchema DiscoverSchema(byte[] file)
    {
        using var reader = new ParquetReader();
        reader.Reset(new MemoryStream(file));
        return reader.Schema;
    }

    static byte[] CreateFile(params byte[][] schemaNodes)
    {
        using var footer = new MemoryStream();
        footer.Write([0x15, 0x02, 0x19, checked((byte)(((schemaNodes.Length + 1) << 4) | 0x0C))]);
        footer.Write([0x48, 0x06]);
        footer.Write("schema"u8);
        footer.Write([0x15, 0x02, 0x00]);
        foreach (var node in schemaNodes)
            footer.Write(node);
        footer.Write([0x16, 0x00, 0x19, 0x0C, 0x00]);

        using var stream = new MemoryStream();
        stream.Write("PAR1"u8);
        footer.Position = 0;
        footer.CopyTo(stream);
        Span<byte> footerLength = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(footerLength, checked((uint)footer.Length));
        stream.Write(footerLength);
        stream.Write("PAR1"u8);
        return stream.ToArray();
    }

    static byte[] ListNode(string name, ParquetRepetition? repetition, int childCount)
    {
        using var stream = new MemoryStream();
        if (repetition is { } value)
            stream.Write([0x35, EncodeRepetition(value), 0x18]);
        else
            stream.WriteByte(0x48);
        WriteName(stream, name);
        stream.Write([
            0x15, EncodeI32(childCount),
            0x15, 0x06,
            0x4C,
            0x3C, 0x00,
            0x00,
            0x00
        ]);
        return stream.ToArray();
    }

    static byte[] GroupNode(string name, ParquetRepetition repetition, int childCount)
    {
        using var stream = new MemoryStream();
        stream.Write([0x35, EncodeRepetition(repetition), 0x18]);
        WriteName(stream, name);
        stream.Write([0x15, EncodeI32(childCount), 0x00]);
        return stream.ToArray();
    }

    static byte[] LeafNode(string name, ParquetRepetition repetition, ParquetPhysicalType physicalType)
    {
        using var stream = new MemoryStream();
        stream.Write([
            0x15, EncodeI32((int)physicalType),
            0x25, EncodeRepetition(repetition),
            0x18
        ]);
        WriteName(stream, name);
        stream.WriteByte(0x00);
        return stream.ToArray();
    }

    static void WriteName(Stream stream, string name)
    {
        var bytes = Encoding.UTF8.GetBytes(name);
        stream.WriteByte(checked((byte)bytes.Length));
        stream.Write(bytes);
    }

    static byte EncodeI32(int value)
        => checked((byte)(value << 1));

    static byte EncodeRepetition(ParquetRepetition repetition)
        => EncodeI32(checked((int)repetition - 1));
}
