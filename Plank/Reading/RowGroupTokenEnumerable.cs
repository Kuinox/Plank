namespace Plank.Reading;

public readonly struct RowGroupTokenEnumerable
{
    readonly ParquetReader _reader;

    internal RowGroupTokenEnumerable(ParquetReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        _reader = reader;
    }

    public Enumerator GetEnumerator()
        => new(_reader);

    public struct Enumerator
    {
        readonly ParquetReader _reader;
        long _cursor;

        internal Enumerator(ParquetReader reader)
        {
            ArgumentNullException.ThrowIfNull(reader);

            _reader = reader;
            _cursor = 0;
            Current = default;
        }

        public RowGroupToken Current { get; private set; }

        public bool MoveNext()
        {
            if (!_reader.TryReadNextRowGroupToken(ref _cursor, out var token))
                return false;

            Current = token;
            return true;
        }
    }
}
