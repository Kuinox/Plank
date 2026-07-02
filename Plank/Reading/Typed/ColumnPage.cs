using Plank.Schema;

namespace Plank.Reading.Typed;

public readonly struct ColumnPage<T>
{
    internal ColumnPage(ReadOnlyMemory<T> values, EncodingKind encoding)
    {
        Values = values;
        Encoding = encoding;
    }

    // Values may be backed by an enumerator-owned reusable buffer and are only stable until the next MoveNext().
    public ReadOnlyMemory<T> Values { get; }

    public int ValueCount
        => Values.Length;

    public EncodingKind Encoding { get; }
}
