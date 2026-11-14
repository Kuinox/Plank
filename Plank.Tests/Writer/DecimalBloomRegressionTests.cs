using Plank.BloomFilters;
using Plank.Reading;
using Plank.Reading.Logical.Internal;
using Plank.Schema;
using Plank.Writing;

namespace Plank.Tests.Writer;

internal sealed class DecimalBloomRegressionTests
{
    [Test]
    [Arguments(ParquetPhysicalType.Int32)]
    [Arguments(ParquetPhysicalType.Int64)]
    [Arguments(ParquetPhysicalType.ByteArray)]
    [Arguments(ParquetPhysicalType.FixedLenByteArray)]
    public void DecimalBloomUsesPhysicalRepresentation(ParquetPhysicalType physicalType)
    {
        using var pool = new DefaultParquetBufferPool();
        var factory = new BufferWriterFactory(pool, 4096, 4096, 4096, 4096);
        var column = new Column("value", physicalType,
            new ColumnOptions(typeLength: physicalType == ParquetPhysicalType.FixedLenByteArray ? 12U : 0U,
                bloomFilter: new ParquetBloomFilterOptions()), logicalType: new LogicalType.Decimal(9, 2));
        ParquetBuffer required = default;
        ParquetBuffer optional = default;
        try
        {
            var length = BloomFilterBuilder.Build<decimal>(factory, column, [-2.25m, 0m, 3.5m], ref required);
            var optionalLength = BloomFilterBuilder.BuildOptional<decimal>(factory, column,
                [null, -2.25m, 0m, 3.5m], ref optional);
            if (!required.Span[..length].SequenceEqual(optional.Span[..optionalLength]))
                throw new InvalidOperationException("Required and optional decimal Bloom filters differ.");
            Span<byte> bytes = stackalloc byte[12];
            foreach (var value in new[] { -225, 0, 350 })
            {
                ulong hash;
                if (physicalType == ParquetPhysicalType.Int32)
                    hash = ParquetBloomFilterHash.Hash(value);
                else if (physicalType == ParquetPhysicalType.Int64)
                    hash = ParquetBloomFilterHash.Hash((long)value);
                else
                {
                    var encoded = new System.Numerics.BigInteger(value).ToByteArray(isBigEndian: true);
                    if (physicalType == ParquetPhysicalType.FixedLenByteArray)
                    {
                        bytes.Fill(value < 0 ? byte.MaxValue : (byte)0);
                        encoded.CopyTo(bytes[^encoded.Length..]);
                        hash = ParquetBloomFilterHash.Hash(bytes);
                    }
                    else
                        hash = ParquetBloomFilterHash.Hash(encoded);
                }
                if (!SplitBlockBloomFilter.MightContainHash(required.Span[..length], hash))
                    throw new InvalidOperationException("Decimal Bloom filter produced a false negative.");
            }
        }
        finally
        {
            required.Dispose();
            optional.Dispose();
        }
    }
}
