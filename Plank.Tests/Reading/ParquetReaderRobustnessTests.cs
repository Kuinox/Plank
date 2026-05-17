using System.Collections.Immutable;
using Plank.Reading;
using Plank.Schema;

namespace Plank.Tests.Reading;

internal sealed class ParquetReaderRobustnessTests
{
    static readonly ParquetSchema[] Schemas =
    [
        Schema(Col("c0", ParquetPhysicalType.Int32, EncodingKind.Plain)),
        Schema(Col("c0", ParquetPhysicalType.ByteArray, EncodingKind.Plain)),
        Schema(Col("c0", ParquetPhysicalType.Int32, EncodingKind.DeltaBinaryPacked),
               Col("c1", ParquetPhysicalType.Boolean, EncodingKind.Plain)),
        Schema(Col("c0", ParquetPhysicalType.Int64, EncodingKind.Plain),
               Col("c1", ParquetPhysicalType.Double, EncodingKind.Plain)),
        Schema(Col("c0", ParquetPhysicalType.Int32, EncodingKind.RleDictionary)),
        Schema(Col("c0", ParquetPhysicalType.ByteArray, EncodingKind.DeltaLengthByteArray)),
        Schema(Col("c0", ParquetPhysicalType.Boolean, EncodingKind.Plain),
               Col("c1", ParquetPhysicalType.Int32, EncodingKind.Plain),
               Col("c2", ParquetPhysicalType.Int64, EncodingKind.Plain),
               Col("c3", ParquetPhysicalType.Double, EncodingKind.Plain),
               Col("c4", ParquetPhysicalType.ByteArray, EncodingKind.Plain)),
        Schema(Col("c0", ParquetPhysicalType.ByteArray, EncodingKind.DeltaByteArray)),
    ];

    [Test]
    public void EmptyInput_DoesNotCrash()
        => AssertDoesNotCrash([]);

    [Test]
    public void AllZeroInput_DoesNotCrash()
        => AssertDoesNotCrash(new byte[64]);

    [Test]
    public void TruncatedMagic_DoesNotCrash()
        => AssertDoesNotCrash([0x00, 0x50, 0x41, 0x52, 0x31]);

    [Test]
    public void ByteStreamSplitInt32PayloadTooShort_DoesNotCrash()
        => AssertDoesNotCrash(FixtureBytes("ByteStreamSplitInt32PayloadTooShort"));

    [Test]
    public void ColumnCountExceedsRemainingInput_DoesNotCrash()
        => AssertDoesNotCrash(FixtureBytes("ColumnCountExceedsRemainingInput"));

    [Test]
    public void Crash001_DoesNotCrash()
        => AssertDoesNotCrash(FixtureBytes("crash-001"));

    [Test]
    public void DefinitionLevelLiteralByteCountExceedsPayload_DoesNotCrash()
        => AssertDoesNotCrash(FixtureBytes("DefinitionLevelLiteralByteCountExceedsPayload"));

    [Test]
    public void DefinitionLevelLiteralGroupCountTooLarge_DoesNotCrash()
        => AssertDoesNotCrash(FixtureBytes("DefinitionLevelLiteralGroupCountTooLarge"));

    [Test]
    public void DictionaryIndexesNullsOutOfBounds_DoesNotCrash()
        => AssertDoesNotCrash(FixtureBytes("DictionaryIndexesNullsOutOfBounds"));

    [Test]
    public void DictionaryLiteralRunBeforeRleRun_DoesNotCrash()
        => AssertDoesNotCrash(FixtureBytes("DictionaryLiteralRunBeforeRleRun"));

    [Test]
    public void NegativeCompressedPageSize_DoesNotCrash()
        => AssertDoesNotCrash(FixtureBytes("NegativeCompressedPageSize"));

    [Test]
    public void NegativeI64OffsetInFooter_DoesNotCrash()
        => AssertDoesNotCrash(FixtureBytes("NegativeI64OffsetInFooter"));

    [Test]
    public void PlainDoublePayloadTooShort_DoesNotCrash()
        => AssertDoesNotCrash(FixtureBytes("PlainDoublePayloadTooShort"));

    [Test]
    public void PlainInt32PayloadTooShort_DoesNotCrash()
        => AssertDoesNotCrash(FixtureBytes("PlainInt32PayloadTooShort"));

    [Test]
    public void PlainInt64PayloadTooShort_DoesNotCrash()
        => AssertDoesNotCrash(FixtureBytes("PlainInt64PayloadTooShort"));

    [Test]
    public void RleBitPackedHybridZeroBitWidth_DoesNotCrash()
        => AssertDoesNotCrash(FixtureBytes("RleBitPackedHybridZeroBitWidth"));

    [Test]
    public void RowGroupCountOverflow_DoesNotCrash()
        => AssertDoesNotCrash(FixtureBytes("RowGroupCountOverflow"));

    [Test]
    public void SnappyDestinationTooSmall_DoesNotCrash()
        => AssertDoesNotCrash(FixtureBytes("SnappyDestinationTooSmall"));

    [Test]
    public void ThriftNestingDepthExceedsMaximum_DoesNotCrash()
        => AssertDoesNotCrash(FixtureBytes("ThriftNestingDepthExceedsMaximum"));

    static void AssertDoesNotCrash(byte[] data)
    {
        var schemaIndex = data.Length == 0 ? 0 : data[0] % Schemas.Length;
        var fileBytes = data.Length == 0 ? [] : data[1..];
        var schema = Schemas[schemaIndex];
        var source = new MemoryReadSource(fileBytes);
        try
        {
            using var reader = schema.CreateReader(source);
            foreach (var token in reader.EnumerateRowGroups())
            {
                using var rowGroup = reader.OpenRowGroup(source, token);
                foreach (var column in schema.Columns)
                    DrainColumn(rowGroup, column);
            }
        }
        catch (Exception ex) when (ex is CorruptParquetException
            or InvalidOperationException
            or NotSupportedException
            or EndOfStreamException) { }
    }

    static void DrainColumn(RowGroupReader rowGroup, Column column)
    {
        switch (column.PhysicalType)
        {
            case ParquetPhysicalType.Boolean:
                DrainPages(rowGroup.Column<bool>(column).Pages); break;
            case ParquetPhysicalType.Int32:
                DrainPages(rowGroup.Column<int>(column).Pages); break;
            case ParquetPhysicalType.Int64:
                DrainPages(rowGroup.Column<long>(column).Pages); break;
            case ParquetPhysicalType.Double:
                DrainPages(rowGroup.Column<double>(column).Pages); break;
            case ParquetPhysicalType.ByteArray:
                DrainPages(rowGroup.Column<byte[]>(column).Pages); break;
        }
    }

    static void DrainPages<T>(ColumnPageEnumerable<T> pages)
    {
        foreach (var page in pages)
        {
            var span = page.Values.Span;
            for (var i = 0; i < span.Length; i++)
                _ = span[i];
        }
    }

    static byte[] FixtureBytes(string name)
        => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Reading", "Fixtures", name + ".parquet"));

    static ParquetSchema Schema(params Column[] columns)
        => new(columns.ToImmutableArray());

    static Column Col(string name, ParquetPhysicalType type, EncodingKind encoding)
        => new(name, type, new ColumnOptions(encodings: ImmutableArray.Create(encoding)));
}
