using System.Buffers.Text;
using Plank.Reading.Physical;
using Plank.Schema;

namespace Plank.Reading.Internal;

static class LegacyStatistics
{
    internal static EncodedStatistics Validate(EncodedStatistics statistics, ReadOnlySpan<byte> storage,
        ParquetColumnSchemaInfo column, ReadOnlySpan<byte> createdBy)
    {
        if (!statistics.HasLegacyBounds)
            return statistics;

        var physical = column.PhysicalType;
        var logical = column.LogicalType;
        if (physical == ParquetPhysicalType.Int96 || logical.Kind is LogicalTypeKind.Interval or LogicalTypeKind.Float16)
            return statistics.WithoutLegacyBounds();

        var binary = physical is ParquetPhysicalType.ByteArray or ParquetPhysicalType.FixedLenByteArray;
        if (binary && !TrustBinaryWriter(createdBy))
            return statistics.WithoutLegacyBounds();

        // Legacy fields are signed-only. Identical legacy extrema are independent of sort order.
        var unsigned = binary && logical.Kind != LogicalTypeKind.Decimal ||
            logical.Kind == LogicalTypeKind.Integer && !logical.IsSigned;
        if (unsigned && !(statistics.HasLegacyMinimum && statistics.HasLegacyMaximum &&
            storage.Slice(statistics.MinimumOffset, statistics.MinimumLength)
                .SequenceEqual(storage.Slice(statistics.MaximumOffset, statistics.MaximumLength))))
            return statistics.WithoutLegacyBounds();

        return statistics;
    }

    static bool TrustBinaryWriter(ReadOnlySpan<byte> createdBy)
    {
        // PARQUET-251/297: old parquet-mr could write corrupt binary bounds, sometimes with no writer ID.
        if (createdBy.IsEmpty)
            return false;
        if (!createdBy.StartsWith("parquet-mr"u8) ||
            createdBy.Length > "parquet-mr".Length && createdBy["parquet-mr".Length] != (byte)' ')
            return true;
        if (!createdBy.StartsWith("parquet-mr version "u8))
            return false;
        var version = createdBy["parquet-mr version ".Length..];
        return Utf8Parser.TryParse(version, out uint major, out var end) &&
               end < version.Length && version[end] == (byte)'.' &&
               Utf8Parser.TryParse(version[(end + 1)..], out uint minor, out var minorEnd) &&
               end + 1 + minorEnd < version.Length && version[end + 1 + minorEnd] == (byte)'.' &&
               Utf8Parser.TryParse(version[(end + 2 + minorEnd)..], out uint _, out _) &&
               (major > 1 || major == 1 && minor >= 8);
    }
}
