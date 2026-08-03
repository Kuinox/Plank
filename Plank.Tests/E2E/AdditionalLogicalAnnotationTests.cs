using System.Buffers.Binary;
using Plank.Reading;
using Plank.Reading.Logical;
using Plank.Reading.Physical;
using Plank.Schema;
using Plank.Writing;
using PhysicalFileMetadata = Plank.Reading.Physical.ParquetFileMetadata;

namespace Plank.Tests.E2E;

internal sealed class AdditionalLogicalAnnotationTests
{
    [Test]
    public void PhysicalAndLogicalReadersPreserveAdditionalAnnotations()
    {
        var file = CreateAnnotatedFile();

        using (var physicalReader = new ParquetFileReader())
        {
            physicalReader.Reset(new MemoryStream(file));
            var metadata = physicalReader.Metadata;
            AssertKind(metadata, "enum_value"u8, LogicalTypeKind.Enum);
            AssertKind(metadata, "bson_value"u8, LogicalTypeKind.Bson);
            AssertKind(metadata, "float16_value"u8, LogicalTypeKind.Float16);
            AssertKind(metadata, "interval_value"u8, LogicalTypeKind.Interval);
            AssertKind(metadata, "unknown_value"u8, LogicalTypeKind.Unknown);

            var geometry = FindNode(metadata, "geometry_value"u8);
            if (geometry.LogicalType.Kind != LogicalTypeKind.Geometry || !geometry.LogicalType.HasCrs ||
                !metadata.SchemaNodeLogicalTypeCrsUtf8(geometry.Ordinal).SequenceEqual("EPSG:4326"u8) ||
                !metadata.ColumnLogicalTypeCrsUtf8(4).SequenceEqual("EPSG:4326"u8))
                throw new InvalidOperationException("GEOMETRY CRS metadata was not preserved.");

            var geography = FindNode(metadata, "geography_value"u8);
            if (geography.LogicalType.Kind != LogicalTypeKind.Geography || !geography.LogicalType.HasCrs ||
                geography.LogicalType.Algorithm != EdgeInterpolationAlgorithm.Karney ||
                !metadata.SchemaNodeLogicalTypeCrsUtf8(geography.Ordinal).SequenceEqual("OGC:CRS84"u8))
                throw new InvalidOperationException("GEOGRAPHY parameters were not preserved.");

            var variant = FindNode(metadata, "variant_value"u8);
            if (variant.Kind != NodeKind.Group || variant.LogicalType.Kind != LogicalTypeKind.Variant ||
                variant.LogicalType.SpecificationVersion != 1)
                throw new InvalidOperationException("VARIANT version metadata was not preserved on its group.");
        }

        using var logicalReader = new ParquetReader();
        logicalReader.Reset(new MemoryStream(file));
        var definitions = logicalReader.Schema.Definitions;
        if (definitions[0].LogicalType is not LogicalType.Enum ||
            definitions[1].LogicalType is not LogicalType.Bson ||
            definitions[2].LogicalType is not LogicalType.Float16 ||
            definitions[3].LogicalType is not LogicalType.Interval ||
            definitions[4].LogicalType is not LogicalType.Geometry { Crs: "EPSG:4326" } ||
            definitions[5].LogicalType is not LogicalType.Geography
            {
                Crs: "OGC:CRS84",
                Algorithm: EdgeInterpolationAlgorithm.Karney
            } ||
            definitions[6].LogicalType is not LogicalType.Unknown ||
            definitions[7].LogicalType is not LogicalType.Variant { SpecificationVersion: 1 })
            throw new InvalidOperationException("Discovered logical schema did not retain all annotations.");
    }

    [Test]
    public void EstablishedAnnotationsAreRecognizedByParquetSharp()
    {
        var path = Path.Combine(Path.GetTempPath(), $"plank-logical-annotations-{Guid.NewGuid():N}.parquet");
        File.WriteAllBytes(path, CreateAnnotatedFile());
        try
        {
            using var reader = new ParquetSharp.ParquetFileReader(path);
            AssertParquetSharpKind(reader, 0, ParquetSharp.LogicalTypeEnum.Enum);
            AssertParquetSharpKind(reader, 1, ParquetSharp.LogicalTypeEnum.Bson);
            AssertParquetSharpKind(reader, 2, ParquetSharp.LogicalTypeEnum.Float16);
            AssertParquetSharpKind(reader, 3, ParquetSharp.LogicalTypeEnum.Interval);
            AssertParquetSharpKind(reader, 6, ParquetSharp.LogicalTypeEnum.Nil);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Test]
    public void WriterUsesStandardModernAndLegacyAnnotationIds()
    {
        var file = CreateAnnotatedFile();

        AssertAnnotationTags(file, "enum_value"u8, expectedConvertedType: 4, expectedLogicalType: 4);
        AssertAnnotationTags(file, "bson_value"u8, expectedConvertedType: 20, expectedLogicalType: 13);
        AssertAnnotationTags(file, "float16_value"u8, expectedConvertedType: -1, expectedLogicalType: 15);
        AssertAnnotationTags(file, "interval_value"u8, expectedConvertedType: 21, expectedLogicalType: -1);
        AssertAnnotationTags(file, "unknown_value"u8, expectedConvertedType: -1, expectedLogicalType: 11);
        AssertAnnotationTags(file, "geometry_value"u8, expectedConvertedType: -1, expectedLogicalType: 17);
        AssertAnnotationTags(file, "geography_value"u8, expectedConvertedType: -1, expectedLogicalType: 18);
        AssertAnnotationTags(file, "variant_value"u8, expectedConvertedType: -1, expectedLogicalType: 16);
    }

    [Test]
    public void LegacyConvertedAnnotationsRemainDiscoverable()
    {
        AssertLegacyAnnotation(ParquetPhysicalType.ByteArray, convertedType: 4, LogicalTypeKind.Enum);
        AssertLegacyAnnotation(ParquetPhysicalType.ByteArray, convertedType: 20, LogicalTypeKind.Bson);
        AssertLegacyAnnotation(ParquetPhysicalType.FixedLenByteArray, convertedType: 21,
            LogicalTypeKind.Interval);
    }

    [Test]
    public void AnnotationPhysicalShapesAreValidated()
    {
        Assert.Throws<ArgumentException>(() => ColumnDefinition.RequiredLeaf("bad_float16",
            ParquetPhysicalType.FixedLenByteArray, new ColumnOptions(typeLength: 4),
            new LogicalType.Float16()));
        Assert.Throws<ArgumentException>(() => ColumnDefinition.RequiredLeaf("bad_interval",
            ParquetPhysicalType.ByteArray, logicalType: new LogicalType.Interval()));
        Assert.Throws<ArgumentException>(() => ColumnDefinition.RequiredLeaf("bad_geometry",
            ParquetPhysicalType.FixedLenByteArray, new ColumnOptions(typeLength: 16),
            new LogicalType.Geometry()));
        Assert.Throws<ArgumentException>(() => ColumnDefinition.RequiredLeaf("bad_variant",
            ParquetPhysicalType.ByteArray, logicalType: new LogicalType.Variant(1)));
        Assert.Throws<ArgumentException>(() => ColumnDefinition.RequiredLeaf("bad_unknown",
            ParquetPhysicalType.ByteArray, logicalType: new LogicalType.Unknown()));
    }

    static byte[] CreateAnnotatedFile()
    {
        var schema = new ParquetSchema([
            ColumnDefinition.RequiredLeaf("enum_value", ParquetPhysicalType.ByteArray,
                logicalType: new LogicalType.Enum()),
            ColumnDefinition.RequiredLeaf("bson_value", ParquetPhysicalType.ByteArray,
                logicalType: new LogicalType.Bson()),
            ColumnDefinition.RequiredLeaf("float16_value", ParquetPhysicalType.FixedLenByteArray,
                new ColumnOptions(typeLength: 2), new LogicalType.Float16()),
            ColumnDefinition.RequiredLeaf("interval_value", ParquetPhysicalType.FixedLenByteArray,
                new ColumnOptions(typeLength: 12), new LogicalType.Interval()),
            ColumnDefinition.RequiredLeaf("geometry_value", ParquetPhysicalType.ByteArray,
                logicalType: new LogicalType.Geometry("EPSG:4326")),
            ColumnDefinition.RequiredLeaf("geography_value", ParquetPhysicalType.ByteArray,
                logicalType: new LogicalType.Geography("OGC:CRS84", EdgeInterpolationAlgorithm.Karney)),
            ColumnDefinition.OptionalLeaf("unknown_value", ParquetPhysicalType.ByteArray,
                logicalType: new LogicalType.Unknown()),
            ColumnDefinition.OptionalGroup("variant_value", new LogicalType.Variant(1),
                ColumnDefinition.RequiredLeaf("metadata", ParquetPhysicalType.ByteArray),
                ColumnDefinition.RequiredLeaf("value", ParquetPhysicalType.ByteArray))
        ]);

        using var stream = new MemoryStream();
        var writer = schema.CreateWriter(stream, new ParquetWriterOptions
        {
            Compression = CompressionKind.None
        });
        var rowGroup = writer.StartRowGroup();
        WriteBinary(rowGroup, schema.LeafColumns[0], ["one"u8.ToArray()]);
        WriteBinary(rowGroup, schema.LeafColumns[1], [[5, 0, 0, 0, 0]]);
        WriteBinary(rowGroup, schema.LeafColumns[2], [[0x00, 0x3C]]);
        WriteBinary(rowGroup, schema.LeafColumns[3], [new byte[12]]);
        WriteBinary(rowGroup, schema.LeafColumns[4], [[1, 1, 0, 0, 0]]);
        WriteBinary(rowGroup, schema.LeafColumns[5], [[1, 1, 0, 0, 0]]);
        WriteBinary(rowGroup, schema.LeafColumns[6], [null]);
        WriteBinary(rowGroup, schema.LeafColumns[7], [[1, 0, 0]]);
        WriteBinary(rowGroup, schema.LeafColumns[8], [[0]]);
        writer.CloseFile();
        return stream.ToArray();
    }

    static void WriteBinary(RowGroupWriter rowGroup, LeafColumn column, byte[]?[] values)
    {
        var serialized = rowGroup.CreateSerializedColumn<byte[]?>(column);
        serialized.Serialize(values);
        rowGroup.Write(serialized);
    }

    static void AssertKind(PhysicalFileMetadata metadata, ReadOnlySpan<byte> name, LogicalTypeKind expected)
    {
        var actual = FindNode(metadata, name).LogicalType.Kind;
        if (actual != expected)
            throw new InvalidOperationException($"Expected logical type {expected}, got {actual}.");
    }

    static void AssertParquetSharpKind(ParquetSharp.ParquetFileReader reader, int columnIndex,
        ParquetSharp.LogicalTypeEnum expected)
    {
        using var logicalType = reader.FileMetaData.Schema.Column(columnIndex).LogicalType;
        if (logicalType.Type != expected)
            throw new InvalidOperationException(
                $"ParquetSharp expected logical type {expected}, got {logicalType.Type}.");
    }

    static ParquetSchemaNodeInfo FindNode(PhysicalFileMetadata metadata, ReadOnlySpan<byte> name)
    {
        for (var i = 0; i < metadata.SchemaNodeCount; i++)
            if (metadata.SchemaNodeNameUtf8(i).SequenceEqual(name))
                return metadata.SchemaNodes[i];

        throw new InvalidOperationException("Schema node was not found.");
    }

    static void AssertAnnotationTags(ReadOnlySpan<byte> file, ReadOnlySpan<byte> name,
        int expectedConvertedType, int expectedLogicalType)
    {
        var footerLength = BinaryPrimitives.ReadInt32LittleEndian(file.Slice(file.Length - 8, sizeof(int)));
        var footerOffset = checked(file.Length - 8 - footerLength);
        var reader = new CompactProtocolReader(file.Slice(footerOffset, footerLength));
        reader.BeginStruct();
        while (reader.TryReadFieldHeader(out var fieldId, out var type, out var inlineBool))
        {
            if (fieldId != 2)
            {
                reader.Skip(type, inlineBool);
                continue;
            }

            var (count, elementType) = reader.ReadListHeader();
            if (elementType != CompactProtocolType.Struct)
                throw new InvalidOperationException("Footer schema is not a list of structs.");
            for (var i = 0U; i < count; i++)
            {
                if (!ReadSchemaNodeAnnotation(ref reader, name, out var convertedType, out var logicalType))
                    continue;
                if (convertedType != expectedConvertedType || logicalType != expectedLogicalType)
                    throw new InvalidOperationException(
                        $"Unexpected annotation tags for '{System.Text.Encoding.UTF8.GetString(name)}'.");
                return;
            }
        }

        throw new InvalidOperationException("Annotated schema node was not found in the footer.");
    }

    static bool ReadSchemaNodeAnnotation(ref CompactProtocolReader reader, ReadOnlySpan<byte> expectedName,
        out int convertedType, out int logicalType)
    {
        var name = ReadOnlySpan<byte>.Empty;
        convertedType = -1;
        logicalType = -1;
        reader.BeginStruct();
        while (reader.TryReadFieldHeader(out var fieldId, out var type, out var inlineBool))
            switch (fieldId)
            {
                case 4:
                    name = reader.ReadBinary();
                    break;
                case 6:
                    convertedType = reader.ReadI32();
                    break;
                case 10:
                    logicalType = ReadLogicalTypeId(ref reader);
                    break;
                default:
                    reader.Skip(type, inlineBool);
                    break;
            }

        return name.SequenceEqual(expectedName);
    }

    static int ReadLogicalTypeId(ref CompactProtocolReader reader)
    {
        var logicalType = -1;
        reader.BeginStruct();
        while (reader.TryReadFieldHeader(out var fieldId, out var type, out var inlineBool))
        {
            logicalType = fieldId;
            reader.Skip(type, inlineBool);
        }
        return logicalType;
    }

    static void AssertLegacyAnnotation(ParquetPhysicalType physicalType, byte convertedType,
        LogicalTypeKind expected)
    {
        using var stream = CreateLegacyAnnotationFile(physicalType, convertedType);
        using var reader = new ParquetFileReader();
        reader.Reset(stream);
        var actual = reader.Metadata.SchemaNodes[1].LogicalType.Kind;
        if (actual != expected)
            throw new InvalidOperationException($"Expected legacy annotation {expected}, got {actual}.");
    }

    static MemoryStream CreateLegacyAnnotationFile(ParquetPhysicalType physicalType, byte convertedType)
    {
        byte[] footer =
        [
            0x15, 0x02,
            0x19, 0x2C,
            0x48, 0x06, (byte)'s', (byte)'c', (byte)'h', (byte)'e', (byte)'m', (byte)'a',
            0x15, 0x02,
            0x00,
            0x15, checked((byte)((byte)physicalType << 1)),
            0x25, 0x00,
            0x18, 0x05, (byte)'v', (byte)'a', (byte)'l', (byte)'u', (byte)'e',
            0x25, checked((byte)(convertedType << 1)),
            0x00,
            0x16, 0x00,
            0x19, 0x0C,
            0x00
        ];

        var stream = new MemoryStream();
        stream.Write("PAR1"u8);
        stream.Write(footer);
        Span<byte> footerLength = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(footerLength, checked((uint)footer.Length));
        stream.Write(footerLength);
        stream.Write("PAR1"u8);
        stream.Position = 0;
        return stream;
    }
}
