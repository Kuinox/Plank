using System.Numerics;

namespace Plank.Schema;

/// <summary>Configures a split-block Bloom filter for one column chunk.</summary>
public sealed record ParquetBloomFilterOptions
{
    /// <summary>The Parquet-recommended default Bloom-filter configuration.</summary>
    public static readonly ParquetBloomFilterOptions Default = new();

    /// <summary>Gets the target probability that a missing value is reported as possibly present.</summary>
    public double FalsePositiveProbability { get; init; } = 0.01;

    /// <summary>
    /// Gets an optional estimate of the number of distinct non-null values in each row group.
    /// </summary>
    /// <remarks>
    /// When omitted, the writer uses the non-null value count. Supplying a good estimate can reduce the filter size
    /// for columns containing many duplicate values.
    /// </remarks>
    public uint? ExpectedDistinctValueCount { get; init; }

    /// <summary>Gets the maximum number of bytes used by a column chunk's Bloom-filter bitset.</summary>
    /// <remarks>The value must be a power of two between 32 bytes and 128 MiB, inclusive.</remarks>
    public uint MaximumBytes { get; init; } = MaximumSupportedBytes;

    internal const uint MinimumBytes = 32;
    internal const uint MaximumSupportedBytes = 128 * 1024 * 1024;

    internal void Validate()
    {
        if (!double.IsFinite(FalsePositiveProbability) || FalsePositiveProbability is <= 0 or >= 1)
            throw new ArgumentOutOfRangeException(nameof(FalsePositiveProbability), FalsePositiveProbability,
                "False-positive probability must be finite and greater than zero and less than one.");
        if (ExpectedDistinctValueCount == 0)
            throw new ArgumentOutOfRangeException(nameof(ExpectedDistinctValueCount), ExpectedDistinctValueCount,
                "Expected distinct value count must be greater than zero when specified.");
        if (MaximumBytes is < MinimumBytes or > MaximumSupportedBytes || !BitOperations.IsPow2(MaximumBytes))
            throw new ArgumentOutOfRangeException(nameof(MaximumBytes), MaximumBytes,
                $"Maximum bytes must be a power of two between {MinimumBytes} and {MaximumSupportedBytes}.");
    }

    internal uint GetBitsetSize(uint valueCount)
    {
        var distinctValueCount = ExpectedDistinctValueCount ?? valueCount;
        if (distinctValueCount == 0)
            return MinimumBytes;

        var bitCount = -8d * distinctValueCount /
                       Math.Log(1d - Math.Pow(FalsePositiveProbability, 1d / 8d));
        var maximumBitCount = (double)MaximumBytes * 8d;
        if (!double.IsFinite(bitCount) || bitCount >= maximumBitCount)
            return MaximumBytes;

        var requestedBits = checked((uint)Math.Max((double)MinimumBytes * 8d, bitCount));
        var roundedBits = BitOperations.RoundUpToPowerOf2(requestedBits);
        return roundedBits == 0 || roundedBits > maximumBitCount
            ? MaximumBytes
            : roundedBits >> 3;
    }
}
