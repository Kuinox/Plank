using System.Collections.Immutable;
using Plank.Reading;
using Plank.Reading.Logical;
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
        await Assert.That(reader.Metadata.FooterOffset).IsGreaterThan(0UL);
        await Assert.That(reader.Metadata.FooterLength).IsGreaterThan(0U);
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
    public async Task DiscoversSchemaWhenOpeningWithoutRequestedSchema()
    {
        using var stream = CreateTwoColumnFile();

        using var reader = ParquetReader.Open(stream);

        await Assert.That(reader.Schema.Columns.Length).IsEqualTo(2);
        await Assert.That(reader.Schema.Columns[0].Name).IsEqualTo("Value");
        await Assert.That(reader.Schema.Columns[0].PhysicalType).IsEqualTo(ParquetPhysicalType.Int32);
        await Assert.That(reader.Schema.Columns[1].Name).IsEqualTo("Other");
        await Assert.That(reader.Schema.Columns[1].PhysicalType).IsEqualTo(ParquetPhysicalType.Int64);
    }

    [Test]
    public async Task ReadsDiscoveredColumns()
    {
        using var stream = CreateTwoColumnFile();

        using var reader = ParquetReader.Open(stream);
        var token = EnumerateTokens(reader)[0];
        using var rowGroup = reader.OpenRowGroup(token);

        await Assert.That(ReadAllPages(rowGroup.Column<int>(reader.Schema.Columns[0]).Pages)).IsEquivalentTo([1, 2, 3]);
        await Assert.That(ReadAllPages(rowGroup.Column<long>(reader.Schema.Columns[1]).Pages)).IsEquivalentTo([10L, 20L, 30L]);
    }

    [Test]
    public async Task ResetUpdatesDiscoveredSchema()
    {
        using var first = CreateInt32File("Value");
        using var second = CreateInt32File("Other");
        using var reader = ParquetReader.Open(first);

        await Assert.That(reader.Schema.Columns[0].Name).IsEqualTo("Value");

        reader.Reset(second);
        await Assert.That(reader.Schema.Columns[0].Name).IsEqualTo("Other");
    }

    [Test]
    public async Task OldTokenIsInvalidAfterReset()
    {
        using var first = CreateInt32File("Value");
        using var second = CreateInt32File("Other");
        using var reader = ParquetReader.Open(first);
        var oldToken = EnumerateTokens(reader)[0];

        reader.Reset(second);

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await Task.Run(() => reader.OpenRowGroup(oldToken)).ConfigureAwait(false));
    }

    [Test]
    public async Task ThrowsWhenRequestedColumnNameDoesNotMatchFileSchema()
    {
        using var stream = CreateInt32File("Actual");
        var requested = new ParquetSchema([
            new PlankColumn("Requested", ParquetPhysicalType.Int32)
        ]);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await Task.Run(() => requested.CreateReader(stream)).ConfigureAwait(false));
    }

    [Test]
    public async Task ThrowsWhenRequestedPhysicalTypeDoesNotMatchFileSchema()
    {
        using var stream = CreateInt32File("Value");
        var requested = new ParquetSchema([
            new PlankColumn("Value", ParquetPhysicalType.Int64)
        ]);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await Task.Run(() => requested.CreateReader(stream)).ConfigureAwait(false));
    }

    [Test]
    public async Task ReadsRequestedProjectionWhenFileSchemaHasExtraColumns()
    {
        using var stream = CreateTwoColumnFile();
        var requested = new ParquetSchema([
            new PlankColumn("Value", ParquetPhysicalType.Int32)
        ]);

        using var reader = requested.CreateReader(stream);
        var token = EnumerateTokens(reader)[0];
        using var rowGroup = reader.OpenRowGroup(token);

        await Assert.That(ReadAllPages(rowGroup.Column<int>(requested.Columns[0]).Pages)).IsEquivalentTo([1, 2, 3]);
        await Assert.That(reader.Schema.Columns.Length).IsEqualTo(1);
        await Assert.That(reader.Metadata.Schema.Columns.Length).IsEqualTo(2);
    }

    [Test]
    public async Task ReadsRequestedProjectionWhenFileSchemaOrderChanged()
    {
        var fileSchema = new ParquetSchema([
            new PlankColumn("Other", ParquetPhysicalType.Int64),
            new PlankColumn("Value", ParquetPhysicalType.Int32)
        ]);
        using var stream = CreateFile(fileSchema, rowGroup =>
        {
            var other = rowGroup.CreateSerializedColumn<long>(fileSchema.Columns[0]);
            other.Serialize([10L, 20L, 30L]);
            rowGroup.Write(other);

            var value = rowGroup.CreateSerializedColumn<int>(fileSchema.Columns[1]);
            value.Serialize([1, 2, 3]);
            rowGroup.Write(value);
        });
        var requested = new ParquetSchema([
            new PlankColumn("Value", ParquetPhysicalType.Int32),
            new PlankColumn("Other", ParquetPhysicalType.Int64)
        ]);

        using var reader = requested.CreateReader(stream);
        var token = EnumerateTokens(reader)[0];
        using var rowGroupReader = reader.OpenRowGroup(token);

        await Assert.That(ReadAllPages(rowGroupReader.Column<int>(requested.Columns[0]).Pages)).IsEquivalentTo([1, 2, 3]);
        await Assert.That(ReadAllPages(rowGroupReader.Column<long>(requested.Columns[1]).Pages))
            .IsEquivalentTo([10L, 20L, 30L]);
    }

    [Test]
    public async Task MissingRequestedColumnThrows()
    {
        using var stream = CreateInt32File("Value");
        var requested = new ParquetSchema([
            new PlankColumn("Value", ParquetPhysicalType.Int32),
            new PlankColumn("Added", ParquetPhysicalType.Int32)
        ]);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await Task.Run(() => requested.CreateReader(stream)).ConfigureAwait(false));
    }

    [Test]
    public async Task AllowsRequiredFileColumnForOptionalRequestedColumn()
    {
        using var stream = CreateInt32File("Value");
        var requested = new ParquetSchema([
            new PlankColumn("Value", ParquetPhysicalType.Int32, new ColumnOptions(ParquetRepetition.Optional))
        ]);

        using var reader = requested.CreateReader(stream);
        var token = EnumerateTokens(reader)[0];
        using var rowGroup = reader.OpenRowGroup(token);

        await Assert.That(ReadAllPages(rowGroup.Column<int?>(requested.Columns[0]).Pages))
            .IsEquivalentTo(new int?[] { 1, 2, 3 });
    }

    [Test]
    public async Task ThrowsWhenOptionalFileColumnIsRequestedAsRequired()
    {
        using var stream = CreateOptionalInt32File("Value");
        var requested = new ParquetSchema([
            new PlankColumn("Value", ParquetPhysicalType.Int32)
        ]);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await Task.Run(() => requested.CreateReader(stream)).ConfigureAwait(false));
    }

    [Test]
    public async Task AllowsRequestedSchemaMismatchWhenStrictModeIsDisabled()
    {
        using var stream = CreateInt32File("Actual");
        var requested = new ParquetSchema([
            new PlankColumn("Requested", ParquetPhysicalType.Int64)
        ]);

        using var reader = requested.CreateReader(stream, new ParquetReaderOptions
        {
            Strict = false
        });

        await Assert.That(reader.Metadata.FooterLength).IsGreaterThan(0U);
    }

    [Test]
    public async Task ResetThrowsWhenNewFileSchemaDoesNotMatchRequestedSchema()
    {
        var requested = new ParquetSchema([
            new PlankColumn("Value", ParquetPhysicalType.Int32)
        ]);
        using var matching = CreateInt32File("Value");
        using var mismatched = CreateInt32File("Other");
        using var reader = requested.CreateReader(matching);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await Task.Run(() => reader.Reset(mismatched)).ConfigureAwait(false));
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
            using var reusable = reader.CreateReusableRowGroupReader();
            var rowGroupIndex = 0;
            foreach (var token in reader.EnumerateRowGroups())
            {
                reader.OpenRowGroup(token, reusable);
                if (rowGroupIndex == 0)
                {
                    await Assert.That(ReadAllPages(reusable.Column<int>(schema.Columns[0]).Pages)).IsEquivalentTo([1, 2]);
                    await Assert.That(ReadAllPages(reusable.Column<double>(schema.Columns[1]).Pages)).IsEquivalentTo([1.5, 2.5]);
                    await AssertByteArraysEqual(ReadAllPages(reusable.Column<byte[]>(schema.Columns[2]).Pages), [Bytes(1), Bytes(2)]);
                }
                else
                {
                    await Assert.That(ReadAllPages(reusable.Column<int>(schema.Columns[0]).Pages)).IsEquivalentTo([3]);
                    await Assert.That(ReadAllPages(reusable.Column<double>(schema.Columns[1]).Pages)).IsEquivalentTo([3.5]);
                    await AssertByteArraysEqual(ReadAllPages(reusable.Column<byte[]>(schema.Columns[2]).Pages), [Bytes(3)]);
                }
                rowGroupIndex++;
            }

            await Assert.That(rowGroupIndex).IsEqualTo(2);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task ReadsCompressedPlainColumnWhenUncompressedPageIsLargerThanRemainingCompressedChunk()
    {
        var path = GetTempPath();
        var schema = new ParquetSchema([
            new PlankColumn("Value", ParquetPhysicalType.Int32,
                new ColumnOptions(encodings: ImmutableArray.Create(EncodingKind.Plain)))
        ]);
        var values = CreateValues(4096);
        try
        {
            using (var writeStream = File.Create(path))
            {
                var writer = schema.CreateWriter(writeStream, new ParquetWriterOptions
                {
                    Compression = CompressionKind.Gzip
                });
                var serialized = writer.CreateSerializedColumn<int>(schema.Columns[0]);
                serialized.Serialize(values);
                writer.StartRowGroup().Write(serialized);
                writer.CloseFile();
            }

            using var readStream = File.OpenRead(path);
            using var reader = schema.CreateReader(readStream);
            var token = EnumerateTokens(reader)[0];
            using var rowGroup = reader.OpenRowGroup(token);

            await Assert.That(ReadAllPages(rowGroup.Column<int>(schema.Columns[0]).Pages)).IsEquivalentTo(values);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task ReadsParquetSharpDataPageV1Columns()
    {
        var path = GetTempPath();
        var schema = new ParquetSchema([
            new PlankColumn("Required", ParquetPhysicalType.Int32),
            new PlankColumn("Optional", ParquetPhysicalType.Int32, new ColumnOptions(ParquetRepetition.Optional))
        ]);
        int?[] optionalValues = [10, null, 30, null, 50];
        try
        {
            WriteParquetSharpDataPageV1File(path, [1, 2, 3, 4, 5], optionalValues);

            using var readStream = File.OpenRead(path);
            using var reader = schema.CreateReader(readStream);
            var token = EnumerateTokens(reader)[0];
            using var rowGroup = reader.OpenRowGroup(token);

            await Assert.That(ReadAllPages(rowGroup.Column<int>(schema.Columns[0]).Pages)).IsEquivalentTo([1, 2, 3, 4, 5]);
            await Assert.That(ReadAllPages(rowGroup.Column<int?>(schema.Columns[1]).Pages)).IsEquivalentTo(optionalValues);
        }
        finally
        {
            File.Delete(path);
        }
    }

    static void WriteParquetSharpDataPageV1File(string path, int[] requiredValues, int?[] optionalValues)
    {
        using var properties = new ParquetSharp.WriterPropertiesBuilder()
            .Compression(ParquetSharp.Compression.Uncompressed)
            .Build();
        using var stream = File.Create(path);
        using var writer = new ParquetSharp.ParquetFileWriter(stream,
            [new ParquetSharp.Column<int>("Required"), new ParquetSharp.Column<int?>("Optional")],
            null, properties, null, true);
        using var rowGroup = writer.AppendRowGroup();
        using (var required = rowGroup.NextColumn().LogicalWriter<int>())
            required.WriteBatch(requiredValues);
        using (var optional = rowGroup.NextColumn().LogicalWriter<int?>())
            optional.WriteBatch(optionalValues);
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
            using var rowGroup = reader.OpenRowGroup(token);

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

    static int[] CreateValues(int count)
    {
        var values = new int[count];
        for (var i = 0; i < values.Length; i++)
            values[i] = i * 3;
        return values;
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
            using var rowGroup = reader.OpenRowGroup(token);

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
            using var rowGroup2 = reader.OpenRowGroup(token);

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
            using var rowGroup2 = reader.OpenRowGroup(token);

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
            using var rowGroup2 = reader.OpenRowGroup(token);

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
            using var rowGroup2 = reader.OpenRowGroup(token);

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

    static MemoryStream CreateInt32File(string columnName)
    {
        var schema = new ParquetSchema([
            new PlankColumn(columnName, ParquetPhysicalType.Int32)
        ]);
        return CreateFile(schema, rowGroup =>
        {
            var serialized = rowGroup.CreateSerializedColumn<int>(schema.Columns[0]);
            serialized.Serialize([1, 2, 3]);
            rowGroup.Write(serialized);
        });
    }

    static MemoryStream CreateOptionalInt32File(string columnName)
    {
        var schema = new ParquetSchema([
            new PlankColumn(columnName, ParquetPhysicalType.Int32, new ColumnOptions(ParquetRepetition.Optional))
        ]);
        return CreateFile(schema, rowGroup =>
        {
            var serialized = rowGroup.CreateSerializedColumn<int?>(schema.Columns[0]);
            serialized.Serialize([1, null, 3]);
            rowGroup.Write(serialized);
        });
    }

    static MemoryStream CreateTwoColumnFile()
    {
        var schema = new ParquetSchema([
            new PlankColumn("Value", ParquetPhysicalType.Int32),
            new PlankColumn("Other", ParquetPhysicalType.Int64)
        ]);
        return CreateFile(schema, rowGroup =>
        {
            var value = rowGroup.CreateSerializedColumn<int>(schema.Columns[0]);
            value.Serialize([1, 2, 3]);
            rowGroup.Write(value);

            var other = rowGroup.CreateSerializedColumn<long>(schema.Columns[1]);
            other.Serialize([10L, 20L, 30L]);
            rowGroup.Write(other);
        });
    }

    static MemoryStream CreateFile(ParquetSchema schema, Action<RowGroupWriter> writeRowGroup)
    {
        var stream = new MemoryStream();
        var writer = schema.CreateWriter(stream);
        writeRowGroup(writer.StartRowGroup());
        writer.CloseFile();
        return new MemoryStream(stream.ToArray());
    }

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
