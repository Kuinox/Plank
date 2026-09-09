using Plank.Reading;
using Plank.Schema;
using Plank.Writing;
using Plank.Writing.PageStrategy;

namespace Plank.Tests.Writer;

internal sealed class OptionalPlainTimestampTests
{
    [Test]
    [Arguments(ParquetDataPageVersion.V1)]
    [Arguments(ParquetDataPageVersion.V2)]
    public void FusedPagesMatchFallbackBytes(ParquetDataPageVersion version)
    {
        foreach (var unit in new[] { TimeUnit.Millis, TimeUnit.Micros, TimeUnit.Nanos })
        foreach (var adjusted in new[] { false, true })
        foreach (var target in new uint[] { 1, 8, 9, 16, 65, 1024 })
        foreach (var pattern in Enumerable.Range(0, 5))
        {
            var kind = adjusted ? DateTimeKind.Utc : DateTimeKind.Unspecified;
            var values = Enumerable.Range(0, pattern == 0 ? 0 : 1031)
                .Select(i => pattern == 1 || (pattern == 3 && i % 2 == 0)
                    || (pattern == 4 && i % 41 < 19)
                    ? (DateTime?)null
                    : DateTime.SpecifyKind(DateTime.UnixEpoch.AddTicks((i - 500L) * 10001), kind))
                .ToArray();
            if (pattern == 2 && unit != TimeUnit.Nanos)
            {
                values[0] = DateTime.SpecifyKind(DateTime.MinValue, kind);
                values[^1] = DateTime.SpecifyKind(DateTime.MaxValue, kind);
            }
            var fused = Write(values, unit, adjusted, target, version, false);
            var fallback = Write(values, unit, adjusted, target, version, true);
            if (!fused.AsSpan().SequenceEqual(fallback))
            {
                throw new InvalidOperationException(
                    $"Changed bytes for {version}/{unit}/{adjusted}/{target}/{pattern}.");
            }
        }
    }

    [Test]
    public void ValidationFailureAllowsRetry()
    {
        foreach (var unit in new[] { TimeUnit.Millis, TimeUnit.Micros, TimeUnit.Nanos })
        foreach (var adjusted in new[] { false, true })
        {
            var schema = CreateSchema(unit, adjusted, null);
            using var stream = new MemoryStream();
            using var writer = schema.CreateWriter(stream, new ParquetWriterOptions
            {
                Compression = CompressionKind.None, TargetDataPageSizeBytes = 9
            });
            var column = writer.CreateSerializedColumn<DateTime?>(schema.LeafColumns[0]);
            var valid = DateTime.SpecifyKind(DateTime.UnixEpoch,
                adjusted ? DateTimeKind.Utc : DateTimeKind.Unspecified);
            var invalid = DateTime.SpecifyKind(valid, adjusted ? DateTimeKind.Unspecified : DateTimeKind.Utc);
            try
            {
                column.Serialize([null, valid, invalid]);
                throw new Exception("Expected kind validation failure.");
            }
            catch (InvalidOperationException ex) when (ex.Message.StartsWith("DateTime values must have kind", StringComparison.Ordinal))
            {
            }
            if (unit == TimeUnit.Nanos)
            {
                try
                {
                    column.Serialize([valid, DateTime.SpecifyKind(DateTime.MaxValue, valid.Kind)]);
                    throw new Exception("Expected nanosecond overflow.");
                }
                catch (OverflowException)
                {
                }
            }
            column.Serialize([null, valid]);
            writer.StartRowGroup().Write(column);
            writer.CloseFile();
            using var reader = schema.CreateReader(new MemoryReadSource(stream.ToArray()));
            var actual = new List<DateTime?>();
            foreach (var buffer in reader.RowGroups[0].Column<DateTime?>(0))
                actual.AddRange(buffer.Values);
            if (!actual.SequenceEqual(new DateTime?[] { null, valid }))
                throw new InvalidOperationException("Retry retained partially encoded values.");
        }
    }

    static byte[] Write(DateTime?[] values, TimeUnit unit, bool adjusted, uint target,
        ParquetDataPageVersion version, bool fallback)
    {
        var schema = CreateSchema(unit, adjusted, fallback ? new SizedStrategy(target) : null);
        using var stream = new MemoryStream();
        using var writer = schema.CreateWriter(stream, new ParquetWriterOptions
        {
            Compression = CompressionKind.None,
            TargetDataPageSizeBytes = target,
            DataPageVersion = version,
            WritePageIndexes = true
        });
        var column = writer.CreateSerializedColumn<DateTime?>(schema.LeafColumns[0]);
        column.Serialize(values);
        writer.StartRowGroup().Write(column);
        writer.CloseFile();
        return stream.ToArray();
    }

    static ParquetSchema CreateSchema(TimeUnit unit, bool adjusted, IPageStrategy? strategy)
        => new([ColumnDefinition.OptionalLeaf("value", ParquetPhysicalType.Int64,
            options: new ColumnOptions(encodings: [EncodingKind.Plain]),
            logicalType: new LogicalType.Timestamp(unit, adjusted), pageStrategy: strategy)]);

    sealed class SizedStrategy(uint target) : IPageStrategy
    {
        public DictionaryMode GetDictionaryMode() => DictionaryMode.Disabled;
        public bool ShouldDropDictionary(uint uniqueCount, uint totalRowCount, uint rowsSeen) => false;
        public uint GetNextDataPageRowCount(uint totalRowCount, uint rowsWritten) => totalRowCount - rowsWritten;
        public bool TryGetTargetDataPageSizeBytes(out uint sizeBytes)
        {
            sizeBytes = target;
            return true;
        }
    }
}
