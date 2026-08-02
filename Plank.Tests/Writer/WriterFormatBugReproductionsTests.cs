using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Globalization;
using ParquetSharp;
using Plank.Reading;
using Plank.Schema;
using Plank.Writing;
using PlankLogicalType = Plank.Schema.LogicalType;
using PlankParquetSchema = Plank.Schema.ParquetSchema;

namespace Plank.Tests.Writer;

internal sealed class WriterFormatBugReproductionsTests
{
    [Test]
    public void BooleanRleValuesAreReadableByParquetSharp()
    {
        var expected = new[]
        {
            false, true, false, true, false, true, false, true,
            true, true, true, true, true, true, true, true,
            false, false, false, false, false, false, false, false
        };
        var schema = new PlankParquetSchema([
            ColumnDefinition.RequiredLeaf("value", ParquetPhysicalType.Boolean,
                new ColumnOptions(encodings: ImmutableArray.Create(EncodingKind.Rle)))
        ]);
        var file = WriteFile(schema, writer =>
        {
            var column = writer.CreateSerializedColumn<bool>(schema.LeafColumns[0]);
            var rowGroup = writer.StartRowGroup();
            column.Serialize(expected);
            rowGroup.Write(column);
        });

        AssertBooleanRleLengthPrefix(file);

        var path = Path.Combine(Path.GetTempPath(), $"plank-boolean-rle-{Guid.NewGuid():N}.parquet");
        try
        {
            File.WriteAllBytes(path, file);
            using var reader = new ParquetFileReader(path);
            using var rowGroupReader = reader.RowGroup(0);
            using var valueReader = rowGroupReader.Column(0).LogicalReader<bool>();
            var actual = valueReader.ReadAll(expected.Length);
            if (!actual.AsSpan().SequenceEqual(expected))
                throw new InvalidOperationException(
                    "ParquetSharp did not read the Boolean RLE values written by Plank.");
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Test]
    public void OptionalColumnFooterListsTheRleLevelEncoding()
    {
        var path = Path.Combine(Path.GetTempPath(), $"plank-rle-level-metadata-{Guid.NewGuid():N}.parquet");
        var schema = new PlankParquetSchema([
            ColumnDefinition.OptionalLeaf("value", ParquetPhysicalType.Int32)
        ]);

        try
        {
            using (var stream = File.Create(path))
                WriteFile(stream, schema, writer =>
                {
                    var column = writer.CreateSerializedColumn<int?>(schema.LeafColumns[0]);
                    var rowGroup = writer.StartRowGroup();
                    column.Serialize([1, null, 3]);
                    rowGroup.Write(column);
                });

            using var reader = new ParquetFileReader(path);
            using var rowGroupReader = reader.RowGroup(0);
            using var metadata = rowGroupReader.MetaData.GetColumnChunkMetaData(0);
            if (!metadata.Encodings.Contains(ParquetSharp.Encoding.Rle))
                throw new InvalidOperationException(
                    $"Column metadata omitted the RLE encoding used for definition levels. Actual: {string.Join(", ", metadata.Encodings)}");
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Test]
    public void SignedIntegerLogicalTypeCanBeWritten()
    {
        var schema = new PlankParquetSchema([
            ColumnDefinition.RequiredLeaf("value", ParquetPhysicalType.Int32,
                logicalType: new PlankLogicalType.Int(32, isSigned: true))
        ]);

        _ = WriteFile(schema, writer =>
        {
            var column = writer.CreateSerializedColumn<int>(schema.LeafColumns[0]);
            var rowGroup = writer.StartRowGroup();
            column.Serialize([-1, 0, 1]);
            rowGroup.Write(column);
        });
    }

    [Test]
    public void DecimalByteArrayStatisticsUseSignedNumericOrdering()
    {
        var column = new Plank.Schema.Column("value", ParquetPhysicalType.ByteArray,
            logicalType: new PlankLogicalType.Decimal(3, 0));
        var minimum = new byte[] { 0xff, 0x7f };
        var maximum = new byte[] { 0x00, 0x80 };

        var statistics = ColumnStatistics.Create(column,
            [[0xff], maximum, minimum, [0x01], [0xff, 0xff], [0x00, 0x00]], 0);

        if (!statistics.GetMinValue().SequenceEqual(minimum))
            throw new InvalidOperationException(
                $"Expected decimal -129 to be the minimum, got {Convert.ToHexString(statistics.GetMinValue())}.");
        if (!statistics.GetMaxValue().SequenceEqual(maximum))
            throw new InvalidOperationException(
                $"Expected decimal +128 to be the maximum, got {Convert.ToHexString(statistics.GetMaxValue())}.");
    }

    [Test]
    public void FloatStatisticsCanonicalizeSignedZeroBounds()
    {
        var column = new Plank.Schema.Column("value", ParquetPhysicalType.Float);

        var statistics = ColumnStatistics.Create(column, [+0.0f, -0.0f], 0);

        if ((int)statistics.MinBits != int.MinValue)
            throw new InvalidOperationException("TYPE_ORDER requires -0.0 to be the minimum zero bound.");
        if ((int)statistics.MaxBits != 0)
            throw new InvalidOperationException("TYPE_ORDER requires +0.0 to be the maximum zero bound.");
    }

    [Test]
    public void FloatTypeOrderStatisticsIncludeNanCount()
    {
        var schema = new PlankParquetSchema([
            ColumnDefinition.RequiredLeaf("value", ParquetPhysicalType.Float)
        ]);
        var file = WriteFile(schema, writer =>
        {
            var column = writer.CreateSerializedColumn<float>(schema.LeafColumns[0]);
            var rowGroup = writer.StartRowGroup();
            column.Serialize([float.NaN, 1.0f, float.NaN]);
            rowGroup.Write(column);
        });

        var nanCount = ReadFirstColumnNanCount(file);
        if (nanCount != 2)
            throw new InvalidOperationException(
                "TYPE_ORDER requires nan_count=2 for these values, got " +
                $"{nanCount?.ToString(CultureInfo.InvariantCulture) ?? "missing"}.");
    }

    static byte[] WriteFile(PlankParquetSchema schema, Action<ParquetWriter> write)
    {
        using var stream = new MemoryStream();
        WriteFile(stream, schema, write);
        return stream.ToArray();
    }

    static void WriteFile(Stream stream, PlankParquetSchema schema, Action<ParquetWriter> write)
    {
        var writer = schema.CreateWriter(stream, new ParquetWriterOptions
        {
            Compression = CompressionKind.None
        });
        write(writer);
        writer.CloseFile();
    }

    static void AssertBooleanRleLengthPrefix(byte[] file)
    {
        using var stream = new MemoryStream(file, writable: false);
        using var reader = new Plank.Reading.Physical.ParquetFileReader();
        reader.Reset(stream);
        using var pages = reader.OpenPages(0, 0);
        if (!pages.MoveNext() || pages.CurrentHeader.Encoding != EncodingKind.Rle)
            throw new InvalidOperationException("Expected a Boolean RLE data page.");

        var payload = pages.CurrentPayload;
        if (payload.Length < sizeof(int))
            throw new InvalidOperationException("Boolean RLE values omitted their four-byte length prefix.");
        var encodedLength = BinaryPrimitives.ReadInt32LittleEndian(payload);
        if (encodedLength != payload.Length - sizeof(int))
            throw new InvalidOperationException(
                $"Boolean RLE prefix declares {encodedLength} encoded bytes, actual {payload.Length - sizeof(int)}.");
    }

    static long? ReadFirstColumnNanCount(byte[] file)
    {
        var footerLength = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(file.Length - 8, sizeof(int)));
        var footerOffset = checked(file.Length - 8 - footerLength);
        var reader = new CompactProtocolReader(file.AsSpan(footerOffset, footerLength));
        reader.BeginStruct();
        while (reader.TryReadFieldHeader(out var fieldId, out var type, out var inlineBool))
        {
            if (fieldId == 4)
            {
                var (count, elementType) = reader.ReadListHeader();
                if (count == 0 || elementType != CompactProtocolType.Struct)
                    throw new InvalidOperationException("Expected at least one row group.");
                return ReadRowGroupNanCount(ref reader);
            }

            reader.Skip(type, inlineBool);
        }

        throw new InvalidOperationException("Footer did not contain row groups.");
    }

    static long? ReadRowGroupNanCount(ref CompactProtocolReader reader)
    {
        reader.BeginStruct();
        while (reader.TryReadFieldHeader(out var fieldId, out var type, out var inlineBool))
        {
            if (fieldId == 1)
            {
                var (count, elementType) = reader.ReadListHeader();
                if (count == 0 || elementType != CompactProtocolType.Struct)
                    throw new InvalidOperationException("Expected at least one column chunk.");
                return ReadColumnChunkNanCount(ref reader);
            }

            reader.Skip(type, inlineBool);
        }

        throw new InvalidOperationException("Row group did not contain column chunks.");
    }

    static long? ReadColumnChunkNanCount(ref CompactProtocolReader reader)
    {
        reader.BeginStruct();
        while (reader.TryReadFieldHeader(out var fieldId, out var type, out var inlineBool))
        {
            if (fieldId == 3)
                return ReadColumnMetadataNanCount(ref reader);

            reader.Skip(type, inlineBool);
        }

        throw new InvalidOperationException("Column chunk did not contain metadata.");
    }

    static long? ReadColumnMetadataNanCount(ref CompactProtocolReader reader)
    {
        reader.BeginStruct();
        while (reader.TryReadFieldHeader(out var fieldId, out var type, out var inlineBool))
        {
            if (fieldId == 12)
                return ReadStatisticsNanCount(ref reader);

            reader.Skip(type, inlineBool);
        }

        return null;
    }

    static long? ReadStatisticsNanCount(ref CompactProtocolReader reader)
    {
        reader.BeginStruct();
        while (reader.TryReadFieldHeader(out var fieldId, out var type, out var inlineBool))
        {
            if (fieldId == 9)
                return reader.ReadI64();

            reader.Skip(type, inlineBool);
        }

        return null;
    }
}
