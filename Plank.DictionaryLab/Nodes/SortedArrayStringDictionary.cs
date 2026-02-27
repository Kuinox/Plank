namespace Plank.DictionaryLab.Nodes;

public sealed class SortedArrayStringDictionary : IExperimentDictionary<string>
{
    public static string ExperimentName => "tree.sorted-array.v1";

    public static Type? ParentExperimentType => null;

    public static string ExperimentDescription => "Sorted array reference baseline.";

    string[] _sorted = [];
    int[] _indices = [];

    public int Count { get; private set; }

    public void Reset(int capacity)
    {
        if (_sorted.Length < capacity)
        {
            var newCapacity = Math.Max(16, capacity);
            _sorted = new string[newCapacity];
            _indices = new int[newCapacity];
        }

        Count = 0;
    }

    public int GetOrAddIndex(string key)
    {
        var low = 0;
        var high = Count - 1;

        while (low <= high)
        {
            var mid = low + ((high - low) >> 1);
            var compare = string.CompareOrdinal(_sorted[mid], key);
            if (compare == 0)
                return _indices[mid];
            if (compare < 0)
                low = mid + 1;
            else
                high = mid - 1;
        }

        EnsureCapacity(Count + 1);

        if (low < Count)
        {
            Array.Copy(_sorted, low, _sorted, low + 1, Count - low);
            Array.Copy(_indices, low, _indices, low + 1, Count - low);
        }

        var index = Count;
        _sorted[low] = key;
        _indices[low] = index;
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
    }
}
