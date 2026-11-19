namespace Plank.Reading.Internal;

readonly struct EncodedStatistics
{
    const byte HasMinimumFlag = 1;
    const byte HasMaximumFlag = 2;
    const byte HasNullCountFlag = 4;
    const byte HasDistinctCountFlag = 8;
    const byte MinimumExactFlag = 16;
    const byte MaximumExactFlag = 32;
    const byte LegacyMinimumFlag = 64;
    const byte LegacyMaximumFlag = 128;

    internal EncodedStatistics(int minimumOffset, int minimumLength, int maximumOffset, int maximumLength,
        long nullCount, long distinctCount, bool hasMinimum, bool hasMaximum, bool hasNullCount,
        bool hasDistinctCount, bool minimumExact, bool maximumExact,
        bool legacyMinimum = false, bool legacyMaximum = false)
    {
        MinimumOffset = minimumOffset;
        MinimumLength = minimumLength;
        MaximumOffset = maximumOffset;
        MaximumLength = maximumLength;
        NullCount = nullCount;
        DistinctCount = distinctCount;
        Flags = (byte)(
            (hasMinimum ? HasMinimumFlag : 0) |
            (hasMaximum ? HasMaximumFlag : 0) |
            (hasNullCount ? HasNullCountFlag : 0) |
            (hasDistinctCount ? HasDistinctCountFlag : 0) |
            (minimumExact ? MinimumExactFlag : 0) |
            (maximumExact ? MaximumExactFlag : 0) |
            (legacyMinimum ? LegacyMinimumFlag : 0) |
            (legacyMaximum ? LegacyMaximumFlag : 0));
    }

    internal int MinimumOffset { get; }

    internal int MinimumLength { get; }

    internal int MaximumOffset { get; }

    internal int MaximumLength { get; }

    internal long NullCount { get; }

    internal long DistinctCount { get; }

    internal byte Flags { get; }

    internal bool HasMinimum
        => (Flags & HasMinimumFlag) != 0;

    internal bool HasMaximum
        => (Flags & HasMaximumFlag) != 0;

    internal bool HasNullCount
        => (Flags & HasNullCountFlag) != 0;

    internal bool HasDistinctCount
        => (Flags & HasDistinctCountFlag) != 0;

    internal bool IsMinimumExact
        => HasMinimum && (Flags & MinimumExactFlag) != 0;

    internal bool IsMaximumExact
        => HasMaximum && (Flags & MaximumExactFlag) != 0;

    internal bool HasLegacyMinimum => (Flags & LegacyMinimumFlag) != 0;

    internal bool HasLegacyMaximum => (Flags & LegacyMaximumFlag) != 0;

    internal bool HasLegacyBounds => (Flags & (LegacyMinimumFlag | LegacyMaximumFlag)) != 0;

    internal EncodedStatistics WithoutLegacyBounds()
        => new(MinimumOffset, MinimumLength, MaximumOffset, MaximumLength, NullCount, DistinctCount,
            HasMinimum && !HasLegacyMinimum, HasMaximum && !HasLegacyMaximum, HasNullCount,
            HasDistinctCount, IsMinimumExact, IsMaximumExact);

    internal bool HasValues
        => HasMinimum || HasMaximum || HasNullCount || HasDistinctCount;
}
