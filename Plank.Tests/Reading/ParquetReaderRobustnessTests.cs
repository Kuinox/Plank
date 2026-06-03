using System.Collections.Immutable;
using Plank.Fuzzing;
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
    [MethodDataSource(nameof(CorpusFiles))]
    public void CorpusFile_DoesNotCrash(string filePath)
        => PlankReaderFuzzTarget.Execute(File.ReadAllBytes(filePath));

    public static string[] CorpusFiles()
        => Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "Reading", "Fixtures", "Corpus"), "*.bin");

    [Test]
    [Arguments("ByteStreamSplitInt32PayloadTooShort.parquet")]
    [Arguments("ColumnCountExceedsRemainingInput.parquet")]
    [Arguments("crash-001.parquet")]
    [Arguments("DefinitionLevelLiteralByteCountExceedsPayload.parquet")]
    [Arguments("DefinitionLevelLiteralGroupCountTooLarge.parquet")]
    [Arguments("DictionaryIndexesNullsOutOfBounds.parquet")]
    [Arguments("DictionaryLiteralRunBeforeRleRun.parquet")]
    [Arguments("DictionaryPageValueCountExceedsPayload.parquet")]
    [Arguments("NegativeCompressedPageSize.parquet")]
    [Arguments("NegativeI64OffsetInFooter.parquet")]
    [Arguments("PlainDoublePayloadTooShort.parquet")]
    [Arguments("PlainInt32PayloadTooShort.parquet")]
    [Arguments("PlainInt64PayloadTooShort.parquet")]
    [Arguments("RleBitPackedHybridZeroBitWidth.parquet")]
    [Arguments("RowGroupCountOverflow.parquet")]
    [Arguments("SnappyDestinationTooSmall.parquet")]
    [Arguments("ThriftNestingDepthExceedsMaximum.parquet")]
    [Arguments("BrotliInvalidOperationException.parquet")]
    public void Fixture_DoesNotCrash(string fileName)
        => AssertDoesNotCrash(FixtureBytes(fileName));

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
                using var rowGroup = reader.OpenRowGroup(token);
                foreach (var column in reader.Schema.Columns)
                    DrainColumn(rowGroup, column);
            }
        }
        catch (Exception ex) when (ex is CorruptParquetException or NotSupportedException or InvalidOperationException) { }
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

    static byte[] FixtureBytes(string fileName)
        => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Reading", "Fixtures", fileName));

    static ParquetSchema Schema(params Column[] columns)
        => new(columns.ToImmutableArray());

    static Column Col(string name, ParquetPhysicalType type, EncodingKind encoding)
        => new(name, type, new ColumnOptions(encodings: ImmutableArray.Create(encoding)));
}
