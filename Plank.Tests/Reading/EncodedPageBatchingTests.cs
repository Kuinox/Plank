using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Plank.Reading;
using Plank.Reading.Logical.Internal;
using Plank.Schema;
using Plank.Writing;
using Plank.Writing.PageStrategy;

namespace Plank.Tests.Reading;

internal sealed class EncodedPageBatchingTests
{
    [Test]
    public void LargeRequiredNumericPagesBatchEveryEncoding()
    {
        AssertRequired(CreateBooleans(600_003), ParquetPhysicalType.Boolean, EncodingKind.Rle);
        AssertRequired(CreateInt32s(180_003), ParquetPhysicalType.Int32,
            EncodingKind.DeltaBinaryPacked);
        AssertRequired(CreateInt32s(180_003), ParquetPhysicalType.Int32,
            EncodingKind.RleDictionary, dictionary: true);
        AssertRequired(CreateInt32s(180_003), ParquetPhysicalType.Int32,
            EncodingKind.PlainDictionary, dictionary: true);
        AssertRequired(CreateDoubles(100_003), ParquetPhysicalType.Double, EncodingKind.Alp);
    }

    [Test]
    public void LargeOptionalNumericPagesBatchEveryEncoding()
    {
        foreach (var pageVersion in new[]
                 {
                     ParquetDataPageVersion.V1,
                     ParquetDataPageVersion.V2
                 })
        {
            AssertOptional(CreateOptional(CreateBooleans(600_003)), ParquetPhysicalType.Boolean,
                EncodingKind.Rle, pageVersion: pageVersion);
            AssertOptional(CreateOptional(CreateInt32s(180_003)), ParquetPhysicalType.Int32,
                EncodingKind.DeltaBinaryPacked, pageVersion: pageVersion);
            AssertOptional(CreateOptional(CreateInt32s(180_003)), ParquetPhysicalType.Int32,
                EncodingKind.RleDictionary, dictionary: true, pageVersion: pageVersion);
            AssertOptional(CreateOptional(CreateDoubles(100_003)), ParquetPhysicalType.Double,
                EncodingKind.Alp, pageVersion: pageVersion);
        }
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    [Arguments(247)]
    [Arguments(260)]
    public void LargeDenseOptionalInt32DictionaryBatchesCommonBitWidths(int distinctCount)
    {
        var expected = new int?[180_003];
        for (var i = 0; i < expected.Length; i++)
            expected[i] = i % distinctCount;
        AssertOptional(expected, ParquetPhysicalType.Int32, EncodingKind.RleDictionary,
            dictionary: true);
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    [Arguments(7)]
    [Arguments(10)]
    public void LargeDenseOptionalInt64DictionaryBatchesNarrowBitWidths(int distinctCount)
    {
        var expected = new long?[180_003];
        for (var i = 0; i < expected.Length; i++)
            expected[i] = i % 257 < 17 ? 0 : i % distinctCount;
        AssertOptional(expected, ParquetPhysicalType.Int64, EncodingKind.RleDictionary,
            dictionary: true);
    }

    [Test]
    public void LargeRequiredBinaryPagesBatchEveryEncoding()
    {
        foreach (var encoding in new[]
                 {
                     EncodingKind.Plain,
                     EncodingKind.RleDictionary,
                     EncodingKind.PlainDictionary,
                     EncodingKind.DeltaLengthByteArray,
                     EncodingKind.DeltaByteArray
                 })
        {
            var dictionary = encoding is EncodingKind.RleDictionary or EncodingKind.PlainDictionary;
            AssertRequiredBinary(CreateBinary(150_003), encoding, dictionary);
        }
    }

    [Test]
    public void LargeOptionalBinaryPagesBatchEveryEncoding()
    {
        foreach (var pageVersion in new[]
                 {
                     ParquetDataPageVersion.V1,
                     ParquetDataPageVersion.V2
                 })
        foreach (var encoding in new[]
                 {
                     EncodingKind.Plain,
                     EncodingKind.RleDictionary,
                     EncodingKind.PlainDictionary,
                     EncodingKind.DeltaLengthByteArray,
                     EncodingKind.DeltaByteArray
                 })
        {
            var dictionary = encoding is EncodingKind.RleDictionary or EncodingKind.PlainDictionary;
            AssertOptionalBinary(CreateOptionalBinary(150_003), encoding, dictionary, pageVersion);
        }
    }

    [Test]
    public void LargeCompressedBinaryPagesBatchEveryEncoding()
    {
        foreach (var encoding in new[]
                 {
                     EncodingKind.Plain,
                     EncodingKind.RleDictionary,
                     EncodingKind.PlainDictionary,
                     EncodingKind.DeltaLengthByteArray,
                     EncodingKind.DeltaByteArray
                 })
        {
            var dictionary = encoding is EncodingKind.RleDictionary or EncodingKind.PlainDictionary;
            AssertRequiredBinary(CreateBinary(150_003), encoding, dictionary,
                CompressionKind.Snappy);
        }
    }

    [Test]
    public void PlainBinaryPayloadBudgetStopsInOnePassAndRetainsTheBatch()
    {
        var expected = new byte[100_003][];
        for (var i = 0; i < expected.Length; i++)
        {
            var length = i < 20_000 ? 128 : 1;
            expected[i] = new byte[length];
            expected[i][^1] = (byte)i;
        }

        var schema = CreateSchema(ParquetPhysicalType.ByteArray, EncodingKind.Plain,
            optional: false, dictionary: false);
        var file = Write(schema, expected);
        using var source = new MemoryReadSource(file);
        using var reader = schema.CreateReader(source);
        var buffers = reader.RowGroups[0].Column<byte>(0).GetEnumerator();
        try
        {
            if (!buffers.MoveNext())
                throw new InvalidOperationException("Expected a plain binary batch.");
            var first = buffers.Current;
            if (first.Count <= 0 || first.Count >= expected.Length)
                throw new InvalidOperationException(
                    $"Expected a partial plain binary batch, got {first.Count} values.");
            var decodedBytes = checked(first.Count * Unsafe.SizeOf<BinaryValueDescriptor>());
            for (var i = 0; i < first.Count; i++)
                decodedBytes = checked(decodedBytes + expected[i].Length);
            if (decodedBytes > ColumnChunkReader.DecodeBatchSizeBytes)
                throw new InvalidOperationException(
                    $"Plain binary batch used {decodedBytes} decoded bytes, expected at most " +
                    $"{ColumnChunkReader.DecodeBatchSizeBytes}.");
            using var retained = first.Retain();
            if (!buffers.MoveNext())
                throw new InvalidOperationException("Expected a second plain binary batch.");
            for (var i = 0; i < first.Count; i++)
                if (!first.GetValue(i).SequenceEqual(expected[i]))
                    throw new InvalidOperationException($"Retained plain binary value {i} differs.");
        }
        finally
        {
            buffers.Dispose();
        }
    }

    [Test]
    public void LargeFixedLengthByteStreamSplitPagesBatch()
    {
        var expected = CreateFixedBinary(100_003, 16);
        var requiredSchema = CreateSchema(ParquetPhysicalType.FixedLenByteArray,
            EncodingKind.ByteStreamSplit, optional: false, dictionary: false, typeLength: 16);
        AssertBinary(requiredSchema, Write(requiredSchema, expected), expected,
            EncodingKind.ByteStreamSplit);
    }

    static void AssertRequired<T>(T[] expected, ParquetPhysicalType physicalType,
        EncodingKind encoding, bool dictionary = false)
        where T : struct
    {
        var schema = CreateSchema(physicalType, encoding, optional: false, dictionary);
        var file = Write(schema, expected);
        using var reader = schema.CreateReader(new MemoryReadSource(file));
        var actual = new List<T>(expected.Length);
        var buffers = 0;
        foreach (var buffer in reader.RowGroups[0].Column<T>(0))
        {
            buffers++;
            actual.AddRange(buffer.Values);
        }
        if (buffers <= 1)
            throw new InvalidOperationException($"{encoding} required page was not batched.");
        if (!actual.ToArray().AsSpan().SequenceEqual(expected))
            throw new InvalidOperationException($"{encoding} required values differ.");
    }

    static void AssertOptional<T>(T?[] expected, ParquetPhysicalType physicalType,
        EncodingKind encoding, bool dictionary = false,
        ParquetDataPageVersion pageVersion = ParquetDataPageVersion.V2)
        where T : struct
    {
        var schema = CreateSchema(physicalType, encoding, optional: true, dictionary);
        var file = Write(schema, expected, pageVersion);
        using var reader = schema.CreateReader(new MemoryReadSource(file));
        var actual = new List<T?>(expected.Length);
        var buffers = 0;
        foreach (var buffer in reader.RowGroups[0].Column<T?>(0))
        {
            buffers++;
            actual.AddRange(buffer.Values);
        }
        if (buffers <= 1)
            throw new InvalidOperationException($"{encoding} optional page was not batched.");
        if (!actual.ToArray().AsSpan().SequenceEqual(expected))
            throw new InvalidOperationException($"{encoding} optional values differ.");
    }

    static void AssertRequiredBinary(byte[][] expected, EncodingKind encoding, bool dictionary,
        CompressionKind compression = CompressionKind.None)
    {
        var schema = CreateSchema(ParquetPhysicalType.ByteArray, encoding,
            optional: false, dictionary);
        var file = Write(schema, expected, compression: compression);
        AssertBinary(schema, file, expected, encoding);
    }

    static void AssertOptionalBinary(byte[]?[] expected, EncodingKind encoding, bool dictionary,
        ParquetDataPageVersion pageVersion = ParquetDataPageVersion.V2)
    {
        var schema = CreateSchema(ParquetPhysicalType.ByteArray, encoding,
            optional: true, dictionary);
        var file = Write(schema, expected, pageVersion);
        AssertBinary(schema, file, expected, encoding);
    }

    static void AssertBinary(ParquetSchema schema, byte[] file, byte[]?[] expected,
        EncodingKind encoding)
    {
        using var reader = schema.CreateReader(new MemoryReadSource(file));
        var actual = new List<byte[]?>(expected.Length);
        var buffers = 0;
        foreach (var buffer in reader.RowGroups[0].Column<byte>(0))
        {
            buffers++;
            for (var i = 0; i < buffer.Count; i++)
                actual.Add(buffer.IsNull(i) ? null : buffer.GetValue(i).ToArray());
        }
        if (buffers <= 1)
            throw new InvalidOperationException($"{encoding} binary page was not batched.");
        if (actual.Count != expected.Length)
            throw new InvalidOperationException(
                $"{encoding} binary count is {actual.Count}, expected {expected.Length}.");
        for (var i = 0; i < expected.Length; i++)
            if (expected[i] is null ? actual[i] is not null :
                actual[i] is null || !actual[i]!.AsSpan().SequenceEqual(expected[i]))
                throw new InvalidOperationException($"{encoding} binary value {i} differs.");
    }

    static byte[] Write<T>(ParquetSchema schema, T[] values,
        ParquetDataPageVersion pageVersion = ParquetDataPageVersion.V2,
        CompressionKind compression = CompressionKind.None)
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
        serialized.Serialize(values);
        writer.StartRowGroup().Write(serialized);
        writer.CloseFile();
        return stream.ToArray();
    }

    static ParquetSchema CreateSchema(ParquetPhysicalType physicalType,
        EncodingKind encoding, bool optional, bool dictionary, uint typeLength = 0)
    {
        var options = new ColumnOptions(optional
            ? ParquetRepetition.Optional
            : ParquetRepetition.Required, ImmutableArray.Create(encoding), typeLength);
        return new([
            ColumnDefinition.Leaf("value", physicalType, options,
                pageStrategy: new SinglePageStrategy(dictionary))
        ]);
    }

    static bool[] CreateBooleans(int count)
    {
        var values = new bool[count];
        for (var i = 0; i < values.Length; i++)
            values[i] = i % 5 is 1 or 2;
        return values;
    }

    static int[] CreateInt32s(int count)
    {
        var values = new int[count];
        for (var i = 0; i < values.Length; i++)
            values[i] = i % 2_047;
        return values;
    }

    static double[] CreateDoubles(int count)
    {
        var values = new double[count];
        for (var i = 0; i < values.Length; i++)
            values[i] = (i % 10_000) / 100d;
        return values;
    }

    static T?[] CreateOptional<T>(T[] values)
        where T : struct
    {
        var optional = new T?[values.Length];
        for (var i = 0; i < values.Length; i++)
            if (i % 11 != 0)
                optional[i] = values[i];
        return optional;
    }

    static byte[][] CreateBinary(int count)
    {
        var values = new byte[count][];
        for (var i = 0; i < values.Length; i++)
            values[i] = System.Text.Encoding.UTF8.GetBytes($"shared-prefix-{i % 2_047:D4}");
        return values;
    }

    static byte[]?[] CreateOptionalBinary(int count)
    {
        var source = CreateBinary(count);
        var values = new byte[]?[count];
        for (var i = 0; i < values.Length; i++)
            if (i % 11 != 0)
                values[i] = source[i];
        return values;
    }

    static byte[][] CreateFixedBinary(int count, int byteLength)
    {
        var values = new byte[count][];
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = new byte[byteLength];
            BitConverter.TryWriteBytes(values[i], i);
            values[i][^1] = (byte)(i * 31);
        }
        return values;
    }

    sealed class SinglePageStrategy(bool dictionary) : IPageStrategy
    {
        public DictionaryMode GetDictionaryMode()
            => dictionary ? DictionaryMode.Forced : DictionaryMode.Disabled;

        public bool ShouldDropDictionary(uint uniqueCount, uint totalRowCount, uint rowsSeen)
            => false;

        public uint GetNextDataPageRowCount(uint totalRowCount, uint rowsWritten)
            => totalRowCount - rowsWritten;
    }
}
