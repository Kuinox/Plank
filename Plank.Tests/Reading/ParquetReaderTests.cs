using System.Collections.Immutable;
using Plank.Reading;
using Plank.Reading.Logical;
using Plank.Schema;
using Plank.Writing;

namespace Plank.Tests.Reading;

internal sealed class ParquetReaderTests
{
    [Test]
    public async Task ParsesFooterMetadataAndEnumeratesRowGroups()
    {
        var path = GetTempPath();
        var schema = new ParquetSchema([
            Plank.Schema.ColumnDefinition.Leaf("Value", ParquetPhysicalType.Int32)
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
        var rowGroups = reader.RowGroups;

        await Assert.That(reader.Metadata.Version).IsEqualTo(1);
        await Assert.That(reader.Metadata.FooterOffset).IsGreaterThan(0UL);
        await Assert.That(reader.Metadata.FooterLength).IsGreaterThan(0U);
        await Assert.That(rowGroups.Count).IsEqualTo(2);
        await Assert.That(rowGroups[0].Index).IsEqualTo(0);
        await Assert.That(rowGroups[1].Index).IsEqualTo(1);
        await Assert.That(rowGroups[0].MetadataOffset).IsGreaterThan(0UL);
        await Assert.That(rowGroups[1].MetadataOffset).IsGreaterThan(rowGroups[0].MetadataOffset);
            await Assert.That(rowGroups[0].ColumnChunkOffset).IsGreaterThan(0UL);
            await Assert.That(rowGroups[1].ColumnChunkOffset).IsGreaterThan(rowGroups[0].ColumnChunkOffset);
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

        using var reader = new ParquetReader();
        reader.Reset(stream);

        await Assert.That(reader.Schema.LeafColumns.Length).IsEqualTo(2);
        await Assert.That(reader.Schema.LeafColumns[0].Path).IsEqualTo("Value");
        await Assert.That(reader.Schema.LeafColumns[0].PhysicalType).IsEqualTo(ParquetPhysicalType.Int32);
        await Assert.That(reader.Schema.LeafColumns[1].Path).IsEqualTo("Other");
        await Assert.That(reader.Schema.LeafColumns[1].PhysicalType).IsEqualTo(ParquetPhysicalType.Int64);
    }

    [Test]
    public async Task ReadsDiscoveredColumns()
    {
        using var stream = CreateTwoColumnFile();

        using var reader = new ParquetReader();
        reader.Reset(stream);
        var rowGroup = reader.RowGroups[0];

        await Assert.That(ReadAllBuffers(rowGroup.Column<int>(reader.Schema.LeafColumns[0]))).IsEquivalentTo([1, 2, 3]);
        await Assert.That(ReadAllBuffers(rowGroup.Column<long>(reader.Schema.LeafColumns[1]))).IsEquivalentTo([10L, 20L, 30L]);
    }

    [Test]
    public async Task ColumnByDefinitionWithWrongClrTypeThrowsImmediately()
    {
        using var stream = CreateTwoColumnFile();
        using var reader = new ParquetReader();
        reader.Reset(stream);
        var rowGroup = reader.RowGroups[0];

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await Task.Run(() => rowGroup.Column<int>(reader.Schema.LeafColumns[1])).ConfigureAwait(false));
    }

    [Test]
    public async Task ColumnByOrdinalWithWrongClrTypeThrowsImmediately()
    {
        using var stream = CreateTwoColumnFile();
        using var reader = new ParquetReader();
        reader.Reset(stream);
        var rowGroup = reader.RowGroups[0];

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await Task.Run(() => rowGroup.Column<int>(1)).ConfigureAwait(false));
    }

    [Test]
    public async Task ResetUpdatesDiscoveredSchema()
    {
        using var first = CreateInt32File("Value");
        using var second = CreateInt32File("Other");
        using var reader = new ParquetReader();
        reader.Reset(first);

        await Assert.That(reader.Schema.LeafColumns[0].Path).IsEqualTo("Value");

        reader.Reset(second);
        await Assert.That(reader.Schema.LeafColumns[0].Path).IsEqualTo("Other");
    }

    [Test]
    public async Task OldRowGroupIsInvalidAfterReset()
    {
        using var first = CreateInt32File("Value");
        using var second = CreateInt32File("Other");
        using var reader = new ParquetReader();
        reader.Reset(first);
        var oldRowGroup = reader.RowGroups[0];

        reader.Reset(second);

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await Task.Run(() => oldRowGroup.Column<int>(0)).ConfigureAwait(false));
    }

    [Test]
    public async Task ThrowsWhenRequestedColumnNameDoesNotMatchFileSchema()
    {
        using var stream = CreateInt32File("Actual");
        var requested = new ParquetSchema([
            Plank.Schema.ColumnDefinition.Leaf("Requested", ParquetPhysicalType.Int32)
        ]);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await Task.Run(() => requested.CreateReader(stream)).ConfigureAwait(false));
    }

    [Test]
    public async Task ThrowsWhenRequestedPhysicalTypeDoesNotMatchFileSchema()
    {
        using var stream = CreateInt32File("Value");
        var requested = new ParquetSchema([
            Plank.Schema.ColumnDefinition.Leaf("Value", ParquetPhysicalType.Int64)
        ]);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await Task.Run(() => requested.CreateReader(stream)).ConfigureAwait(false));
    }

    [Test]
    public async Task ReadsRequestedProjectionWhenFileSchemaHasExtraColumns()
    {
        using var stream = CreateTwoColumnFile();
        var requested = new ParquetSchema([
            Plank.Schema.ColumnDefinition.Leaf("Value", ParquetPhysicalType.Int32)
        ]);

        using var reader = requested.CreateReader(stream);
        var rowGroup = reader.RowGroups[0];

        await Assert.That(ReadAllBuffers(rowGroup.Column<int>(requested.LeafColumns[0]))).IsEquivalentTo([1, 2, 3]);
        await Assert.That(reader.Schema.LeafColumns.Length).IsEqualTo(1);
        await Assert.That(reader.Metadata.Schema.LeafColumns.Length).IsEqualTo(2);
    }

    [Test]
    public async Task ReadsRequestedProjectionWhenFileSchemaOrderChanged()
    {
        var fileSchema = new ParquetSchema([
            Plank.Schema.ColumnDefinition.Leaf("Other", ParquetPhysicalType.Int64),
            Plank.Schema.ColumnDefinition.Leaf("Value", ParquetPhysicalType.Int32)
        ]);
        using var stream = CreateFile(fileSchema, rowGroup =>
        {
            var other = rowGroup.CreateSerializedColumn<long>(fileSchema.LeafColumns[0]);
            other.Serialize([10L, 20L, 30L]);
            rowGroup.Write(other);

            var value = rowGroup.CreateSerializedColumn<int>(fileSchema.LeafColumns[1]);
            value.Serialize([1, 2, 3]);
            rowGroup.Write(value);
        });
        var requested = new ParquetSchema([
            Plank.Schema.ColumnDefinition.Leaf("Value", ParquetPhysicalType.Int32),
            Plank.Schema.ColumnDefinition.Leaf("Other", ParquetPhysicalType.Int64)
        ]);

        using var reader = requested.CreateReader(stream);
        var rowGroupReader = reader.RowGroups[0];

        await Assert.That(ReadAllBuffers(rowGroupReader.Column<int>(requested.LeafColumns[0]))).IsEquivalentTo([1, 2, 3]);
        await Assert.That(ReadAllBuffers(rowGroupReader.Column<long>(requested.LeafColumns[1])))
            .IsEquivalentTo([10L, 20L, 30L]);
    }

    [Test]
    public async Task MissingRequestedColumnThrows()
    {
        using var stream = CreateInt32File("Value");
        var requested = new ParquetSchema([
            Plank.Schema.ColumnDefinition.Leaf("Value", ParquetPhysicalType.Int32),
            Plank.Schema.ColumnDefinition.Leaf("Added", ParquetPhysicalType.Int32)
        ]);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await Task.Run(() => requested.CreateReader(stream)).ConfigureAwait(false));
    }

    [Test]
    public async Task AllowsRequiredFileColumnForOptionalRequestedColumn()
    {
        using var stream = CreateInt32File("Value");
        var requested = new ParquetSchema([
            Plank.Schema.ColumnDefinition.Leaf("Value", ParquetPhysicalType.Int32, new ColumnOptions(ParquetRepetition.Optional))
        ]);

        using var reader = requested.CreateReader(stream);
        var rowGroup = reader.RowGroups[0];

        await Assert.That(ReadAllBuffers(rowGroup.Column<int?>(requested.LeafColumns[0])))
            .IsEquivalentTo(new int?[] { 1, 2, 3 });
    }

    [Test]
    public async Task ThrowsWhenOptionalFileColumnIsRequestedAsRequired()
    {
        using var stream = CreateOptionalInt32File("Value");
        var requested = new ParquetSchema([
            Plank.Schema.ColumnDefinition.Leaf("Value", ParquetPhysicalType.Int32)
        ]);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await Task.Run(() => requested.CreateReader(stream)).ConfigureAwait(false));
    }

    [Test]
    public async Task AllowsRequestedSchemaMismatchWhenStrictModeIsDisabled()
    {
        using var stream = CreateInt32File("Actual");
        var requested = new ParquetSchema([
            Plank.Schema.ColumnDefinition.Leaf("Requested", ParquetPhysicalType.Int64)
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
            Plank.Schema.ColumnDefinition.Leaf("Value", ParquetPhysicalType.Int32)
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
            Plank.Schema.ColumnDefinition.Leaf("Id", ParquetPhysicalType.Int32),
            Plank.Schema.ColumnDefinition.Leaf("Score", ParquetPhysicalType.Double),
            Plank.Schema.ColumnDefinition.Leaf("Payload", ParquetPhysicalType.ByteArray)
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
            var rowGroupIndex = 0;
            foreach (var rowGroup in reader.RowGroups)
            {
                if (rowGroupIndex == 0)
                {
                    await Assert.That(ReadAllBuffers(rowGroup.Column<int>(schema.LeafColumns[0]))).IsEquivalentTo([1, 2]);
                    await Assert.That(ReadAllBuffers(rowGroup.Column<double>(schema.LeafColumns[1]))).IsEquivalentTo([1.5, 2.5]);
                    await AssertByteArraysEqual(ReadAllBinaryBuffers(rowGroup.Column<byte>(schema.LeafColumns[2])),
                        [Bytes(1), Bytes(2)]);
                }
                else
                {
                    await Assert.That(ReadAllBuffers(rowGroup.Column<int>(schema.LeafColumns[0]))).IsEquivalentTo([3]);
                    await Assert.That(ReadAllBuffers(rowGroup.Column<double>(schema.LeafColumns[1]))).IsEquivalentTo([3.5]);
                    await AssertByteArraysEqual(ReadAllBinaryBuffers(rowGroup.Column<byte>(schema.LeafColumns[2])),
                        [Bytes(3)]);
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
            Plank.Schema.ColumnDefinition.Leaf("Value", ParquetPhysicalType.Int32,
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
                var serialized = writer.CreateSerializedColumn<int>(schema.LeafColumns[0]);
                serialized.Serialize(values);
                writer.StartRowGroup().Write(serialized);
                writer.CloseFile();
            }

            using var readStream = File.OpenRead(path);
            using var reader = schema.CreateReader(readStream);
            var rowGroup = reader.RowGroups[0];

            await Assert.That(ReadAllBuffers(rowGroup.Column<int>(schema.LeafColumns[0]))).IsEquivalentTo(values);
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
            Plank.Schema.ColumnDefinition.Leaf("Required", ParquetPhysicalType.Int32),
            Plank.Schema.ColumnDefinition.Leaf("Optional", ParquetPhysicalType.Int32, new ColumnOptions(ParquetRepetition.Optional))
        ]);
        int?[] optionalValues = [10, null, 30, null, 50];
        try
        {
            WriteParquetSharpDataPageV1File(path, [1, 2, 3, 4, 5], optionalValues);

            using var readStream = File.OpenRead(path);
            using var reader = schema.CreateReader(readStream);
            var rowGroup = reader.RowGroups[0];

            await Assert.That(ReadAllBuffers(rowGroup.Column<int>(schema.LeafColumns[0]))).IsEquivalentTo([1, 2, 3, 4, 5]);
            await Assert.That(ReadAllBuffers(rowGroup.Column<int?>(schema.LeafColumns[1]))).IsEquivalentTo(optionalValues);
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
            Plank.Schema.ColumnDefinition.Leaf("DeltaInt", ParquetPhysicalType.Int32,
                new ColumnOptions(encodings: ImmutableArray.Create(EncodingKind.DeltaBinaryPacked))),
            Plank.Schema.ColumnDefinition.Leaf("SplitDouble", ParquetPhysicalType.Double,
                new ColumnOptions(encodings: ImmutableArray.Create(EncodingKind.ByteStreamSplit))),
            Plank.Schema.ColumnDefinition.Leaf("DeltaBytes", ParquetPhysicalType.ByteArray,
                new ColumnOptions(encodings: ImmutableArray.Create(EncodingKind.DeltaLengthByteArray))),
            Plank.Schema.ColumnDefinition.Leaf("DictInt", ParquetPhysicalType.Int32,
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
            var rowGroup = reader.RowGroups[0];

            await Assert.That(ReadAllBuffers(rowGroup.Column<int>(schema.LeafColumns[0]))).IsEquivalentTo([10, 20, 30]);
            await Assert.That(ReadAllBuffers(rowGroup.Column<double>(schema.LeafColumns[1]))).IsEquivalentTo([1.25, 2.25, 3.25]);
            await AssertByteArraysEqual(ReadAllBinaryBuffers(rowGroup.Column<byte>(schema.LeafColumns[2])),
                [Bytes(1, 1), Bytes(1, 2), Bytes(1, 3)]);
            await Assert.That(ReadAllBuffers(rowGroup.Column<int>(schema.LeafColumns[3]))).IsEquivalentTo([7, 7, 9]);
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
            Plank.Schema.ColumnDefinition.Leaf("ByteValue", ParquetPhysicalType.Int32,
                new ColumnOptions(encodings: ImmutableArray.Create(EncodingKind.Plain)),
                new LogicalType.Int(8, false)),
            Plank.Schema.ColumnDefinition.Leaf("UInt16Value", ParquetPhysicalType.Int32,
                new ColumnOptions(encodings: ImmutableArray.Create(EncodingKind.DeltaBinaryPacked)),
                new LogicalType.Int(16, false)),
            Plank.Schema.ColumnDefinition.Leaf("UInt32Value", ParquetPhysicalType.Int32,
                new ColumnOptions(encodings: ImmutableArray.Create(EncodingKind.ByteStreamSplit)),
                new LogicalType.Int(32, false)),
            Plank.Schema.ColumnDefinition.Leaf("UInt64Value", ParquetPhysicalType.Int64,
                new ColumnOptions(encodings: ImmutableArray.Create(EncodingKind.DeltaBinaryPacked)),
                new LogicalType.Int(64, false)),
            Plank.Schema.ColumnDefinition.Leaf("UInt32Dictionary", ParquetPhysicalType.Int32,
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

                var byteColumn = writerRowGroup.CreateSerializedColumn<byte>(schema.LeafColumns[0]);
                byteColumn.Serialize(byteValues);
                writerRowGroup.Write(byteColumn);

                var ushortColumn = writerRowGroup.CreateSerializedColumn<ushort>(schema.LeafColumns[1]);
                ushortColumn.Serialize(ushortValues);
                writerRowGroup.Write(ushortColumn);

                var uintColumn = writerRowGroup.CreateSerializedColumn<uint>(schema.LeafColumns[2]);
                uintColumn.Serialize(uintValues);
                writerRowGroup.Write(uintColumn);

                var ulongColumn = writerRowGroup.CreateSerializedColumn<ulong>(schema.LeafColumns[3]);
                ulongColumn.Serialize(ulongValues);
                writerRowGroup.Write(ulongColumn);

                var dictionaryColumn = writerRowGroup.CreateSerializedColumn<uint>(schema.LeafColumns[4]);
                dictionaryColumn.Serialize(dictionaryValues);
                writerRowGroup.Write(dictionaryColumn);

                writer.CloseFile();
            }

            using var readStream = File.OpenRead(path);
            using var reader = schema.CreateReader(readStream);
            var rowGroup = reader.RowGroups[0];

            await Assert.That(ReadAllBuffers(rowGroup.Column<byte>(schema.LeafColumns[0]))).IsEquivalentTo(byteValues);
            await Assert.That(ReadAllBuffers(rowGroup.Column<ushort>(schema.LeafColumns[1]))).IsEquivalentTo(ushortValues);
            await Assert.That(ReadAllBuffers(rowGroup.Column<uint>(schema.LeafColumns[2]))).IsEquivalentTo(uintValues);
            await Assert.That(ReadAllBuffers(rowGroup.Column<ulong>(schema.LeafColumns[3]))).IsEquivalentTo(ulongValues);
            await Assert.That(ReadAllBuffers(rowGroup.Column<uint>(schema.LeafColumns[4]))).IsEquivalentTo(dictionaryValues);
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
            Plank.Schema.ColumnDefinition.Leaf("IntOpt", ParquetPhysicalType.Int32,
                new ColumnOptions(ParquetRepetition.Optional)),
            Plank.Schema.ColumnDefinition.Leaf("LongOpt", ParquetPhysicalType.Int64,
                new ColumnOptions(ParquetRepetition.Optional)),
            Plank.Schema.ColumnDefinition.Leaf("DoubleOpt", ParquetPhysicalType.Double,
                new ColumnOptions(ParquetRepetition.Optional)),
            Plank.Schema.ColumnDefinition.Leaf("BoolOpt", ParquetPhysicalType.Boolean,
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

                var intCol = rowGroup.CreateSerializedColumn<int?>(schema.LeafColumns[0]);
                intCol.Serialize(intValues);
                rowGroup.Write(intCol);

                var longCol = rowGroup.CreateSerializedColumn<long?>(schema.LeafColumns[1]);
                longCol.Serialize(longValues);
                rowGroup.Write(longCol);

                var doubleCol = rowGroup.CreateSerializedColumn<double?>(schema.LeafColumns[2]);
                doubleCol.Serialize(doubleValues);
                rowGroup.Write(doubleCol);

                var boolCol = rowGroup.CreateSerializedColumn<bool?>(schema.LeafColumns[3]);
                boolCol.Serialize(boolValues);
                rowGroup.Write(boolCol);

                writer.CloseFile();
            }

            using var readStream = File.OpenRead(path);
            using var reader = schema.CreateReader(readStream);
            var rowGroup2 = reader.RowGroups[0];

            await Assert.That(ReadAllBuffers(rowGroup2.Column<int?>(schema.LeafColumns[0]))).IsEquivalentTo(intValues);
            await Assert.That(ReadAllBuffers(rowGroup2.Column<long?>(schema.LeafColumns[1]))).IsEquivalentTo(longValues);
            await Assert.That(ReadAllBuffers(rowGroup2.Column<double?>(schema.LeafColumns[2]))).IsEquivalentTo(doubleValues);
            await Assert.That(ReadAllBuffers(rowGroup2.Column<bool?>(schema.LeafColumns[3]))).IsEquivalentTo(boolValues);
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
            Plank.Schema.ColumnDefinition.Leaf("StrOpt", ParquetPhysicalType.ByteArray,
                new ColumnOptions(ParquetRepetition.Optional), new LogicalType.String()),
            Plank.Schema.ColumnDefinition.Leaf("BinOpt", ParquetPhysicalType.ByteArray,
                new ColumnOptions(ParquetRepetition.Optional))
        ]);
        byte[]?[] utf8Values = ["hello"u8.ToArray(), null, "world"u8.ToArray(), null, "!"u8.ToArray()];
        byte[]?[] binValues = [new byte[] { 1 }, null, new byte[] { 3, 4 }, null, new byte[] { 5 }];
        try
        {
            using (var writeStream = File.Create(path))
            {
                var writer = schema.CreateWriter(writeStream);
                var rowGroup = writer.StartRowGroup();

                var utf8Col = rowGroup.CreateSerializedColumn<byte[]>(schema.LeafColumns[0]);
                utf8Col.Serialize([.. utf8Values]);
                rowGroup.Write(utf8Col);

                var binCol = rowGroup.CreateSerializedColumn<byte[]>(schema.LeafColumns[1]);
                binCol.Serialize([.. binValues]);
                rowGroup.Write(binCol);

                writer.CloseFile();
            }

            using var readStream = File.OpenRead(path);
            using var reader = schema.CreateReader(readStream);
            var rowGroup2 = reader.RowGroups[0];

            var actualUtf8 = ReadAllBinaryBuffers(rowGroup2.Column<byte>(schema.LeafColumns[0]));
            await Assert.That(actualUtf8.Length).IsEqualTo(utf8Values.Length);
            for (var i = 0; i < utf8Values.Length; i++)
            {
                if (utf8Values[i] is null)
                    await Assert.That(actualUtf8[i]).IsNull();
                else
                    await Assert.That(actualUtf8[i]).IsEquivalentTo(utf8Values[i]!);
            }

            var actualBin = ReadAllBinaryBuffers(rowGroup2.Column<byte>(schema.LeafColumns[1]));
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
            Plank.Schema.ColumnDefinition.Leaf("IntOpt", ParquetPhysicalType.Int32,
                new ColumnOptions(ParquetRepetition.Optional))
        ]);
        int?[] values = [null, null, null];
        try
        {
            using (var writeStream = File.Create(path))
            {
                var writer = schema.CreateWriter(writeStream);
                var rowGroup = writer.StartRowGroup();
                var col = rowGroup.CreateSerializedColumn<int?>(schema.LeafColumns[0]);
                col.Serialize(values);
                rowGroup.Write(col);
                writer.CloseFile();
            }

            using var readStream = File.OpenRead(path);
            using var reader = schema.CreateReader(readStream);
            var rowGroup2 = reader.RowGroups[0];

            await Assert.That(ReadAllBuffers(rowGroup2.Column<int?>(schema.LeafColumns[0]))).IsEquivalentTo(values);
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
            Plank.Schema.ColumnDefinition.Leaf("IntOpt", ParquetPhysicalType.Int32,
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
                var col = rowGroup.CreateSerializedColumn<int?>(schema.LeafColumns[0]);
                col.Serialize(values);
                rowGroup.Write(col);
                writer.CloseFile();
            }

            using var readStream = File.OpenRead(path);
            using var reader = schema.CreateReader(readStream);
            var rowGroup2 = reader.RowGroups[0];

            await Assert.That(ReadAllBuffers(rowGroup2.Column<int?>(schema.LeafColumns[0]))).IsEquivalentTo(values);
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

        var intColumn = rowGroup.CreateSerializedColumn<int>(schema.LeafColumns[0]);
        intColumn.Serialize(ints);
        rowGroup.Write(intColumn);

        if (schema.LeafColumns.Length == 1)
            return;

        var doubleColumn = rowGroup.CreateSerializedColumn<double>(schema.LeafColumns[1]);
        doubleColumn.Serialize(doubles);
        rowGroup.Write(doubleColumn);

        var byteColumn = rowGroup.CreateSerializedColumn<byte[]>(schema.LeafColumns[2]);
        byteColumn.Serialize(bytes);
        rowGroup.Write(byteColumn);

        if (dictionaryInts is null)
            return;

        var dictionaryColumn = rowGroup.CreateSerializedColumn<int>(schema.LeafColumns[3]);
        dictionaryColumn.Serialize(dictionaryInts);
        rowGroup.Write(dictionaryColumn);
    }

    static async Task AssertByteArraysEqual(byte[]?[] actual, byte[][] expected)
    {
        await Assert.That(actual.Length).IsEqualTo(expected.Length);
        for (var i = 0; i < expected.Length; i++)
            await Assert.That(actual[i]).IsEquivalentTo(expected[i]);
    }

    static byte[] Bytes(params byte[] values)
        => values;

    static MemoryStream CreateInt32File(string columnName)
    {
        var schema = new ParquetSchema([
            Plank.Schema.ColumnDefinition.Leaf(columnName, ParquetPhysicalType.Int32)
        ]);
        return CreateFile(schema, rowGroup =>
        {
            var serialized = rowGroup.CreateSerializedColumn<int>(schema.LeafColumns[0]);
            serialized.Serialize([1, 2, 3]);
            rowGroup.Write(serialized);
        });
    }

    static MemoryStream CreateOptionalInt32File(string columnName)
    {
        var schema = new ParquetSchema([
            Plank.Schema.ColumnDefinition.Leaf(columnName, ParquetPhysicalType.Int32, new ColumnOptions(ParquetRepetition.Optional))
        ]);
        return CreateFile(schema, rowGroup =>
        {
            var serialized = rowGroup.CreateSerializedColumn<int?>(schema.LeafColumns[0]);
            serialized.Serialize([1, null, 3]);
            rowGroup.Write(serialized);
        });
    }

    static MemoryStream CreateTwoColumnFile()
    {
        var schema = new ParquetSchema([
            Plank.Schema.ColumnDefinition.Leaf("Value", ParquetPhysicalType.Int32),
            Plank.Schema.ColumnDefinition.Leaf("Other", ParquetPhysicalType.Int64)
        ]);
        return CreateFile(schema, rowGroup =>
        {
            var value = rowGroup.CreateSerializedColumn<int>(schema.LeafColumns[0]);
            value.Serialize([1, 2, 3]);
            rowGroup.Write(value);

            var other = rowGroup.CreateSerializedColumn<long>(schema.LeafColumns[1]);
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

    static T[] ReadAllBuffers<T>(RowGroupColumn<T> buffers)
    {
        var values = new List<T>();
        foreach (var buffer in buffers)
            foreach (var value in buffer.Values)
                values.Add(value);
        return values.ToArray();
    }

    static byte[]?[] ReadAllBinaryBuffers(RowGroupColumn<byte> buffers)
    {
        var values = new List<byte[]?>();
        foreach (var buffer in buffers)
            for (var i = 0; i < buffer.Count; i++)
                values.Add(buffer.IsNull(i) ? null : buffer.GetValue(i).ToArray());
        return values.ToArray();
    }

    static string GetTempPath()
        => Path.Combine(Path.GetTempPath(), $"plank-reader-tests-{Guid.NewGuid():N}.parquet");
}
