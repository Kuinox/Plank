namespace Plank.Reading.Logical;

public readonly struct RowGroupCollection
{
    readonly ParquetReader? _reader;
    readonly int _footerVersion;

    internal RowGroupCollection(ParquetReader reader, int footerVersion)
    {
        ArgumentNullException.ThrowIfNull(reader);
        _reader = reader;
        _footerVersion = footerVersion;
    }

    public int Count
        => GetReader().GetRowGroupCount(_footerVersion);

    public RowGroup this[int index]
        => GetReader().GetRowGroup(index, _footerVersion);

    public Enumerator GetEnumerator()
        => new(this);

    ParquetReader GetReader()
        => _reader ?? throw new InvalidOperationException("The row group collection is not initialized.");

    public struct Enumerator
    {
        readonly RowGroupCollection _rowGroups;
        readonly int _count;
        int _index;

        internal Enumerator(RowGroupCollection rowGroups)
        {
            _rowGroups = rowGroups;
            _count = rowGroups.Count;
            _index = -1;
            Current = default;
        }

        public RowGroup Current { get; private set; }

        public bool MoveNext()
        {
            var index = _index + 1;
            if (index >= _count)
            {
                Current = default;
                return false;
            }

            Current = _rowGroups[index];
            _index = index;
            return true;
        }
    }
}
