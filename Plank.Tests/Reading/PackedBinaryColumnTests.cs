using System.Collections.Immutable;
using Plank.Reading.Logical;
using Plank.Schema;
using Plank.Writing;
using Plank.Writing.PageStrategy;

namespace Plank.Tests.Reading;

[NotInParallel]
internal sealed class PackedBinaryColumnTests
{
    [Test]
    public void BinaryValuesRoundTripFromPooledPageStorage()
    {
        var encodings = new[]
        {
            EncodingKind.Plain,
            EncodingKind.DeltaLengthByteArray,
            EncodingKind.DeltaByteArray,
            EncodingKind.RleDictionary
        };

        foreach (var encoding in encodings)
        {
            AssertByteArrayEncoding(encoding, optional: false);
            AssertByteArrayEncoding(encoding, optional: true);
        }
    }

    [Test]
    public void FixedLengthBinaryValuesRoundTripFromPooledPageStorage()
    {
        AssertFixedLengthEncoding(ParquetPhysicalType.FixedLenByteArray, EncodingKind.Plain);
        AssertFixedLengthEncoding(ParquetPhysicalType.FixedLenByteArray, EncodingKind.ByteStreamSplit);
        AssertFixedLengthEncoding(ParquetPhysicalType.FixedLenByteArray, EncodingKind.RleDictionary);
        AssertFixedLengthEncoding(ParquetPhysicalType.Int96, EncodingKind.Plain);
    }

    [Test]
    public void AllNullBinaryPagesRemainInPooledStorage()
    {
        var encodings = new[]
        {
            EncodingKind.Plain,
            EncodingKind.DeltaLengthByteArray,
            EncodingKind.DeltaByteArray,
            EncodingKind.RleDictionary
        };
        foreach (var encoding in encodings)
        {
            var schema = new ParquetSchema([
                ColumnDefinition.OptionalLeaf("Value", ParquetPhysicalType.ByteArray,
                    new ColumnOptions(encodings: ImmutableArray.Create(encoding)),
                    pageStrategy: encoding == EncodingKind.RleDictionary
                        ? ForceDictionaryPageStrategy.Shared
                        : null)
            ]);
            AssertRetainedValues(schema, new byte[]?[] { null, null, null, null });
        }
    }

    static void AssertByteArrayEncoding(EncodingKind encoding, bool optional)
    {
        byte[]?[] values =
        [
            "alpha"u8.ToArray(),
            optional ? null : [],
            [],
            "alphabet"u8.ToArray(),
            "beta"u8.ToArray(),
            optional ? null : "gamma"u8.ToArray()
        ];
        var options = new ColumnOptions(
            optional ? ParquetRepetition.Optional : ParquetRepetition.Required,
            ImmutableArray.Create(encoding));
        var schema = new ParquetSchema([
            ColumnDefinition.Leaf("Value", ParquetPhysicalType.ByteArray, options,
                pageStrategy: encoding == EncodingKind.RleDictionary
                    ? ForceDictionaryPageStrategy.Shared
                    : null)
        ]);
        AssertRetainedValues(schema, values);
    }

    static void AssertFixedLengthEncoding(ParquetPhysicalType physicalType, EncodingKind encoding)
    {
        var valueLength = physicalType == ParquetPhysicalType.Int96 ? 0U : 4U;
        byte[]?[] values = physicalType == ParquetPhysicalType.Int96
            ?
            [
                [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12],
                [13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24],
                [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12]
            ]
            :
            [
                [1, 2, 3, 4],
                [5, 6, 7, 8],
                [1, 2, 3, 4],
                [9, 10, 11, 12]
            ];
        var schema = new ParquetSchema([
            ColumnDefinition.Leaf("Value", physicalType,
                new ColumnOptions(encodings: ImmutableArray.Create(encoding), typeLength: valueLength),
                pageStrategy: encoding == EncodingKind.RleDictionary
                    ? ForceDictionaryPageStrategy.Shared
                    : null)
        ]);
        AssertRetainedValues(schema, values);
    }

    static void AssertRetainedValues(ParquetSchema schema, byte[]?[] expected)
    {
        var path = Path.Combine(Path.GetTempPath(), $"plank-binary-value-{Guid.NewGuid():N}.parquet");
        ParquetBuffer retained = default;
        ColumnBuffer<byte> actual = default;
        try
        {
            using (var stream = File.Create(path))
            {
                var writer = schema.CreateWriter(stream);
                var column = writer.CreateSerializedColumn<byte[]>(schema.LeafColumns[0]);
                column.Serialize((byte[][])(object)expected);
                writer.StartRowGroup().Write(column);
                writer.CloseFile();
            }

            using (var input = File.OpenRead(path))
            using (var reader = schema.CreateReader(input))
            {
                var buffers = reader.RowGroups[0].Column<byte>(0).GetEnumerator();
                try
                {
                    if (!buffers.MoveNext())
                        throw new InvalidOperationException("Expected a binary value buffer.");
                    actual = buffers.Current;
                    retained = actual.Retain();
                }
                finally
                {
                    buffers.Dispose();
                }
            }

            if (actual.Count != expected.Length)
                throw new InvalidOperationException(
                    $"Expected {expected.Length} binary values but decoded {actual.Count}.");

            var payload = actual.Values;
            var payloadOffset = 0;
            for (var i = 0; i < expected.Length; i++)
            {
                if (expected[i] is null)
                    continue;
                if (!payload.Slice(payloadOffset, expected[i]!.Length).SequenceEqual(expected[i]))
                    throw new InvalidOperationException($"Binary payload at index {i} did not round-trip.");
                payloadOffset += expected[i]!.Length;
            }
            if (payloadOffset != payload.Length)
                throw new InvalidOperationException(
                    $"Expected {payloadOffset} payload bytes but decoded {payload.Length}.");

            for (var i = 0; i < actual.Count; i++)
            {
                if (expected[i] is null)
                {
                    if (!actual.IsNull(i))
                        throw new InvalidOperationException($"Binary value {i} should be null.");
                    continue;
                }

                if (actual.IsNull(i) || !actual.GetValue(i).SequenceEqual(expected[i]))
                    throw new InvalidOperationException($"Binary value {i} did not round-trip.");
            }
        }
        finally
        {
            retained.Dispose();
            File.Delete(path);
        }
    }
}
