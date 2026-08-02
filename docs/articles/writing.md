# Writing

The writing APIs use the same source-generated [schema](schema.md) as the logical and row read layers.

## Write split-block Bloom filters

Bloom filters are opt-in per leaf. Runtime schemas configure one with
[`ColumnOptions.BloomFilter`](xref:Plank.Schema.ColumnOptions.BloomFilter):

```csharp
ColumnDefinition id = ColumnDefinition.RequiredLeaf(
    "id",
    ParquetPhysicalType.Int32,
    new ColumnOptions(bloomFilter: new ParquetBloomFilterOptions
    {
        FalsePositiveProbability = 0.01,
        ExpectedDistinctValueCount = 100_000,
        MaximumBytes = 1024 * 1024
    }));
```

The writer emits the Parquet split-block algorithm with XXH64 hashing and no compression. When
`ExpectedDistinctValueCount` is omitted, it sizes each row group's filter from that chunk's non-null value count.
`MaximumBytes` caps the result and must be a power of two from 32 bytes through 128 MiB.

Generated schemas use the same feature through [`ParquetColumnAttribute`](xref:Plank.Schema.ParquetColumnAttribute):

```csharp
[ParquetColumn(
    BloomFilter = true,
    BloomFilterFalsePositiveProbability = 0.01,
    BloomFilterExpectedDistinctValueCount = 100_000)]
public int Id { get; set; }
```

Nulls are not inserted. Boolean columns cannot enable a Bloom filter because the Parquet Bloom-filter
specification does not define a standalone boolean hash representation.
