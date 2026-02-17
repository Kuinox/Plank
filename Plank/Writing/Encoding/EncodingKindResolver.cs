using Plank.Schema;

namespace Plank.Writing;

static class EncodingKindResolver
{
    internal static EncodingKind GetDataEncodingKind(Column column)
    {
        var encodings = column.Options.Encodings;
        if (encodings.Length == 0)
            return EncodingKind.Plain;

        for (var i = 0; i < encodings.Length; i++)
        {
            var encoding = encodings[i];
            if (encoding is EncodingKind.PlainDictionary or EncodingKind.RleDictionary)
                continue;
            return encoding;
        }

        return EncodingKind.Plain;
    }

    internal static EncodingKind GetDictionaryEncodingKind(Column column)
    {
        var encodings = column.Options.Encodings;
        for (var i = 0; i < encodings.Length; i++)
            if (encodings[i] == EncodingKind.RleDictionary)
                return EncodingKind.RleDictionary;

        for (var i = 0; i < encodings.Length; i++)
            if (encodings[i] == EncodingKind.PlainDictionary)
                return EncodingKind.PlainDictionary;

        return EncodingKind.RleDictionary;
    }
}
