using Plank.Schema;

namespace Plank.Writing;

internal struct Page
{
    public BufferWriter Header;

    public BufferWriter Content;

    public PageKind Kind;

    public EncodingKind Encoding;

    public int RowCount;

    public int ValueCount;

    public int NullCount;

    public int RepetitionLevelsByteLength;

    public int DefinitionLevelsByteLength;

    public int DictionaryValueCount;

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
    }

    public void SetDataPageMetadata(int rowCount, int valueCount, int nullCount, int repetitionLevelsByteLength,
        int definitionLevelsByteLength, EncodingKind encoding)
    {
        Kind = PageKind.DataV2;
        RowCount = rowCount;
        ValueCount = valueCount;
        NullCount = nullCount;
        RepetitionLevelsByteLength = repetitionLevelsByteLength;
        DefinitionLevelsByteLength = definitionLevelsByteLength;
        Encoding = encoding;
        DictionaryValueCount = 0;
    }

    public void SetDictionaryPageMetadata(int dictionaryValueCount)
    {
        Kind = PageKind.Dictionary;
        DictionaryValueCount = dictionaryValueCount;
        RowCount = 0;
        ValueCount = 0;
        NullCount = 0;
        RepetitionLevelsByteLength = 0;
        DefinitionLevelsByteLength = 0;
        Encoding = EncodingKind.Plain;
    }
}
