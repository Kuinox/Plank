namespace Plank.Reading.Physical;

public readonly struct ParquetKeyValueMetadataInfo
{
    internal readonly int KeyOffset;
    internal readonly int ValueOffset;

    internal ParquetKeyValueMetadataInfo(int keyOffset, int keyLength, int valueOffset, int valueLength,
        bool hasValue)
    {
        KeyOffset = keyOffset;
        KeyLength = keyLength;
        ValueOffset = valueOffset;
        ValueLength = valueLength;
        HasValue = hasValue;
    }

    public int KeyLength { get; }

    public int ValueLength { get; }

    public bool HasValue { get; }
}
