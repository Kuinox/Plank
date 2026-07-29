using System.Buffers.Binary;
using System.Text;
using Plank.Reading;
using Plank.Reading.Logical;
using Plank.Schema;

namespace Plank.Tests.Reading;

internal sealed class KeyOnlyMapDiscoveryTests
{
    [Test]
    public void ValidKeyOnlyMapSchemaCanBeReadAsASet()
    {
        using var stream = new MemoryStream(CreateFile());
        using var reader = new ParquetReader();

        reader.Reset(stream);

        AssertKeyOnlyMap(reader.Schema);
    }

    [Test]
    public void DiscoveredKeyOnlyMapSchemaCanBeReusedAndWritten()
    {
        var file = CreateFile(useLegacyNames: true);
        var discovered = DiscoverSchema(file);
        using (var strictReader = discovered.CreateReader(new MemoryStream(file)))
            AssertKeyOnlyMap(strictReader.Schema);

        var projection = discovered.LeafProjectionInfos.Single();
        if (projection is not
            {
                IsList: true,
                ListOptional: false,
                ElementOptional: false,
                MaxRepetitionLevel: 1,
                MaxDefinitionLevel: 1
            })
            throw new InvalidOperationException("The key-only MAP projection levels were not preserved.");

        int[][] expected = [[1, 2], [], [3]];
        var path = Path.Combine(Path.GetTempPath(), $"plank-key-only-map-{Guid.NewGuid():N}.parquet");
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

            var rediscovered = DiscoverSchema(File.ReadAllBytes(path));
            AssertKeyOnlyMap(rediscovered);

            using var reader = new ParquetSharp.ParquetFileReader(path);
            using var rowGroupReader = reader.RowGroup(0);
            using var logical = rowGroupReader.Column(0).LogicalReader<int[]>();
            var actual = logical.ReadAll(expected.Length);
            if (actual.Length != expected.Length)
                throw new InvalidOperationException($"Expected {expected.Length} rows, got {actual.Length}.");
            for (var i = 0; i < expected.Length; i++)
                if (!actual[i].AsSpan().SequenceEqual(expected[i]))
                    throw new InvalidOperationException($"Key row {i} did not round-trip.");
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    static void AssertKeyOnlyMap(ParquetSchema schema)
    {
        var map = schema.Definitions.Single();
        if (map.Kind != NodeKind.Map ||
            map.Repetition != ParquetRepetition.Required ||
            map.Children is not
            [
                {
                    Name: "key",
                    Kind: NodeKind.Leaf,
                    Repetition: ParquetRepetition.Required,
                    PhysicalType: ParquetPhysicalType.Int32
                }
            ])
            throw new InvalidOperationException("The valid key-only MAP schema was not preserved as a set.");
    }

    [Test]
    public void KeyOnlyMapRejectsNonRepeatedKeyValueGroup()
        => Assert.Throws<CorruptParquetException>(() =>
            ReadSchema(CreateFile(keyValueRepetition: ParquetRepetition.Required)));

    [Test]
    public void KeyOnlyMapRejectsPrimitiveKeyValueNode()
        => Assert.Throws<CorruptParquetException>(() =>
            ReadSchema(CreateFile(keyValueHasPhysicalType: true)));

    [Test]
    public void KeyOnlyMapRejectsOptionalKey()
        => Assert.Throws<CorruptParquetException>(() =>
            ReadSchema(CreateFile(keyRepetition: ParquetRepetition.Optional)));

    [Test]
    public void MapRejectsRepeatedValue()
        => Assert.Throws<CorruptParquetException>(() =>
            ReadSchema(CreateFile(valueRepetition: ParquetRepetition.Repeated)));

    [Test]
    public void MapRejectsValueWithoutRepetition()
        => Assert.Throws<CorruptParquetException>(() =>
            ReadSchema(CreateFile(valueRepetition: ParquetRepetition.Unspecified)));

    [Test]
    public void MapRejectsRepeatedOuterGroup()
        => Assert.Throws<CorruptParquetException>(() =>
            ReadSchema(CreateFile(mapRepetition: ParquetRepetition.Repeated)));

    static void ReadSchema(byte[] file)
    {
        using var stream = new MemoryStream(file);
        using var reader = new ParquetReader();
        reader.Reset(stream);
    }

    static ParquetSchema DiscoverSchema(byte[] file)
    {
        using var stream = new MemoryStream(file);
        using var reader = new ParquetReader();
        reader.Reset(stream);
        return reader.Schema;
    }

    static byte[] CreateFile(ParquetRepetition mapRepetition = ParquetRepetition.Required,
        ParquetRepetition keyValueRepetition = ParquetRepetition.Repeated,
        ParquetRepetition keyRepetition = ParquetRepetition.Required, bool keyValueHasPhysicalType = false,
        ParquetRepetition? valueRepetition = null, bool useLegacyNames = false)
    {
        byte[] keyValueHeader = keyValueHasPhysicalType
            ? [0x15, 0x02, 0x25, EncodeRepetition(keyValueRepetition)]
            : [0x35, EncodeRepetition(keyValueRepetition)];
        byte[] valueNode = valueRepetition switch
        {
            null => [],
            ParquetRepetition.Unspecified =>
            [
                0x15, 0x02,
                0x38, 0x05, (byte)'v', (byte)'a', (byte)'l', (byte)'u', (byte)'e',
                0x00
            ],
            { } repetition =>
            [
                0x15, 0x02,
                0x25, EncodeRepetition(repetition),
                0x18, 0x05, (byte)'v', (byte)'a', (byte)'l', (byte)'u', (byte)'e',
                0x00
            ]
        };
        var keyValueName = Encoding.UTF8.GetBytes(useLegacyNames ? "map" : "key_value");
        var keyName = Encoding.UTF8.GetBytes(useLegacyNames ? "str" : "key");
        byte[] footer =
        [
            0x15, 0x02,
            0x19, checked((byte)(((valueRepetition is null ? 4 : 5) << 4) | 0x0C)),
            0x48, 0x06, (byte)'s', (byte)'c', (byte)'h', (byte)'e', (byte)'m', (byte)'a',
            0x15, 0x02,
            0x00,
            0x35, EncodeRepetition(mapRepetition),
            0x18, 0x04, (byte)'t', (byte)'a', (byte)'g', (byte)'s',
            0x15, 0x02,
            0x15, 0x02,
            0x00,
            .. keyValueHeader,
            0x18, checked((byte)keyValueName.Length), .. keyValueName,
            0x15, valueRepetition is null ? (byte)0x02 : (byte)0x04,
            0x00,
            0x15, 0x02,
            0x25, EncodeRepetition(keyRepetition),
            0x18, checked((byte)keyName.Length), .. keyName,
            0x00,
            .. valueNode,
            0x16, 0x00,
            0x19, 0x0C,
            0x00
        ];

        using var stream = new MemoryStream();
        stream.Write("PAR1"u8);
        stream.Write(footer);
        Span<byte> footerLength = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(footerLength, checked((uint)footer.Length));
        stream.Write(footerLength);
        stream.Write("PAR1"u8);
        return stream.ToArray();
    }

    static byte EncodeRepetition(ParquetRepetition repetition)
        => checked((byte)(((int)repetition - 1) << 1));
}
