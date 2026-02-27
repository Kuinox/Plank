namespace Plank.DictionaryLab.Nodes;

public sealed class SortedArrayFirstUnitUtf8Dictionary : IExperimentDictionary<ReadOnlyMemory<byte>>
{
    public static string ExperimentName => "tree.sorted-array.first-unit.v1";

    public static Type? ParentExperimentType => typeof(SortedArrayUtf8Dictionary);

    public static string ExperimentDescription => "UTF-8 sorted array branch with first-byte metadata short-circuiting compare.";

    ReadOnlyMemory<byte>[] _sorted = [];
    int[] _indices = [];
    int[] _firstBytes = [];

    public int Count { get; private set; }

    public void Reset(int capacity)
    {
        if (_sorted.Length < capacity)
        {
            var newCapacity = Math.Max(16, capacity);
            _sorted = new ReadOnlyMemory<byte>[newCapacity];
            _indices = new int[newCapacity];
            _firstBytes = new int[newCapacity];
        }

        Count = 0;
    }

    public int GetOrAddIndex(ReadOnlyMemory<byte> key)
    {
        var keySpan = key.Span;
        var keyFirst = keySpan.Length == 0 ? -1 : keySpan[0];
        var low = 0;
        var high = Count - 1;

        while (low <= high)
        {
            var mid = low + ((high - low) >> 1);
            var compare = keyFirst.CompareTo(_firstBytes[mid]);
            if (compare == 0)
                compare = keySpan.SequenceCompareTo(_sorted[mid].Span);
            if (compare == 0)
                return _indices[mid];
            if (compare < 0)
                high = mid - 1;
            else
                low = mid + 1;
        }

        EnsureCapacity(Count + 1);

        if (low < Count)
        {
            Array.Copy(_sorted, low, _sorted, low + 1, Count - low);
            Array.Copy(_indices, low, _indices, low + 1, Count - low);
            Array.Copy(_firstBytes, low, _firstBytes, low + 1, Count - low);
        }

        var index = Count;
        _sorted[low] = key;
        _indices[low] = index;
        _firstBytes[low] = keyFirst;
        Count++;
        return index;
    }

    void EnsureCapacity(int target)
    {
        if (_sorted.Length >= target)
            return;

        var newCapacity = _sorted.Length == 0 ? 16 : _sorted.Length << 1;
        while (newCapacity < target)
            newCapacity <<= 1;

        Array.Resize(ref _sorted, newCapacity);
        Array.Resize(ref _indices, newCapacity);
        Array.Resize(ref _firstBytes, newCapacity);
    }
}
