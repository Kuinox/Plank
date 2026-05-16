using System.Collections.Immutable;
using Plank.Fuzzing;
using Plank.Schema;
using Plank.Writing;

namespace Plank.Tests.Fuzzing;

/// <summary>
/// Generates AFL corpus seeds for PlankReaderFuzzTarget.
/// Each seed file = schema-selector byte + valid parquet file bytes.
/// Run WriteSeeds() to refresh fuzz/reader-corpus/.
/// </summary>
static class ReaderSeedGenerator
{
    const string CorpusDir = "../../../../fuzz/reader-corpus";

    public static void WriteSeeds()
    {
        Directory.CreateDirectory(CorpusDir);
        foreach (var (name, bytes) in AllSeeds())
            File.WriteAllBytes(Path.Combine(CorpusDir, name), bytes);
    }

    public static IEnumerable<(string Name, byte[] Bytes)> AllSeeds()
    {
        yield return ("schema0-i32-plain", BuildSeed(0, WriteInt32Plain));
        yield return ("schema1-bin-plain", BuildSeed(1, WriteBinPlain));
        yield return ("schema2-i32delta-bool", BuildSeed(2, WriteInt32DeltaBool));
        yield return ("schema3-i64-dbl", BuildSeed(3, WriteInt64Double));
        yield return ("schema4-i32-rledict", BuildSeed(4, WriteInt32RleDict));
        yield return ("schema5-bin-deltalength", BuildSeed(5, WriteBinDeltaLength));
        yield return ("schema6-all-types", BuildSeed(6, WriteAllTypes));
        yield return ("schema7-bin-deltabyte", BuildSeed(7, WriteBinDeltaByte));
    }

    static byte[] BuildSeed(byte schemaIndex, Action<Stream> writeParquet)
    {
        using var ms = new MemoryStream();
        ms.WriteByte(schemaIndex);
        writeParquet(ms);
        return ms.ToArray();
    }

    static void WriteInt32Plain(Stream stream)
    {
        var schema = new ParquetSchema(ImmutableArray.Create(
            new Column("c0", ParquetPhysicalType.Int32,
                new ColumnOptions(encodings: ImmutableArray.Create(EncodingKind.Plain)))));
        var writer = schema.CreateWriter(stream, new ParquetWriterOptions { Compression = CompressionKind.None });
        var col = writer.CreateSerializedColumn<int>(schema.Columns[0]);
        col.Serialize([1, 2, 3, -1, 100, int.MinValue, int.MaxValue, 0]);
        var rg = writer.StartRowGroup();
        rg.Write(col);
        writer.CloseFile();
    }

    static void WriteBinPlain(Stream stream)
    {
        var schema = new ParquetSchema(ImmutableArray.Create(
            new Column("c0", ParquetPhysicalType.ByteArray,
                new ColumnOptions(encodings: ImmutableArray.Create(EncodingKind.Plain)))));
        var writer = schema.CreateWriter(stream, new ParquetWriterOptions { Compression = CompressionKind.None });
        var col = writer.CreateSerializedColumn<byte[]>(schema.Columns[0]);
        col.Serialize([[], [0x00], [0x41, 0x42, 0x43], [0xFF, 0xFE], new byte[16]]);
        var rg = writer.StartRowGroup();
        rg.Write(col);
        writer.CloseFile();
    }

    static void WriteInt32DeltaBool(Stream stream)
    {
        var schema = new ParquetSchema(ImmutableArray.Create(
            new Column("c0", ParquetPhysicalType.Int32,
                new ColumnOptions(encodings: ImmutableArray.Create(EncodingKind.DeltaBinaryPacked))),
            new Column("c1", ParquetPhysicalType.Boolean,
                new ColumnOptions(encodings: ImmutableArray.Create(EncodingKind.Plain)))));
        var writer = schema.CreateWriter(stream, new ParquetWriterOptions { Compression = CompressionKind.None });
        var c0 = writer.CreateSerializedColumn<int>(schema.Columns[0]);
        var c1 = writer.CreateSerializedColumn<bool>(schema.Columns[1]);
        c0.Serialize([0, 1, 2, 5, 10, 20, 50, 100]);
        c1.Serialize([true, false, true, true, false, false, true, false]);
        var rg = writer.StartRowGroup();
        rg.Write(c0);
        rg.Write(c1);
        writer.CloseFile();
    }

    static void WriteInt64Double(Stream stream)
    {
        var schema = new ParquetSchema(ImmutableArray.Create(
            new Column("c0", ParquetPhysicalType.Int64,
                new ColumnOptions(encodings: ImmutableArray.Create(EncodingKind.Plain))),
            new Column("c1", ParquetPhysicalType.Double,
                new ColumnOptions(encodings: ImmutableArray.Create(EncodingKind.Plain)))));
        var writer = schema.CreateWriter(stream, new ParquetWriterOptions { Compression = CompressionKind.None });
        var c0 = writer.CreateSerializedColumn<long>(schema.Columns[0]);
        var c1 = writer.CreateSerializedColumn<double>(schema.Columns[1]);
        c0.Serialize([0L, 1L, -1L, long.MaxValue, long.MinValue, 1_000_000_000L]);
        c1.Serialize([0.0, 1.0, -1.0, double.MaxValue, double.MinValue, 3.14159]);
        var rg = writer.StartRowGroup();
        rg.Write(c0);
        rg.Write(c1);
        writer.CloseFile();
    }

    static void WriteInt32RleDict(Stream stream)
    {
        var schema = new ParquetSchema(ImmutableArray.Create(
            new Column("c0", ParquetPhysicalType.Int32,
                new ColumnOptions(encodings: ImmutableArray.Create(EncodingKind.RleDictionary)))));
        var writer = schema.CreateWriter(stream, new ParquetWriterOptions { Compression = CompressionKind.None });
        var col = writer.CreateSerializedColumn<int>(schema.Columns[0]);
        col.Serialize([1, 2, 1, 1, 3, 2, 1, 3, 2, 2, 1, 3]);
        var rg = writer.StartRowGroup();
        rg.Write(col);
        writer.CloseFile();
    }

    static void WriteBinDeltaLength(Stream stream)
    {
        var schema = new ParquetSchema(ImmutableArray.Create(
            new Column("c0", ParquetPhysicalType.ByteArray,
                new ColumnOptions(encodings: ImmutableArray.Create(EncodingKind.DeltaLengthByteArray)))));
        var writer = schema.CreateWriter(stream, new ParquetWriterOptions { Compression = CompressionKind.None });
        var col = writer.CreateSerializedColumn<byte[]>(schema.Columns[0]);
        col.Serialize([[], [0x01], [0x01, 0x02], [0x01, 0x02, 0x03], [0xAA, 0xBB, 0xCC, 0xDD]]);
        var rg = writer.StartRowGroup();
        rg.Write(col);
        writer.CloseFile();
    }

    static void WriteAllTypes(Stream stream)
    {
        var schema = new ParquetSchema(ImmutableArray.Create(
            new Column("c0", ParquetPhysicalType.Boolean,
                new ColumnOptions(encodings: ImmutableArray.Create(EncodingKind.Plain))),
            new Column("c1", ParquetPhysicalType.Int32,
                new ColumnOptions(encodings: ImmutableArray.Create(EncodingKind.Plain))),
            new Column("c2", ParquetPhysicalType.Int64,
                new ColumnOptions(encodings: ImmutableArray.Create(EncodingKind.Plain))),
            new Column("c3", ParquetPhysicalType.Double,
                new ColumnOptions(encodings: ImmutableArray.Create(EncodingKind.Plain))),
            new Column("c4", ParquetPhysicalType.ByteArray,
                new ColumnOptions(encodings: ImmutableArray.Create(EncodingKind.Plain)))));
        var writer = schema.CreateWriter(stream, new ParquetWriterOptions { Compression = CompressionKind.None });
        var c0 = writer.CreateSerializedColumn<bool>(schema.Columns[0]);
        var c1 = writer.CreateSerializedColumn<int>(schema.Columns[1]);
        var c2 = writer.CreateSerializedColumn<long>(schema.Columns[2]);
        var c3 = writer.CreateSerializedColumn<double>(schema.Columns[3]);
        var c4 = writer.CreateSerializedColumn<byte[]>(schema.Columns[4]);
        c0.Serialize([true, false, true, false]);
        c1.Serialize([10, 20, 30, 40]);
        c2.Serialize([100L, 200L, 300L, 400L]);
        c3.Serialize([1.1, 2.2, 3.3, 4.4]);
        c4.Serialize([[0x01, 0x02], [0x03], [0x04, 0x05, 0x06], []]);
        var rg = writer.StartRowGroup();
        rg.Write(c0);
        rg.Write(c1);
        rg.Write(c2);
        rg.Write(c3);
        rg.Write(c4);
        writer.CloseFile();
    }

    static void WriteBinDeltaByte(Stream stream)
    {
        var schema = new ParquetSchema(ImmutableArray.Create(
            new Column("c0", ParquetPhysicalType.ByteArray,
                new ColumnOptions(encodings: ImmutableArray.Create(EncodingKind.DeltaByteArray)))));
        var writer = schema.CreateWriter(stream, new ParquetWriterOptions { Compression = CompressionKind.None });
        var col = writer.CreateSerializedColumn<byte[]>(schema.Columns[0]);
        col.Serialize([
            [0x70, 0x61, 0x72],
            [0x70, 0x61, 0x72, 0x71],
            [0x70, 0x61, 0x72, 0x71, 0x75],
            [0x70, 0x61, 0x72, 0x71, 0x75, 0x65],
        ]);
        var rg = writer.StartRowGroup();
        rg.Write(col);
        writer.CloseFile();
    }
}
