using System.Collections;

namespace Plank.Untyped;

sealed class UntypedMap
{
    internal sealed class Entry
    {
        internal object? Key;
        internal object? Value;
    }

    internal List<Entry> Entries { get; } = [];

    internal static UntypedMap FromObject(object value, Func<object?, object?> keyConverter,
        Func<object?, object?> valueConverter)
    {
        var result = new UntypedMap();
        if (value is IDictionary dictionary)
        {
            foreach (DictionaryEntry entry in dictionary)
                result.Entries.Add(new Entry
                {
                    Key = keyConverter(entry.Key),
                    Value = valueConverter(entry.Value)
                });
            return result;
        }

        if (value is IEnumerable<KeyValuePair<object, object?>> entries)
        {
            foreach (var entry in entries)
                result.Entries.Add(new Entry
                {
                    Key = keyConverter(entry.Key),
                    Value = valueConverter(entry.Value)
                });
            return result;
        }

        throw new ArgumentException("Map values must implement IDictionary or IEnumerable<KeyValuePair<object, object?>>.");
    }
}
