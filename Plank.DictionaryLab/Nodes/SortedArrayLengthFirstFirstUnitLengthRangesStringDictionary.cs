namespace Plank.DictionaryLab.Nodes;

public sealed class SortedArrayLengthFirstFirstUnitLengthRangesStringDictionary : IExperimentDictionary<string>
{
    public static readonly string Name = "tree.sorted-array.length-first.first-unit.length-ranges.v1";

    public static readonly Type? Parent = typeof(SortedArrayLengthFirstFirstUnitStringDictionary);

    public static readonly string Description = "Sorted array length-first+first-char branch with per-length ranges to narrow binary search and reduce compare work.";

    public static string ExperimentName => Name;

    public static Type? ParentExperimentType => Parent;

    public static string ExperimentDescription => Description;

    string[] _sorted = [];
    int[] _indices = [];
    int[] _firstChars = [];

    int[] _lengthStarts = [];
    int[] _lengthCounts = [];
    int _maxTrackedLength;

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

        if (_lengthCounts.Length < 16)
        {
            _lengthStarts = new int[16];
            _lengthCounts = new int[16];
        }

        for (var i = 0; i <= _maxTrackedLength; i++)
        {
            _lengthStarts[i] = 0;
            _lengthCounts[i] = 0;
        }

        _maxTrackedLength = 0;
        Count = 0;
    }

    public int GetOrAddIndex(string key)
    {
        var keyLength = key.Length;
        var keyFirst = keyLength == 0 ? -1 : key[0];
        EnsureLengthTrackingCapacity(keyLength);

        int low;
        int high;

        if (TryGetLengthRange(keyLength, out low, out high))
        {
            while (low <= high)
            {
                var mid = low + ((high - low) >> 1);
                var compare = keyFirst.CompareTo(_firstChars[mid]);
                if (compare == 0 && keyLength != 0)
                    compare = string.CompareOrdinal(key, _sorted[mid]);
                if (compare == 0)
                    return _indices[mid];
                if (compare < 0)
                    high = mid - 1;
                else
                    low = mid + 1;
            }
        }
        else
        {
            low = FindInsertPositionForMissingLength(keyLength);
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

        InsertLengthPosition(keyLength, low);
        return index;
    }

    void InsertLengthPosition(int keyLength, int insertAt)
    {
        if (_lengthCounts[keyLength] == 0)
            _lengthStarts[keyLength] = insertAt;

        _lengthCounts[keyLength]++;

        if (keyLength > _maxTrackedLength)
            _maxTrackedLength = keyLength;

        for (var length = keyLength + 1; length <= _maxTrackedLength; length++)
            if (_lengthCounts[length] != 0)
                _lengthStarts[length]++;
    }

    bool TryGetLengthRange(int keyLength, out int low, out int high)
    {
        if (keyLength > _maxTrackedLength || _lengthCounts[keyLength] == 0)
        {
            low = 0;
            high = -1;
            return false;
        }

        low = _lengthStarts[keyLength];
        high = low + _lengthCounts[keyLength] - 1;
        return true;
    }

    int FindInsertPositionForMissingLength(int keyLength)
    {
        for (var length = keyLength + 1; length <= _maxTrackedLength; length++)
            if (_lengthCounts[length] != 0)
                return _lengthStarts[length];

        return Count;
    }

    void EnsureLengthTrackingCapacity(int keyLength)
    {
        if (keyLength < _lengthCounts.Length)
            return;

        var newCapacity = _lengthCounts.Length == 0 ? 16 : _lengthCounts.Length;
        while (newCapacity <= keyLength)
            newCapacity <<= 1;

        Array.Resize(ref _lengthStarts, newCapacity);
        Array.Resize(ref _lengthCounts, newCapacity);
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
