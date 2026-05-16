using System.Collections.Immutable;
using Plank.Reading;
using Plank.Schema;
using Plank.Writing;
using PlankColumn = Plank.Schema.Column;

namespace Plank.Tests.Reading;

internal sealed class ParquetReaderTests
{
    [Test]
    public async Task ParsesFooterMetadataAndEnumeratesRowGroups()
    {
        var path = GetTempPath();
        var schema = new ParquetSchema([
            new PlankColumn("Value", ParquetPhysicalType.Int32)
        ]);
        try
        {
            using (var writeStream = File.Create(path))
            {
                var writer = schema.CreateWriter(writeStream);
                WriteRowGroup(writer, schema, [1, 2], [], []);
                WriteRowGroup(writer, schema, [3, 4, 5], [], []);
                writer.CloseFile();
            }

            using var readStream = File.OpenRead(path);
            using var reader = schema.CreateReader(readStream);
        var tokens = EnumerateTokens(reader);

        await Assert.That(reader.Metadata.Version).IsEqualTo(1);
        await Assert.That(reader.Metadata.FooterOffset).IsGreaterThan(0);
        await Assert.That(reader.Metadata.FooterLength).IsGreaterThan(0);
        await Assert.That(tokens.Length).IsEqualTo(2);
        await Assert.That(tokens[0].RowGroupOrdinal).IsEqualTo(0);
        await Assert.That(tokens[1].RowGroupOrdinal).IsEqualTo(1);
        await Assert.That(tokens[0].MetadataOffset).IsGreaterThan(0UL);
        await Assert.That(tokens[1].MetadataOffset).IsGreaterThan(tokens[0].MetadataOffset);
            await Assert.That(tokens[0].ColumnChunkOffset).IsGreaterThan(0UL);
            await Assert.That(tokens[1].ColumnChunkOffset).IsGreaterThan(tokens[0].ColumnChunkOffset);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task ReadsPlainColumnsFromEachRowGroup()
    {
        var path = GetTempPath();
        var schema = new ParquetSchema([
            new PlankColumn("Id", ParquetPhysicalType.Int32),
            new PlankColumn("Score", ParquetPhysicalType.Double),
            new PlankColumn("Payload", ParquetPhysicalType.ByteArray)
        ]);
        try
        {
            using (var writeStream = File.Create(path))
            {
                var writer = schema.CreateWriter(writeStream);
                WriteRowGroup(writer, schema, [1, 2], [1.5, 2.5], [Bytes(1), Bytes(2)]);
                WriteRowGroup(writer, schema, [3], [3.5], [Bytes(3)]);
                writer.CloseFile();
            }

            using var readStream = File.OpenRead(path);
            using var reader = schema.CreateReader(readStream);
            var tokens = EnumerateTokens(reader);

            using (var rowGroup = reader.OpenRowGroup(readStream, tokens[0]))
            {
                await Assert.That(ReadAllPages(rowGroup.Column<int>(schema.Columns[0]).Pages)).IsEquivalentTo([1, 2]);
                await Assert.That(ReadAllPages(rowGroup.Column<double>(schema.Columns[1]).Pages)).IsEquivalentTo([1.5, 2.5]);
                await AssertByteArraysEqual(ReadAllPages(rowGroup.Column<byte[]>(schema.Columns[2]).Pages), [Bytes(1), Bytes(2)]);
            }

            using (var rowGroup = reader.OpenRowGroup(readStream, tokens[1]))
            {
                await Assert.That(ReadAllPages(rowGroup.Column<int>(schema.Columns[0]).Pages)).IsEquivalentTo([3]);
                await Assert.That(ReadAllPages(rowGroup.Column<double>(schema.Columns[1]).Pages)).IsEquivalentTo([3.5]);
                await AssertByteArraysEqual(ReadAllPages(rowGroup.Column<byte[]>(schema.Columns[2]).Pages), [Bytes(3)]);
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task ReadsWriterEncodingsForFlatColumns()
    {
        var path = GetTempPath();
        var schema = new ParquetSchema([
            new PlankColumn("DeltaInt", ParquetPhysicalType.Int32,
                new ColumnOptions(encodings: ImmutableArray.Create(EncodingKind.DeltaBinaryPacked))),
            new PlankColumn("SplitDouble", ParquetPhysicalType.Double,
                new ColumnOptions(encodings: ImmutableArray.Create(EncodingKind.ByteStreamSplit))),
            new PlankColumn("DeltaBytes", ParquetPhysicalType.ByteArray,
                new ColumnOptions(encodings: ImmutableArray.Create(EncodingKind.DeltaLengthByteArray))),
            new PlankColumn("DictInt", ParquetPhysicalType.Int32,
                new ColumnOptions(encodings: ImmutableArray.Create(EncodingKind.RleDictionary)))
        ]);
        try
        {
            using (var writeStream = File.Create(path))
            {
                var writer = schema.CreateWriter(writeStream);
                WriteRowGroup(writer, schema, [10, 20, 30], [1.25, 2.25, 3.25],
                    [Bytes(1, 1), Bytes(1, 2), Bytes(1, 3)], [7, 7, 9]);
                writer.CloseFile();
            }

            using var readStream = File.OpenRead(path);
            using var reader = schema.CreateReader(readStream);
            var token = EnumerateTokens(reader)[0];
            using var rowGroup = reader.OpenRowGroup(readStream, token);

            await Assert.That(ReadAllPages(rowGroup.Column<int>(schema.Columns[0]).Pages)).IsEquivalentTo([10, 20, 30]);
            await Assert.That(ReadAllPages(rowGroup.Column<double>(schema.Columns[1]).Pages)).IsEquivalentTo([1.25, 2.25, 3.25]);
            await AssertByteArraysEqual(ReadAllPages(rowGroup.Column<byte[]>(schema.Columns[2]).Pages),
                [Bytes(1, 1), Bytes(1, 2), Bytes(1, 3)]);
            await Assert.That(ReadAllPages(rowGroup.Column<int>(schema.Columns[3]).Pages)).IsEquivalentTo([7, 7, 9]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task ReadsUnsignedIntegerColumns()
    {
        var path = GetTempPath();
        var schema = new ParquetSchema([
            new PlankColumn("ByteValue", ParquetPhysicalType.Int32,
                new ColumnOptions(encodings: ImmutableArray.Create(EncodingKind.Plain)),
                new LogicalType.Int(8, false)),
            new PlankColumn("UInt16Value", ParquetPhysicalType.Int32,
                new ColumnOptions(encodings: ImmutableArray.Create(EncodingKind.DeltaBinaryPacked)),
                new LogicalType.Int(16, false)),
            new PlankColumn("UInt32Value", ParquetPhysicalType.Int32,
                new ColumnOptions(encodings: ImmutableArray.Create(EncodingKind.ByteStreamSplit)),
                new LogicalType.Int(32, false)),
            new PlankColumn("UInt64Value", ParquetPhysicalType.Int64,
                new ColumnOptions(encodings: ImmutableArray.Create(EncodingKind.DeltaBinaryPacked)),
                new LogicalType.Int(64, false)),
            new PlankColumn("UInt32Dictionary", ParquetPhysicalType.Int32,
                new ColumnOptions(encodings: ImmutableArray.Create(EncodingKind.RleDictionary)),
                new LogicalType.Int(32, false))
        ]);
        var byteValues = new byte[] { 0, 1, 127, 255, 42 };
        var ushortValues = new ushort[] { 0, 255, 32768, ushort.MaxValue, 12345 };
        var uintValues = new uint[] { 0u, 1u, 2147483648u, uint.MaxValue, 17u };
        var ulongValues = new ulong[] { 0ul, 9223372036854775808ul, ulong.MaxValue, 1ul, 18446744073709551614ul };
        var dictionaryValues = new uint[] { uint.MaxValue, 1u, 0u, uint.MaxValue, 1u };
        try
        {
            using (var writeStream = File.Create(path))
            {
                var writer = schema.CreateWriter(writeStream);
                var writerRowGroup = writer.StartRowGroup();

                var byteColumn = writerRowGroup.CreateSerializedColumn<byte>(schema.Columns[0]);
                byteColumn.Serialize(byteValues);
                writerRowGroup.Write(byteColumn);

                var ushortColumn = writerRowGroup.CreateSerializedColumn<ushort>(schema.Columns[1]);
                ushortColumn.Serialize(ushortValues);
                writerRowGroup.Write(ushortColumn);

                var uintColumn = writerRowGroup.CreateSerializedColumn<uint>(schema.Columns[2]);
                uintColumn.Serialize(uintValues);
                writerRowGroup.Write(uintColumn);

                var ulongColumn = writerRowGroup.CreateSerializedColumn<ulong>(schema.Columns[3]);
                ulongColumn.Serialize(ulongValues);
                writerRowGroup.Write(ulongColumn);

                var dictionaryColumn = writerRowGroup.CreateSerializedColumn<uint>(schema.Columns[4]);
                dictionaryColumn.Serialize(dictionaryValues);
                writerRowGroup.Write(dictionaryColumn);

                writer.CloseFile();
            }

            using var readStream = File.OpenRead(path);
            using var reader = schema.CreateReader(readStream);
            var token = EnumerateTokens(reader)[0];
            using var rowGroup = reader.OpenRowGroup(readStream, token);

            await Assert.That(ReadAllPages(rowGroup.Column<byte>(schema.Columns[0]).Pages)).IsEquivalentTo(byteValues);
            await Assert.That(ReadAllPages(rowGroup.Column<ushort>(schema.Columns[1]).Pages)).IsEquivalentTo(ushortValues);
            await Assert.That(ReadAllPages(rowGroup.Column<uint>(schema.Columns[2]).Pages)).IsEquivalentTo(uintValues);
            await Assert.That(ReadAllPages(rowGroup.Column<ulong>(schema.Columns[3]).Pages)).IsEquivalentTo(ulongValues);
            await Assert.That(ReadAllPages(rowGroup.Column<uint>(schema.Columns[4]).Pages)).IsEquivalentTo(dictionaryValues);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task ReadsOptionalNullableValueColumns()
    {
        var path = GetTempPath();
        var schema = new ParquetSchema([
            new PlankColumn("IntOpt", ParquetPhysicalType.Int32,
                new ColumnOptions(ParquetRepetition.Optional)),
            new PlankColumn("LongOpt", ParquetPhysicalType.Int64,
                new ColumnOptions(ParquetRepetition.Optional)),
            new PlankColumn("DoubleOpt", ParquetPhysicalType.Double,
                new ColumnOptions(ParquetRepetition.Optional)),
            new PlankColumn("BoolOpt", ParquetPhysicalType.Boolean,
                new ColumnOptions(ParquetRepetition.Optional))
        ]);
        int?[] intValues = [1, null, 3, null, 5];
        long?[] longValues = [null, 2L, null, 4L, null];
        double?[] doubleValues = [1.5, 2.5, null, null, 5.5];
        bool?[] boolValues = [true, null, false, null, true];
        try
        {
            using (var writeStream = File.Create(path))
            {
                var writer = schema.CreateWriter(writeStream);
                var rowGroup = writer.StartRowGroup();

                var intCol = rowGroup.CreateSerializedColumn<int?>(schema.Columns[0]);
                intCol.Serialize(intValues);
                rowGroup.Write(intCol);

                var longCol = rowGroup.CreateSerializedColumn<long?>(schema.Columns[1]);
                longCol.Serialize(longValues);
                rowGroup.Write(longCol);

                var doubleCol = rowGroup.CreateSerializedColumn<double?>(schema.Columns[2]);
                doubleCol.Serialize(doubleValues);
                rowGroup.Write(doubleCol);

                var boolCol = rowGroup.CreateSerializedColumn<bool?>(schema.Columns[3]);
                boolCol.Serialize(boolValues);
                rowGroup.Write(boolCol);

                writer.CloseFile();
            }

            using var readStream = File.OpenRead(path);
            using var reader = schema.CreateReader(readStream);
            var token = EnumerateTokens(reader)[0];
            using var rowGroup2 = reader.OpenRowGroup(readStream, token);

            await Assert.That(ReadAllPages(rowGroup2.Column<int?>(schema.Columns[0]).Pages)).IsEquivalentTo(intValues);
            await Assert.That(ReadAllPages(rowGroup2.Column<long?>(schema.Columns[1]).Pages)).IsEquivalentTo(longValues);
            await Assert.That(ReadAllPages(rowGroup2.Column<double?>(schema.Columns[2]).Pages)).IsEquivalentTo(doubleValues);
            await Assert.That(ReadAllPages(rowGroup2.Column<bool?>(schema.Columns[3]).Pages)).IsEquivalentTo(boolValues);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task ReadsOptionalNullableReferenceColumns()
    {
        var path = GetTempPath();
        var schema = new ParquetSchema([
            new PlankColumn("StrOpt", ParquetPhysicalType.ByteArray,
                new ColumnOptions(ParquetRepetition.Optional), new LogicalType.String()),
            new PlankColumn("BinOpt", ParquetPhysicalType.ByteArray,
                new ColumnOptions(ParquetRepetition.Optional))
        ]);
        string?[] strValues = ["hello", null, "world", null, "!"];
        byte[]?[] binValues = [new byte[] { 1 }, null, new byte[] { 3, 4 }, null, new byte[] { 5 }];
        try
        {
            using (var writeStream = File.Create(path))
            {
                var writer = schema.CreateWriter(writeStream);
                var rowGroup = writer.StartRowGroup();

                var strCol = rowGroup.CreateSerializedColumn<string>(schema.Columns[0]);
                strCol.Serialize([.. strValues]);
                rowGroup.Write(strCol);

                var binCol = rowGroup.CreateSerializedColumn<byte[]>(schema.Columns[1]);
                binCol.Serialize([.. binValues]);
                rowGroup.Write(binCol);

                writer.CloseFile();
            }

            using var readStream = File.OpenRead(path);
            using var reader = schema.CreateReader(readStream);
            var token = EnumerateTokens(reader)[0];
            using var rowGroup2 = reader.OpenRowGroup(readStream, token);

            var actualStr = ReadAllPages(rowGroup2.Column<string>(schema.Columns[0]).Pages);
            await Assert.That(actualStr).IsEquivalentTo(strValues);

            var actualBin = ReadAllPages(rowGroup2.Column<byte[]>(schema.Columns[1]).Pages);
            await Assert.That(actualBin.Length).IsEqualTo(binValues.Length);
            for (var i = 0; i < binValues.Length; i++)
            {
                if (binValues[i] is null)
                    await Assert.That(actualBin[i]).IsNull();
                else
                    await Assert.That(actualBin[i]).IsEquivalentTo(binValues[i]!);
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task ReadsOptionalColumnWithAllNulls()
    {
        var path = GetTempPath();
        var schema = new ParquetSchema([
            new PlankColumn("IntOpt", ParquetPhysicalType.Int32,
                new ColumnOptions(ParquetRepetition.Optional))
        ]);
        int?[] values = [null, null, null];
        try
        {
            using (var writeStream = File.Create(path))
            {
                var writer = schema.CreateWriter(writeStream);
                var rowGroup = writer.StartRowGroup();
                var col = rowGroup.CreateSerializedColumn<int?>(schema.Columns[0]);
                col.Serialize(values);
                rowGroup.Write(col);
                writer.CloseFile();
            }

            using var readStream = File.OpenRead(path);
            using var reader = schema.CreateReader(readStream);
            var token = EnumerateTokens(reader)[0];
            using var rowGroup2 = reader.OpenRowGroup(readStream, token);

            await Assert.That(ReadAllPages(rowGroup2.Column<int?>(schema.Columns[0]).Pages)).IsEquivalentTo(values);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task ReadsOptionalColumnWithDictionaryEncoding()
    {
        var path = GetTempPath();
        var schema = new ParquetSchema([
            new PlankColumn("IntOpt", ParquetPhysicalType.Int32,
                new ColumnOptions(ParquetRepetition.Optional,
                    encodings: ImmutableArray.Create(EncodingKind.RleDictionary)))
        ]);
        int?[] values = [10, null, 10, 20, null, 20, 10];
        try
        {
            using (var writeStream = File.Create(path))
            {
                var writer = schema.CreateWriter(writeStream);
                var rowGroup = writer.StartRowGroup();
                var col = rowGroup.CreateSerializedColumn<int?>(schema.Columns[0]);
                col.Serialize(values);
                rowGroup.Write(col);
                writer.CloseFile();
            }

            using var readStream = File.OpenRead(path);
            using var reader = schema.CreateReader(readStream);
            var token = EnumerateTokens(reader)[0];
            using var rowGroup2 = reader.OpenRowGroup(readStream, token);

            await Assert.That(ReadAllPages(rowGroup2.Column<int?>(schema.Columns[0]).Pages)).IsEquivalentTo(values);
        }
        finally
        {
            File.Delete(path);
        }
    }

    static void WriteRowGroup(ParquetWriter writer, ParquetSchema schema, int[] ints, double[] doubles, byte[][] bytes,
        int[]? dictionaryInts = null)
    {
        var rowGroup = writer.StartRowGroup();

        var intColumn = rowGroup.CreateSerializedColumn<int>(schema.Columns[0]);
        intColumn.Serialize(ints);
        rowGroup.Write(intColumn);

        if (schema.Columns.Length == 1)
            return;

        var doubleColumn = rowGroup.CreateSerializedColumn<double>(schema.Columns[1]);
        doubleColumn.Serialize(doubles);
        rowGroup.Write(doubleColumn);

        var byteColumn = rowGroup.CreateSerializedColumn<byte[]>(schema.Columns[2]);
        byteColumn.Serialize(bytes);
        rowGroup.Write(byteColumn);

        if (dictionaryInts is null)
            return;

        var dictionaryColumn = rowGroup.CreateSerializedColumn<int>(schema.Columns[3]);
        dictionaryColumn.Serialize(dictionaryInts);
        rowGroup.Write(dictionaryColumn);
    }

    static async Task AssertByteArraysEqual(byte[][] actual, byte[][] expected)
    {
        await Assert.That(actual.Length).IsEqualTo(expected.Length);
        for (var i = 0; i < expected.Length; i++)
            await Assert.That(actual[i]).IsEquivalentTo(expected[i]);
    }

    static RowGroupToken[] EnumerateTokens(ParquetReader reader)
    {
        var tokens = new List<RowGroupToken>();
        foreach (var token in reader.EnumerateRowGroups())
            tokens.Add(token);
        return tokens.ToArray();
    }

    static byte[] Bytes(params byte[] values)
        => values;

    static T[] ReadAllPages<T>(ColumnPageEnumerable<T> pages)
    {
        var values = new List<T>();
        foreach (var page in pages)
            foreach (var value in page.Values.Span)
                values.Add(value);
        return values.ToArray();
    }

    static string GetTempPath()
        => Path.Combine(Path.GetTempPath(), $"plank-reader-tests-{Guid.NewGuid():N}.parquet");
}
