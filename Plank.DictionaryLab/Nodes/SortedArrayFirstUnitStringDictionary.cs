namespace Plank.DictionaryLab.Nodes;

public sealed class SortedArrayFirstUnitStringDictionary : IExperimentDictionary<string>
{
    public static string ExperimentName => "tree.sorted-array.first-unit.v1";

    public static Type? ParentExperimentType => typeof(SortedArrayStringDictionary);

    public static string ExperimentDescription => "Sorted array branch with first-char metadata short-circuiting compare.";

    string[] _sorted = [];
    int[] _indices = [];
    int[] _firstChars = [];

    public int Count { get; private set; }

    public void Reset(int capacity)
    {
        if (_sorted.Length < capacity)
        {
            var newCapacity = Math.Max(16, capacity);
            _sorted = new string[newCapacity];
            _indices = new int[newCapacity];
            _firstChars = new int[newCapacity];
        }

        Count = 0;
    }

    public int GetOrAddIndex(string key)
    {
        var keyFirst = key.Length == 0 ? -1 : key[0];
        var low = 0;
        var high = Count - 1;

        while (low <= high)
        {
            var mid = low + ((high - low) >> 1);
            var compare = keyFirst.CompareTo(_firstChars[mid]);
            if (compare == 0)
                compare = string.CompareOrdinal(key, _sorted[mid]);
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
            Array.Copy(_firstChars, low, _firstChars, low + 1, Count - low);
        }

        var index = Count;
        _sorted[low] = key;
        _indices[low] = index;
        _firstChars[low] = keyFirst;
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
        Array.Resize(ref _firstChars, newCapacity);
    }
}
