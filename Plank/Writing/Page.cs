using Plank.Schema;

namespace Plank.Writing;

internal struct Page
{
    public BufferWriter Header;

    public BufferWriter Content;

    public PageKind Kind;

    public EncodingKind Encoding;

    public uint RowCount;

    public uint ValueCount;

    public uint NullCount;

    public uint RepetitionLevelsByteLength;

    public uint DefinitionLevelsByteLength;

    public uint DictionaryValueCount;

    internal ColumnStatistics Statistics;

    internal byte[]? StatisticsMinValueBuffer;

    internal byte[]? StatisticsMaxValueBuffer;

    public void ResetMetadata()
    {
        Kind = PageKind.DataV2;
        Encoding = EncodingKind.Plain;
        RowCount = 0;
        ValueCount = 0;
        NullCount = 0;
        RepetitionLevelsByteLength = 0;
        DefinitionLevelsByteLength = 0;
        DictionaryValueCount = 0;
        Statistics = default;
    }

    public void SetDataPageMetadata(uint rowCount, uint valueCount, uint nullCount, uint repetitionLevelsByteLength,
        uint definitionLevelsByteLength, EncodingKind encoding)
    {
        Kind = PageKind.DataV2;
        RowCount = rowCount;
        ValueCount = valueCount;
        NullCount = nullCount;
        RepetitionLevelsByteLength = repetitionLevelsByteLength;
        DefinitionLevelsByteLength = definitionLevelsByteLength;
        Encoding = encoding;
        DictionaryValueCount = 0;
        Statistics = default;
    }

    public void SetDictionaryPageMetadata(uint dictionaryValueCount)
    {
        Kind = PageKind.Dictionary;
        DictionaryValueCount = dictionaryValueCount;
        RowCount = 0;
        ValueCount = 0;
        NullCount = 0;
        RepetitionLevelsByteLength = 0;
        DefinitionLevelsByteLength = 0;
        Encoding = EncodingKind.Plain;
        Statistics = default;
    }
}
