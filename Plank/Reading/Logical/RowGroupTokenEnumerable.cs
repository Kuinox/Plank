namespace Plank.Reading.Logical;

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
        int _ordinal;

        internal Enumerator(ParquetReader reader)
        {
            ArgumentNullException.ThrowIfNull(reader);

            _reader = reader;
            _ordinal = 0;
            Current = default;
        }

        public RowGroupToken Current { get; private set; }

        public bool MoveNext()
        {
            if (!_reader.TryReadNextRowGroupToken(_ordinal, out var token))
                return false;

            Current = token;
            _ordinal++;
            return true;
        }
    }
}
