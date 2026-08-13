using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Plank.Reading;
using Plank.Reading.Logical.Internal;
using Plank.Schema;
using Plank.Writing;
using Plank.Writing.PageStrategy;

namespace Plank.Tests.Reading;

internal sealed class NullableNumericDecodingTests
{
    static readonly EncodingKind[] Encodings =
    [
        EncodingKind.Plain,
        EncodingKind.RleDictionary,
        EncodingKind.ByteStreamSplit
    ];

    [Test]
    public void NullableNumericsPreserveValuesAcrossEncodingsPagesAndNullPatterns()
    {
        ParquetDataPageVersion[] pageVersions =
            [ParquetDataPageVersion.V1, ParquetDataPageVersion.V2];
        foreach (var pageVersion in pageVersions)
        foreach (var encoding in Encodings)
        foreach (var pattern in Enum.GetValues<NullPattern>())
        {
            AssertEqual(CreateValues<int>(pattern, CreateInt32),
                RoundTrip(ParquetPhysicalType.Int32, CreateValues<int>(pattern, CreateInt32),
                    encoding, pageVersion), encoding, pageVersion, pattern);
            AssertEqual(CreateValues<long>(pattern, CreateInt64),
                RoundTrip(ParquetPhysicalType.Int64, CreateValues<long>(pattern, CreateInt64),
                    encoding, pageVersion), encoding, pageVersion, pattern);
            AssertEqual(CreateValues<float>(pattern, CreateFloat),
                RoundTrip(ParquetPhysicalType.Float, CreateValues<float>(pattern, CreateFloat),
                    encoding, pageVersion), encoding, pageVersion, pattern);
            AssertEqual(CreateValues<double>(pattern, CreateDouble),
                RoundTrip(ParquetPhysicalType.Double, CreateValues<double>(pattern, CreateDouble),
                    encoding, pageVersion), encoding, pageVersion, pattern);
        }
    }

    [Test]
    public void LargeNullableNumericPageIsSplitIntoBoundedBuffersAcrossEncodingsAndCompression()
    {
        ParquetDataPageVersion[] pageVersions =
            [ParquetDataPageVersion.V1, ParquetDataPageVersion.V2];
        CompressionKind[] compressions =
            [CompressionKind.None, CompressionKind.Snappy, CompressionKind.Zstd];
        var expected = CreateLargeDoubleValues(65_539);
        var maximumBufferCount = ColumnChunkReader.DecodeBatchSizeBytes / Unsafe.SizeOf<double?>();

        foreach (var pageVersion in pageVersions)
        foreach (var encoding in Encodings)
        foreach (var compression in compressions)
        {
            var schema = CreateSinglePageSchema(ParquetPhysicalType.Double, encoding);
            var file = WriteSinglePage(schema, expected, pageVersion, compression);
            var retained = new List<ParquetBuffer>();
            try
            {
                using (var reader = schema.CreateReader(new MemoryReadSource(file)))
                {
                    foreach (var buffer in reader.RowGroups[0].Column<double?>(0))
                    {
                        if (buffer.Count > maximumBufferCount)
                            throw new InvalidOperationException(
                                $"{pageVersion}/{encoding}/{compression}: buffer contains {buffer.Count} values, " +
                                $"maximum is {maximumBufferCount}.");
                        retained.Add(buffer.Retain());
                    }
                }

                if (retained.Count <= 1)
                    throw new InvalidOperationException(
                        $"{pageVersion}/{encoding}/{compression}: the large physical page was not split.");
                var offset = 0;
                foreach (var buffer in retained)
                {
                    var values = buffer.AsSpan<double?>();
                    AssertEqual(expected.AsSpan(offset, values.Length), values, encoding, pageVersion,
                        NullPattern.Mixed);
                    offset += values.Length;
                }
                if (offset != expected.Length)
                    throw new InvalidOperationException(
                        $"{pageVersion}/{encoding}/{compression}: expected {expected.Length} values, got {offset}.");
            }
            finally
            {
                foreach (var buffer in retained)
                    buffer.Dispose();
            }
        }
    }

    [Test]
    public void LargeRequiredNumericPageIsSplitAcrossEncodingsPagesAndCompression()
    {
        ParquetDataPageVersion[] pageVersions =
            [ParquetDataPageVersion.V1, ParquetDataPageVersion.V2];
        CompressionKind[] compressions =
            [CompressionKind.None, CompressionKind.Snappy, CompressionKind.Zstd];
        var expected = new double[70_003];
        for (var i = 0; i < expected.Length; i++)
            expected[i] = CreateDouble(i % 2_048);

        foreach (var pageVersion in pageVersions)
        foreach (var encoding in new[] { EncodingKind.Plain, EncodingKind.ByteStreamSplit })
        foreach (var compression in compressions)
        {
            var schema = CreateSinglePageSchema(ParquetPhysicalType.Double, encoding, optional: false);
            AssertLargeRequired(schema, expected, encoding, pageVersion, compression);
        }
    }

    [Test]
    public void LargeFixedWidthLogicalAndConvertedValuesAreSplitIntoBoundedBuffers()
    {
        foreach (var encoding in Encodings)
        {
            var timestamps = new DateTime[40_003];
            var optionalTimestamps = new DateTime?[20_003];
            for (var i = 0; i < timestamps.Length; i++)
                timestamps[i] = DateTime.UnixEpoch.AddTicks(i * 10L);
            for (var i = 0; i < optionalTimestamps.Length; i++)
                if (i % 11 != 0)
                    optionalTimestamps[i] = DateTime.UnixEpoch.AddTicks(i * 10L);
            var timestampType = new LogicalType.Timestamp(TimeUnit.Micros, IsAdjustedToUtc: true);
            if (encoding != EncodingKind.RleDictionary)
                AssertLargeRequired(CreateSinglePageSchema(ParquetPhysicalType.Int64, encoding,
                        optional: false, logicalType: timestampType), timestamps, encoding);
            AssertLargeOptional(CreateSinglePageSchema(ParquetPhysicalType.Int64, encoding,
                    optional: true, logicalType: timestampType), optionalTimestamps, encoding);

            var converted = new ScaledValue[20_003];
            var optionalConverted = new ScaledValue?[20_003];
            for (var i = 0; i < converted.Length; i++)
            {
                converted[i] = new ScaledValue(i / 100m);
                if (i % 17 != 0)
                    optionalConverted[i] = converted[i];
            }
            var converter = new ScaledValueConverter();
            if (encoding != EncodingKind.RleDictionary)
                AssertLargeRequired(CreateSinglePageSchema(ParquetPhysicalType.Int64, encoding,
                        optional: false, converter: converter), converted, encoding);
            AssertLargeOptional(CreateSinglePageSchema(ParquetPhysicalType.Int64, encoding,
                    optional: true, converter: converter), optionalConverted, encoding);

            var guids = new GuidValue[20_003];
            var optionalGuids = new GuidValue?[20_003];
            for (var i = 0; i < guids.Length; i++)
            {
                guids[i] = new GuidValue(new Guid(i, 0, 0, new byte[8]));
                if (i % 13 != 0)
                    optionalGuids[i] = guids[i];
            }
            var guidConverter = new GuidValueConverter();
            var uuidType = new LogicalType.Uuid();
            if (encoding != EncodingKind.RleDictionary)
                AssertLargeRequired(CreateSinglePageSchema(ParquetPhysicalType.FixedLenByteArray, encoding,
                        optional: false, logicalType: uuidType, typeLength: 16, converter: guidConverter),
                    guids, encoding);
            AssertLargeOptional(CreateSinglePageSchema(ParquetPhysicalType.FixedLenByteArray, encoding,
                    optional: true, logicalType: uuidType, typeLength: 16, converter: guidConverter),
                optionalGuids, encoding);
        }

        var booleans = new bool[300_003];
        var optionalBooleans = new bool?[140_003];
        for (var i = 0; i < booleans.Length; i++)
            booleans[i] = (i & 3) != 0;
        for (var i = 0; i < optionalBooleans.Length; i++)
            if (i % 17 != 0)
                optionalBooleans[i] = (i & 3) != 0;
        AssertLargeRequired(CreateSinglePageSchema(ParquetPhysicalType.Boolean, EncodingKind.Plain,
                optional: false), booleans, EncodingKind.Plain);
        AssertLargeOptional(CreateSinglePageSchema(ParquetPhysicalType.Boolean, EncodingKind.Plain,
                optional: true), optionalBooleans, EncodingKind.Plain);
    }

    [Test]
    public void LargeByteStreamSplitPagesBatchEveryRemainingFixedWidthProjection()
    {
        const EncodingKind encoding = EncodingKind.ByteStreamSplit;

        var bytes = new byte[70_003];
        var unsigned16 = new ushort[70_003];
        var unsigned32 = new uint[70_003];
        var unsigned64 = new ulong[40_003];
        for (var i = 0; i < bytes.Length; i++)
        {
            bytes[i] = (byte)i;
            unsigned16[i] = (ushort)(i * 17);
            unsigned32[i] = unchecked((uint)i * 0x9E37_79B9U);
        }
        for (var i = 0; i < unsigned64.Length; i++)
            unsigned64[i] = unchecked((ulong)i * 0x9E37_79B9_7F4A_7C15UL);

        AssertLargeRequired(CreateSinglePageSchema(ParquetPhysicalType.Int32, encoding,
                optional: false, logicalType: new LogicalType.Int(8, isSigned: false)), bytes, encoding);
        AssertLargeOptional(CreateSinglePageSchema(ParquetPhysicalType.Int32, encoding,
                optional: true, logicalType: new LogicalType.Int(8, isSigned: false)),
            CreateOptional(bytes, 17), encoding);
        AssertLargeRequired(CreateSinglePageSchema(ParquetPhysicalType.Int32, encoding,
                optional: false, logicalType: new LogicalType.Int(16, isSigned: false)), unsigned16, encoding);
        AssertLargeOptional(CreateSinglePageSchema(ParquetPhysicalType.Int32, encoding,
                optional: true, logicalType: new LogicalType.Int(16, isSigned: false)),
            CreateOptional(unsigned16, 17), encoding);
        AssertLargeRequired(CreateSinglePageSchema(ParquetPhysicalType.Int32, encoding,
                optional: false, logicalType: new LogicalType.Int(32, isSigned: false)), unsigned32, encoding);
        AssertLargeOptional(CreateSinglePageSchema(ParquetPhysicalType.Int32, encoding,
                optional: true, logicalType: new LogicalType.Int(32, isSigned: false)),
            CreateOptional(unsigned32, 17), encoding);
        AssertLargeRequired(CreateSinglePageSchema(ParquetPhysicalType.Int64, encoding,
                optional: false, logicalType: new LogicalType.Int(64, isSigned: false)), unsigned64, encoding);
        AssertLargeOptional(CreateSinglePageSchema(ParquetPhysicalType.Int64, encoding,
                optional: true, logicalType: new LogicalType.Int(64, isSigned: false)),
            CreateOptional(unsigned64, 17), encoding);

        var dates = new DateOnly[70_003];
        var times = new TimeOnly[40_003];
        for (var i = 0; i < dates.Length; i++)
            dates[i] = DateOnly.FromDateTime(DateTime.UnixEpoch).AddDays(i % 10_000);
        for (var i = 0; i < times.Length; i++)
            times[i] = TimeOnly.MinValue.Add(TimeSpan.FromTicks(i * 10L));
        AssertLargeRequired(CreateSinglePageSchema(ParquetPhysicalType.Int32, encoding,
                optional: false, logicalType: new LogicalType.Date()), dates, encoding);
        AssertLargeOptional(CreateSinglePageSchema(ParquetPhysicalType.Int32, encoding,
                optional: true, logicalType: new LogicalType.Date()), CreateOptional(dates, 17), encoding);
        AssertLargeRequired(CreateSinglePageSchema(ParquetPhysicalType.Int64, encoding,
                optional: false, logicalType: new LogicalType.Time(TimeUnit.Micros, false)), times, encoding);
        AssertLargeOptional(CreateSinglePageSchema(ParquetPhysicalType.Int64, encoding,
                optional: true, logicalType: new LogicalType.Time(TimeUnit.Micros, false)),
            CreateOptional(times, 17), encoding);

        var timestamps = new DateTimeOffset[20_003];
        for (var i = 0; i < timestamps.Length; i++)
            timestamps[i] = DateTimeOffset.UnixEpoch.AddTicks(i * 10L);
        AssertLargeRequired(CreateSinglePageSchema(ParquetPhysicalType.Int64, encoding,
                optional: false, logicalType: new LogicalType.Timestamp(TimeUnit.Micros, true)),
            timestamps, encoding);
        AssertLargeOptional(CreateSinglePageSchema(ParquetPhysicalType.Int64, encoding,
                optional: true, logicalType: new LogicalType.Timestamp(TimeUnit.Micros, true)),
            CreateOptional(timestamps, 17), encoding);
    }

    [Test]
    public void LargeNullableNumericPagesSplitForEverySupportedProjectionAndNullExtreme()
    {
        var intValues = new int?[32_771];
        var longValues = new long?[16_387];
        var floatValues = new float?[32_771];
        for (var i = 0; i < intValues.Length; i++)
            intValues[i] = CreateInt32(i);
        for (var i = 0; i < floatValues.Length; i++)
            if (i % 5 != 0)
                floatValues[i] = CreateFloat(i);

        AssertLargeSinglePage(ParquetPhysicalType.Int32, intValues);
        AssertLargeSinglePage(ParquetPhysicalType.Int64, longValues);
        AssertLargeSinglePage(ParquetPhysicalType.Float, floatValues);
    }

    [Test]
    public void UnretainedNullableNumericBatchesReuseOutputStorage()
    {
        var schema = CreateSinglePageSchema(ParquetPhysicalType.Double, EncodingKind.Plain);
        var expected = CreateLargeDoubleValues(65_539);
        var file = WriteSinglePage(schema, expected, ParquetDataPageVersion.V2, CompressionKind.None);
        using var reader = schema.CreateReader(new MemoryReadSource(file));
        var buffers = reader.RowGroups[0].Column<double?>(0).GetEnumerator();
        try
        {
            if (!buffers.MoveNext())
                throw new InvalidOperationException("Expected a first nullable numeric batch.");
            var firstAddress = buffers.Current.NativeValues.DangerousGetAddress();

            if (!buffers.MoveNext())
                throw new InvalidOperationException("Expected a second nullable numeric batch.");
            var secondAddress = buffers.Current.NativeValues.DangerousGetAddress();

            if (secondAddress != firstAddress)
                throw new InvalidOperationException("Expected an unretained output allocation to be reused.");
        }
        finally
        {
            buffers.Dispose();
        }
    }

    [Test]
    public void RetainedRequiredFixedWidthBatchSurvivesAdvancementAndReaderDisposal()
    {
        var schema = CreateSinglePageSchema(ParquetPhysicalType.Double, EncodingKind.Plain,
            optional: false);
        var expected = new double[70_003];
        for (var i = 0; i < expected.Length; i++)
            expected[i] = CreateDouble(i % 2_048);

        using var stream = new MemoryStream();
        var writer = schema.CreateWriter(stream, new ParquetWriterOptions
        {
            Compression = CompressionKind.None,
            DataPageVersion = ParquetDataPageVersion.V2,
            WritePageIndexes = false,
            WritePageCrc = false
        });
        var serialized = writer.CreateSerializedColumn<double>(schema.LeafColumns[0]);
        serialized.Serialize(expected);
        writer.StartRowGroup().Write(serialized);
        writer.CloseFile();

        ParquetBuffer retained = default;
        double[] firstBatch;
        try
        {
            using (var reader = schema.CreateReader(new MemoryReadSource(stream.ToArray())))
            {
                var buffers = reader.RowGroups[0].Column<double>(0).GetEnumerator();
                try
                {
                    if (!buffers.MoveNext())
                        throw new InvalidOperationException("Expected a required fixed-width batch.");
                    firstBatch = buffers.Current.Values.ToArray();
                    retained = buffers.Current.Retain();
                    if (!buffers.MoveNext())
                        throw new InvalidOperationException("Expected a second required fixed-width batch.");
                    if (buffers.Current.NativeValues.DangerousGetAddress() == retained.DangerousGetAddress())
                        throw new InvalidOperationException(
                            "Advancing overwrote the storage held by the retained batch.");
                }
                finally
                {
                    buffers.Dispose();
                }
            }

            if (!retained.AsSpan<double>().SequenceEqual(firstBatch))
                throw new InvalidOperationException(
                    "Retained required fixed-width batch changed after advancing and disposing the reader.");
        }
        finally
        {
            retained.Dispose();
        }
    }

    [Test]
    public void NullableInt32ReadsLegacyBitPackedDefinitionLevels()
    {
        var schema = CreateSchema(ParquetPhysicalType.Int32, EncodingKind.Plain);
        var column = schema.LeafColumns[0].Column;
        byte[] payload = new byte[1 + 4 * sizeof(int)];
        payload[0] = 0b1011_0010;
        int[] physicalValues = [10, 20, 30, 40];
        for (var i = 0; i < physicalValues.Length; i++)
            BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(1 + i * sizeof(int)), physicalValues[i]);
        var header = CreateHeader(PageHeaderType.DataPage, payload.Length, valueCount: 8,
            EncodingKind.Plain, definitionByteLength: 0, nullCount: 4,
            definitionEncoding: EncodingKind.BitPacked);
        var buffers = default(ColumnReadBuffers<int?>);
        try
        {
            var decoded = ColumnChunkReader.TryDecodeNullablePageIntoNative(header, payload, column,
                rowCount: 8, ref buffers, DefaultParquetBufferPool.Shared, out var buffer);
            if (!decoded)
                throw new InvalidOperationException("The native nullable numeric path declined the page.");
            int?[] expected = [10, null, 20, 30, null, null, 40, null];
            AssertEqual(expected, buffer.Values, EncodingKind.Plain, ParquetDataPageVersion.V1,
                NullPattern.Mixed);
        }
        finally
        {
            buffers.Dispose();
        }
    }

    [Test]
    public void NullableNumericsRejectCorruptDefinitionLevels()
    {
        var schema = CreateSchema(ParquetPhysicalType.Int32, EncodingKind.Plain);
        var column = schema.LeafColumns[0].Column;

        AssertCorrupt(column, definitionPayload: [0x03], valueCount: 8, nullCount: 8);
        AssertCorrupt(column, definitionPayload: [0x10, 0x02], valueCount: 8, nullCount: 0);
        AssertCorrupt(column, definitionPayload: [0x08, 0x01], valueCount: 4, nullCount: 1,
            physicalPayload: new byte[4 * sizeof(int)]);
    }

    [Test]
    public void NullableNumericRetainedPageSurvivesAdvancementAndReaderDisposal()
    {
        var schema = CreateSchema(ParquetPhysicalType.Double, EncodingKind.ByteStreamSplit);
        var expected = CreateValues<double>(NullPattern.Mixed, CreateDouble);
        using var stream = new MemoryStream();
        var writer = schema.CreateWriter(stream, new ParquetWriterOptions
        {
            Compression = CompressionKind.None,
            DataPageVersion = ParquetDataPageVersion.V2
        });
        var serialized = writer.CreateSerializedColumn<double?>(schema.LeafColumns[0]);
        serialized.Serialize(expected);
        writer.StartRowGroup().Write(serialized);
        writer.CloseFile();

        ParquetBuffer retained = default;
        double?[] firstPage;
        try
        {
            var input = new MemoryReadSource(stream.ToArray());
            using (var reader = schema.CreateReader(input))
            {
                var buffers = reader.RowGroups[0].Column<double?>(0).GetEnumerator();
                try
                {
                    if (!buffers.MoveNext())
                        throw new InvalidOperationException("Expected a nullable numeric value buffer.");
                    firstPage = buffers.Current.Values.ToArray();
                    retained = buffers.Current.Retain();
                    if (!buffers.MoveNext())
                        throw new InvalidOperationException("Expected multiple nullable numeric pages.");
                }
                finally
                {
                    buffers.Dispose();
                }
            }

            if (!retained.AsSpan<double?>().SequenceEqual(firstPage))
                throw new InvalidOperationException(
                    "Retained nullable numeric page changed after advancing and disposing the reader.");
        }
        finally
        {
            retained.Dispose();
        }
    }

    static void AssertCorrupt(Column column, byte[] definitionPayload, uint valueCount, uint nullCount,
        byte[]? physicalPayload = null)
    {
        physicalPayload ??= [];
        var payload = new byte[definitionPayload.Length + physicalPayload.Length];
        definitionPayload.CopyTo(payload, 0);
        physicalPayload.CopyTo(payload, definitionPayload.Length);
        var header = CreateHeader(PageHeaderType.DataPageV2, payload.Length, valueCount,
            EncodingKind.Plain, checked((uint)definitionPayload.Length), nullCount,
            EncodingKind.Rle);
        var buffers = default(ColumnReadBuffers<int?>);
        try
        {
            var page = default(ColumnChunkReader.FixedWidthPageState);
            Assert.Throws<CorruptParquetException>(() =>
                ColumnChunkReader.TryStartFixedWidthPageBatches(header, payload, column,
                    rowCount: valueCount, ref buffers, DefaultParquetBufferPool.Shared, ref page, out _));
            Assert.Throws<CorruptParquetException>(() =>
                ColumnChunkReader.TryDecodeNullablePageIntoNative(header, payload, column,
                    rowCount: valueCount, ref buffers, DefaultParquetBufferPool.Shared, out _));
        }
        finally
        {
            buffers.Dispose();
        }
    }

    static ParquetSchema CreateSchema(ParquetPhysicalType physicalType, EncodingKind encoding)
    {
        var options = new ColumnOptions(ParquetRepetition.Optional,
            ImmutableArray.Create(encoding));
        return new([
            ColumnDefinition.OptionalLeaf("value", physicalType, options,
                pageStrategy: new FixedRowsPageStrategy(rowsPerPage: 31,
                    dictionary: encoding == EncodingKind.RleDictionary))
        ]);
    }

    static ParquetSchema CreateSinglePageSchema(ParquetPhysicalType physicalType, EncodingKind encoding,
        bool optional = true, LogicalType? logicalType = null, uint typeLength = 0,
        ParquetValueConverter? converter = null)
    {
        var options = new ColumnOptions(optional ? ParquetRepetition.Optional : ParquetRepetition.Required,
            ImmutableArray.Create(encoding), typeLength);
        var pageStrategy = new SinglePageStrategy(encoding == EncodingKind.RleDictionary);
        var definition = optional
            ? ColumnDefinition.OptionalLeaf("value", physicalType, options, logicalType, pageStrategy, converter)
            : ColumnDefinition.RequiredLeaf("value", physicalType, options, logicalType, pageStrategy, converter);
        return new([definition]);
    }

    static byte[] WriteSinglePage<T>(ParquetSchema schema, T?[] values,
        ParquetDataPageVersion pageVersion, CompressionKind compression)
        where T : struct
    {
        using var stream = new MemoryStream();
        var writer = schema.CreateWriter(stream, new ParquetWriterOptions
        {
            Compression = compression,
            DataPageVersion = pageVersion,
            WritePageIndexes = false,
            WritePageCrc = false
        });
        var serialized = writer.CreateSerializedColumn<T?>(schema.LeafColumns[0]);
        serialized.Serialize(values);
        writer.StartRowGroup().Write(serialized);
        writer.CloseFile();
        return stream.ToArray();
    }

    static void AssertLargeSinglePage<T>(ParquetPhysicalType physicalType, T?[] expected)
        where T : struct
    {
        var schema = CreateSinglePageSchema(physicalType, EncodingKind.Plain);
        var file = WriteSinglePage(schema, expected, ParquetDataPageVersion.V2, CompressionKind.None);
        using var reader = schema.CreateReader(new MemoryReadSource(file));
        var maximumBufferCount = ColumnChunkReader.DecodeBatchSizeBytes / Unsafe.SizeOf<T?>();
        var offset = 0;
        var bufferCount = 0;
        foreach (var buffer in reader.RowGroups[0].Column<T?>(0))
        {
            bufferCount++;
            if (buffer.Count > maximumBufferCount)
                throw new InvalidOperationException(
                    $"{typeof(T).Name}: buffer contains {buffer.Count} values, maximum is {maximumBufferCount}.");
            AssertEqual(expected.AsSpan(offset, buffer.Count), buffer.Values, EncodingKind.Plain,
                ParquetDataPageVersion.V2, NullPattern.Mixed);
            offset += buffer.Count;
        }
        if (bufferCount <= 1 || offset != expected.Length)
            throw new InvalidOperationException(
                $"{typeof(T).Name}: expected a split page with {expected.Length} values, got " +
                $"{bufferCount} buffers and {offset} values.");
    }

    static void AssertLargeRequired<T>(ParquetSchema schema, T[] expected, EncodingKind encoding,
        ParquetDataPageVersion pageVersion = ParquetDataPageVersion.V2,
        CompressionKind compression = CompressionKind.None)
        where T : struct
    {
        using var stream = new MemoryStream();
        var writer = schema.CreateWriter(stream, new ParquetWriterOptions
        {
            Compression = compression,
            DataPageVersion = pageVersion,
            WritePageIndexes = false,
            WritePageCrc = false
        });
        var serialized = writer.CreateSerializedColumn<T>(schema.LeafColumns[0]);
        serialized.Serialize(expected);
        writer.StartRowGroup().Write(serialized);
        writer.CloseFile();

        using var reader = schema.CreateReader(new MemoryReadSource(stream.ToArray()));
        var maximumBufferCount = ColumnChunkReader.DecodeBatchSizeBytes / Unsafe.SizeOf<T>();
        var offset = 0;
        var bufferCount = 0;
        foreach (var buffer in reader.RowGroups[0].Column<T>(0))
        {
            bufferCount++;
            if (buffer.Count > maximumBufferCount)
                throw new InvalidOperationException(
                    $"{typeof(T).Name}/{encoding}: buffer contains {buffer.Count} values, " +
                    $"maximum is {maximumBufferCount}.");
            if (!buffer.Values.SequenceEqual(expected.AsSpan(offset, buffer.Count)))
                throw new InvalidOperationException(
                    $"{typeof(T).Name}/{encoding}: values differ in batch {bufferCount}.");
            offset += buffer.Count;
        }
        if (bufferCount <= 1 || offset != expected.Length)
            throw new InvalidOperationException(
                $"{typeof(T).Name}/{encoding}: expected a split page with {expected.Length} values, got " +
                $"{bufferCount} buffers and {offset} values.");
    }

    static void AssertLargeOptional<T>(ParquetSchema schema, T?[] expected, EncodingKind encoding)
        where T : struct
    {
        var file = WriteSinglePage(schema, expected, ParquetDataPageVersion.V2, CompressionKind.None);
        using var reader = schema.CreateReader(new MemoryReadSource(file));
        var maximumBufferCount = ColumnChunkReader.DecodeBatchSizeBytes / Unsafe.SizeOf<T?>();
        var offset = 0;
        var bufferCount = 0;
        foreach (var buffer in reader.RowGroups[0].Column<T?>(0))
        {
            bufferCount++;
            if (buffer.Count > maximumBufferCount)
                throw new InvalidOperationException(
                    $"{typeof(T).Name}?/{encoding}: buffer contains {buffer.Count} values, " +
                    $"maximum is {maximumBufferCount}.");
            if (!buffer.Values.SequenceEqual(expected.AsSpan(offset, buffer.Count)))
                throw new InvalidOperationException(
                    $"{typeof(T).Name}?/{encoding}: values differ in batch {bufferCount}.");
            offset += buffer.Count;
        }
        if (bufferCount <= 1 || offset != expected.Length)
            throw new InvalidOperationException(
                $"{typeof(T).Name}?/{encoding}: expected a split page with {expected.Length} values, got " +
                $"{bufferCount} buffers and {offset} values.");
    }

    static T?[] CreateOptional<T>(T[] values, int nullInterval)
        where T : struct
    {
        var optional = new T?[values.Length];
        for (var i = 0; i < values.Length; i++)
            if (i % nullInterval != 0)
                optional[i] = values[i];
        return optional;
    }

    static T?[] RoundTrip<T>(ParquetPhysicalType physicalType, T?[] values, EncodingKind encoding,
        ParquetDataPageVersion pageVersion)
        where T : struct
    {
        var schema = CreateSchema(physicalType, encoding);
        using var stream = new MemoryStream();
        var writer = schema.CreateWriter(stream, new ParquetWriterOptions
        {
            Compression = CompressionKind.None,
            DataPageVersion = pageVersion
        });
        var serialized = writer.CreateSerializedColumn<T?>(schema.LeafColumns[0]);
        serialized.Serialize(values);
        writer.StartRowGroup().Write(serialized);
        writer.CloseFile();

        using var reader = schema.CreateReader(new MemoryReadSource(stream.ToArray()));
        var actual = new List<T?>(values.Length);
        foreach (var buffer in reader.RowGroups[0].Column<T?>(0))
            actual.AddRange(buffer.Values);
        return actual.ToArray();
    }

    static T?[] CreateValues<T>(NullPattern pattern, Func<int, T> factory)
        where T : struct
    {
        var values = new T?[257];
        for (var i = 0; i < values.Length; i++)
        {
            if (pattern == NullPattern.AllNull ||
                pattern == NullPattern.Mixed &&
                (i < 17 || i >= 241 || i % 7 == 0 || i is >= 93 and < 128))
                continue;
            values[i] = factory(i);
        }
        return values;
    }

    static double?[] CreateLargeDoubleValues(int count)
    {
        var values = new double?[count];
        for (var i = 0; i < values.Length; i++)
            if (i % 31 != 0 && i % 257 is not (>= 80 and < 96))
                values[i] = CreateDouble(i % 2_048);
        return values;
    }

    static int CreateInt32(int index)
        => unchecked((int)((uint)index * 0x9E37_79B9u) ^ (index % 19));

    static long CreateInt64(int index)
        => unchecked((long)((ulong)(uint)index * 0x9E37_79B9_7F4A_7C15UL) ^ (uint)(index % 23));

    static float CreateFloat(int index)
        => (index - 128) * 0.125f + index % 11 * 0.0009765625f;

    static double CreateDouble(int index)
        => (index - 128L) * 0.125 + index % 13 * 0.00000095367431640625;

    static void AssertEqual<T>(ReadOnlySpan<T?> expected, ReadOnlySpan<T?> actual,
        EncodingKind encoding, ParquetDataPageVersion pageVersion, NullPattern pattern)
        where T : struct
    {
        if (actual.Length != expected.Length)
            throw new InvalidOperationException(
                $"{pageVersion}/{encoding}/{typeof(T).Name}/{pattern}: expected {expected.Length} values, " +
                $"got {actual.Length}.");
        for (var i = 0; i < expected.Length; i++)
        {
            if (expected[i].HasValue != actual[i].HasValue)
                throw new InvalidOperationException(
                    $"{pageVersion}/{encoding}/{typeof(T).Name}/{pattern}: null mismatch at {i}.");
            if (expected[i] is { } expectedValue && actual[i] is { } actualValue &&
                !BitsEqual(expectedValue, actualValue))
                throw new InvalidOperationException(
                    $"{pageVersion}/{encoding}/{typeof(T).Name}/{pattern}: value mismatch at {i}.");
        }
    }

    static bool BitsEqual<T>(T left, T right)
        where T : struct
    {
        if (typeof(T) == typeof(float))
            return BitConverter.SingleToInt32Bits((float)(object)left) ==
                BitConverter.SingleToInt32Bits((float)(object)right);
        if (typeof(T) == typeof(double))
            return BitConverter.DoubleToInt64Bits((double)(object)left) ==
                BitConverter.DoubleToInt64Bits((double)(object)right);
        return EqualityComparer<T>.Default.Equals(left, right);
    }

    static PageHeader CreateHeader(PageHeaderType type, int payloadLength, uint valueCount,
        EncodingKind encoding, uint definitionByteLength, uint nullCount,
        EncodingKind definitionEncoding)
        => new(type, checked((uint)payloadLength), checked((uint)payloadLength), valueCount, encoding,
            HeaderLength: 1, RepetitionLevelsByteLength: 0,
            DefinitionLevelsByteLength: definitionByteLength, NullCount: nullCount,
            IsCompressed: false, RepetitionLevelEncoding: EncodingKind.Rle,
            DefinitionLevelEncoding: definitionEncoding, RowCount: valueCount);

    enum NullPattern
    {
        NoNulls,
        AllNull,
        Mixed
    }

    readonly record struct ScaledValue(decimal Value);

    sealed class ScaledValueConverter : ParquetValueConverter<ScaledValue, long>
    {
        public override long ConvertToPhysical(ScaledValue value)
            => decimal.ToInt64(value.Value * 100m);

        public override ScaledValue ConvertFromPhysical(long value)
            => new(value / 100m);
    }

    readonly record struct GuidValue(Guid Value);

    sealed class GuidValueConverter : ParquetValueConverter<GuidValue, Guid>
    {
        public override Guid ConvertToPhysical(GuidValue value)
            => value.Value;

        public override GuidValue ConvertFromPhysical(Guid value)
            => new(value);
    }

    sealed class FixedRowsPageStrategy(uint rowsPerPage, bool dictionary) : IPageStrategy
    {
        public DictionaryMode GetDictionaryMode()
            => dictionary ? DictionaryMode.Forced : DictionaryMode.Disabled;

        public bool ShouldDropDictionary(uint uniqueCount, uint totalRowCount, uint rowsSeen)
            => false;

        public bool ShouldStartNewDataPage(uint totalRowCount, uint rowsWritten, uint currentPageRowCount)
            => currentPageRowCount >= rowsPerPage;
    }

    sealed class SinglePageStrategy(bool dictionary) : IPageStrategy
    {
        public DictionaryMode GetDictionaryMode()
            => dictionary ? DictionaryMode.Forced : DictionaryMode.Disabled;

        public bool ShouldDropDictionary(uint uniqueCount, uint totalRowCount, uint rowsSeen)
            => false;

        public bool ShouldStartNewDataPage(uint totalRowCount, uint rowsWritten, uint currentPageRowCount)
            => false;
    }
}
