using System.Collections.Immutable;
using Plank.Reading;
using Plank.Reading.Logical;
using Plank.Schema;
using Plank.Writing;
using Plank.Writing.Thrift;

namespace Plank.Tests.Reading;

// The format does not forbid bytes after a data page's values (fastparquet writes eight zero bytes after PLAIN
// byte arrays), and Arrow ignores them. Only BYTE_STREAM_SPLIT, whose streams must end at the end of the page, and
// padding between ALP vectors are forbidden.
internal sealed class PageTrailingBytesTests
{
    static readonly (ParquetPhysicalType Type, EncodingKind Encoding)[] Encodings =
    [
        (ParquetPhysicalType.Int32, EncodingKind.Plain),
        (ParquetPhysicalType.Int32, EncodingKind.DeltaBinaryPacked),
        (ParquetPhysicalType.Int32, EncodingKind.RleDictionary),
        (ParquetPhysicalType.Int64, EncodingKind.Plain),
        (ParquetPhysicalType.Int64, EncodingKind.DeltaBinaryPacked),
        (ParquetPhysicalType.Float, EncodingKind.Plain),
        (ParquetPhysicalType.Float, EncodingKind.Alp),
        (ParquetPhysicalType.Double, EncodingKind.Plain),
        (ParquetPhysicalType.Double, EncodingKind.Alp),
        (ParquetPhysicalType.Boolean, EncodingKind.Plain),
        (ParquetPhysicalType.Boolean, EncodingKind.Rle),
        (ParquetPhysicalType.ByteArray, EncodingKind.Plain),
        (ParquetPhysicalType.ByteArray, EncodingKind.DeltaLengthByteArray),
        (ParquetPhysicalType.ByteArray, EncodingKind.DeltaByteArray),
        (ParquetPhysicalType.ByteArray, EncodingKind.RleDictionary),
        (ParquetPhysicalType.FixedLenByteArray, EncodingKind.Plain),
        (ParquetPhysicalType.FixedLenByteArray, EncodingKind.DeltaByteArray),
    ];

    static readonly ParquetPhysicalType[] ByteStreamSplitTypes =
        [ParquetPhysicalType.Int32, ParquetPhysicalType.Float, ParquetPhysicalType.FixedLenByteArray];

    public static IEnumerable<(ParquetPhysicalType, EncodingKind, ParquetDataPageVersion, bool)> AcceptedCases()
    {
        foreach (var (type, encoding) in Encodings)
        foreach (var version in new[] { ParquetDataPageVersion.V1, ParquetDataPageVersion.V2 })
        foreach (var optional in new[] { false, true })
        {
            // The writer has no required FIXED_LEN_BYTE_ARRAY DELTA_BYTE_ARRAY path.
            if (type == ParquetPhysicalType.FixedLenByteArray && encoding == EncodingKind.DeltaByteArray && !optional)
                continue;
            yield return (type, encoding, version, optional);
        }
    }

    public static IEnumerable<(ParquetPhysicalType, ParquetDataPageVersion, bool)> ByteStreamSplitCases()
    {
        foreach (var type in ByteStreamSplitTypes)
        foreach (var version in new[] { ParquetDataPageVersion.V1, ParquetDataPageVersion.V2 })
        foreach (var optional in new[] { false, true })
        {
            // The writer has no optional FIXED_LEN_BYTE_ARRAY BYTE_STREAM_SPLIT path.
            if (type == ParquetPhysicalType.FixedLenByteArray && optional)
                continue;
            yield return (type, version, optional);
        }
    }

    [Test]
    [MethodDataSource(nameof(AcceptedCases))]
    public void IgnoresBytesAfterPageValues(ParquetPhysicalType type, EncodingKind encoding,
        ParquetDataPageVersion version, bool optional)
    {
        var expected = Write(type, encoding, version, optional, trailing: 0);
        var padded = Write(type, encoding, version, optional, trailing: 8);
        foreach (var api in Enum.GetValues<ReadApi>())
        {
            var want = Read(expected, type, optional, api);
            var got = Read(padded, type, optional, api);
            var wantCount = optional && api == ReadApi.Nested ? 4 : 5;
            if (want.Count != wantCount || !got.SequenceEqual(want))
                throw new InvalidOperationException(
                    $"{api}: read [{string.Join(", ", got)}], expected [{string.Join(", ", want)}].");
        }
    }

    [Test]
    [MethodDataSource(nameof(ByteStreamSplitCases))]
    public void RejectsBytesAfterByteStreamSplitStreams(ParquetPhysicalType type, ParquetDataPageVersion version,
        bool optional)
    {
        var padded = Write(type, EncodingKind.ByteStreamSplit, version, optional, trailing: 8);
        foreach (var api in Enum.GetValues<ReadApi>())
            Assert.Throws<CorruptParquetException>(() => Read(padded, type, optional, api));
    }

    internal enum ReadApi
    {
        Flat,
        FlatBorrowed,
        Nested,
    }

    static object Values(ParquetPhysicalType type, bool optional) => (type, optional) switch
    {
        (ParquetPhysicalType.Int32, false) => new[] { 1, 2, 3, 4, 1 },
        (ParquetPhysicalType.Int32, true) => new int?[] { 1, null, 3, 4, 1 },
        (ParquetPhysicalType.Int64, false) => new[] { 1L, 2, 3, 4, 1 },
        (ParquetPhysicalType.Int64, true) => new long?[] { 1, null, 3, 4, 1 },
        (ParquetPhysicalType.Float, false) => new[] { 1.5f, 2, 3, 4, 1.5f },
        (ParquetPhysicalType.Float, true) => new float?[] { 1.5f, null, 3, 4, 1.5f },
        (ParquetPhysicalType.Double, false) => new[] { 1.5, 2, 3, 4, 1.5 },
        (ParquetPhysicalType.Double, true) => new double?[] { 1.5, null, 3, 4, 1.5 },
        (ParquetPhysicalType.Boolean, false) => new[] { true, false, true, true, false },
        (ParquetPhysicalType.Boolean, true) => new bool?[] { true, null, true, true, false },
        (ParquetPhysicalType.ByteArray, false) => new byte[][] { [1], [], [2, 3], [1], [4, 5, 6] },
        (ParquetPhysicalType.ByteArray, true) => new byte[]?[] { [1], null, [2, 3], [1], [] },
        (ParquetPhysicalType.FixedLenByteArray, false) =>
            new byte[][] { [1, 2, 3, 4], [5, 6, 7, 8], [1, 2, 3, 4], [9, 9, 9, 9], [0, 0, 0, 0] },
        (ParquetPhysicalType.FixedLenByteArray, true) =>
            new byte[]?[] { [1, 2, 3, 4], null, [1, 2, 3, 4], [9, 9, 9, 9], [0, 0, 0, 0] },
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    static byte[] Write(ParquetPhysicalType type, EncodingKind encoding, ParquetDataPageVersion version, bool optional,
        int trailing)
    {
        var options = new ColumnOptions(encodings: ImmutableArray.Create(encoding),
            typeLength: type == ParquetPhysicalType.FixedLenByteArray ? 4u : 0u);
        var schema = new ParquetSchema([
            optional
                ? ColumnDefinition.OptionalLeaf("value", type, options)
                : ColumnDefinition.RequiredLeaf("value", type, options)
        ]);
        using var output = new MemoryStream();
        using (var writer = schema.CreateWriter(output, new ParquetWriterOptions
            { Compression = CompressionKind.None, DataPageVersion = version }))
        {
            var rowGroup = writer.StartRowGroup();
            switch (Values(type, optional))
            {
                case int[] v: Serialize(writer, rowGroup, schema, v, version, trailing); break;
                case int?[] v: Serialize(writer, rowGroup, schema, v, version, trailing); break;
                case long[] v: Serialize(writer, rowGroup, schema, v, version, trailing); break;
                case long?[] v: Serialize(writer, rowGroup, schema, v, version, trailing); break;
                case float[] v: Serialize(writer, rowGroup, schema, v, version, trailing); break;
                case float?[] v: Serialize(writer, rowGroup, schema, v, version, trailing); break;
                case double[] v: Serialize(writer, rowGroup, schema, v, version, trailing); break;
                case double?[] v: Serialize(writer, rowGroup, schema, v, version, trailing); break;
                case bool[] v: Serialize(writer, rowGroup, schema, v, version, trailing); break;
                case bool?[] v: Serialize(writer, rowGroup, schema, v, version, trailing); break;
                case byte[]?[] v: Serialize(writer, rowGroup, schema, v, version, trailing); break;
            }
            writer.CloseFile();
        }
        return output.ToArray();
    }

    static void Serialize<T>(ParquetWriter writer, RowGroupWriter rowGroup, ParquetSchema schema, T[] values,
        ParquetDataPageVersion version, int trailing)
    {
        var column = writer.CreateSerializedColumn<T>(schema.LeafColumns[0]);
        column.Serialize(values);
        if (trailing > 0)
            AppendTrailingBytes(column.Pages, version, trailing);
        rowGroup.Write(column);
    }

    // Grows each uncompressed data page by `trailing` zero bytes after its values and rewrites the header sizes.
    static void AppendTrailingBytes(PageList pages, ParquetDataPageVersion version, int trailing)
    {
        for (var i = 0; i < pages.Count; i++)
        {
            ref var page = ref pages[i];
            if (page.Kind == PageKind.Dictionary)
                continue;
            page.Content.Write(new byte[trailing]);
            var size = page.Content.WrittenLength;
            page.UncompressedContentSize = size;
            page.Header.Reset();
            if (version == ParquetDataPageVersion.V1)
                ParquetMetadataThriftWriter.WriteDataPageHeaderV1(ref page.Header, page.ValueCount, page.Encoding,
                    size, size, page.Statistics, null);
            else
                ParquetMetadataThriftWriter.WriteDataPageHeaderV2(ref page.Header, page.RowCount, page.ValueCount,
                    page.NullCount, page.RepetitionLevelsByteLength, page.DefinitionLevelsByteLength, page.Encoding,
                    size, size, false, page.Statistics, null);
        }
    }

    static List<string?> Read(byte[] file, ParquetPhysicalType type, bool optional, ReadApi api)
    {
        using var reader = new ParquetReader(new ParquetReaderOptions { BorrowBinaryValues = api == ReadApi.FlatBorrowed });
        reader.Reset(new MemoryStream(file));
        var group = reader.RowGroups[0];
        var result = new List<string?>();
        switch (type)
        {
            case ParquetPhysicalType.Int32: ReadNumeric<int>(group, optional, api, result); break;
            case ParquetPhysicalType.Int64: ReadNumeric<long>(group, optional, api, result); break;
            case ParquetPhysicalType.Float: ReadNumeric<float>(group, optional, api, result); break;
            case ParquetPhysicalType.Double: ReadNumeric<double>(group, optional, api, result); break;
            case ParquetPhysicalType.Boolean: ReadNumeric<bool>(group, optional, api, result); break;
            default:
                if (api == ReadApi.Nested)
                    foreach (var buffer in group.NestedColumn<byte>(0))
                        AddBinary(buffer.Values, result);
                else
                    foreach (var buffer in group.Column<byte>(0))
                        AddBinary(buffer, result);
                break;
        }
        return result;
    }

    static void ReadNumeric<T>(RowGroup group, bool optional, ReadApi api, List<string?> result)
        where T : struct
    {
        if (api == ReadApi.Nested)
            foreach (var buffer in group.NestedColumn<T>(0))
                foreach (var value in buffer.Values.Values)
                    result.Add(value.ToString());
        else if (optional)
            foreach (var buffer in group.Column<T?>(0))
                foreach (var value in buffer.Values)
                    result.Add(value?.ToString());
        else
            foreach (var buffer in group.Column<T>(0))
                foreach (var value in buffer.Values)
                    result.Add(value.ToString());
    }

    static void AddBinary(ColumnBuffer<byte> buffer, List<string?> result)
    {
        for (var i = 0; i < buffer.Count; i++)
            result.Add(buffer.IsNull(i) ? null : Convert.ToHexString(buffer.GetValue(i)));
    }
}
