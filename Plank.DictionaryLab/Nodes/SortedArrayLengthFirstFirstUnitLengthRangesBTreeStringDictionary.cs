namespace Plank.DictionaryLab.Nodes;

public sealed class SortedArrayLengthFirstFirstUnitLengthRangesBTreeStringDictionary : IExperimentDictionary<string>
{
    public static readonly string Name = "tree.sorted-array.length-first.first-unit.length-ranges.btree.v1";

    public static readonly Type? Parent = typeof(SortedArrayLengthFirstFirstUnitLengthRangesStringDictionary);

    public static readonly string Description = "Sorted array length-first+first-char ranges with active-length indexing to reduce insert-path range maintenance.";

    public static string ExperimentName => Name;

    public static Type? ParentExperimentType => Parent;

    public static string ExperimentDescription => Description;

    string[] _sorted = [];
    int[] _indices = [];
    int[] _firstChars = [];

    int[] _lengthStarts = [];
    int[] _lengthCounts = [];
    readonly List<int> _activeLengths = [];

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

        for (var i = 0; i < _activeLengths.Count; i++)
        {
            var length = _activeLengths[i];
            _lengthStarts[length] = 0;
            _lengthCounts[length] = 0;
        }

        _activeLengths.Clear();
        Count = 0;
    }

    public int GetOrAddIndex(string key)
    {
        var keyLength = key.Length;
        var keyFirst = keyLength == 0 ? -1 : key[0];
        EnsureLengthTrackingCapacity(keyLength);

        int low;
        if (TryGetLengthRange(keyLength, out var rangeLow, out var rangeHigh))
        {
            var firstLow = LowerBoundFirstChar(rangeLow, rangeHigh + 1, keyFirst);
            var firstHighExclusive = UpperBoundFirstChar(firstLow, rangeHigh + 1, keyFirst);
            if (firstLow < firstHighExclusive)
            {
                var keyIndex = BinarySearchKey(firstLow, firstHighExclusive - 1, key);
                if (keyIndex >= 0)
                    return _indices[keyIndex];

                low = ~keyIndex;
            }
            else
            {
                low = firstLow;
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

    int LowerBoundFirstChar(int low, int highExclusive, int keyFirst)
    {
        while (low < highExclusive)
        {
            var mid = low + ((highExclusive - low) >> 1);
            if (_firstChars[mid] < keyFirst)
                low = mid + 1;
            else
                highExclusive = mid;
        }

        return low;
    }

    int UpperBoundFirstChar(int low, int highExclusive, int keyFirst)
    {
        while (low < highExclusive)
        {
            var mid = low + ((highExclusive - low) >> 1);
            if (_firstChars[mid] <= keyFirst)
                low = mid + 1;
            else
                highExclusive = mid;
        }

        return low;
    }

    int BinarySearchKey(int low, int high, string key)
    {
        while (low <= high)
        {
            var mid = low + ((high - low) >> 1);
            var compare = string.CompareOrdinal(key, _sorted[mid]);
            if (compare == 0)
                return mid;
            if (compare < 0)
                high = mid - 1;
            else
                low = mid + 1;
        }

        return ~low;
    }

    void InsertLengthPosition(int keyLength, int insertAt)
    {
        var activeIndex = _activeLengths.BinarySearch(keyLength);
        if (activeIndex < 0)
        {
            activeIndex = ~activeIndex;
            _activeLengths.Insert(activeIndex, keyLength);
            _lengthStarts[keyLength] = insertAt;
        }

        _lengthCounts[keyLength]++;

        for (var i = activeIndex + 1; i < _activeLengths.Count; i++)
            _lengthStarts[_activeLengths[i]]++;
    }

    bool TryGetLengthRange(int keyLength, out int low, out int high)
    {
        if (keyLength >= _lengthCounts.Length || _lengthCounts[keyLength] == 0)
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
        var nextActive = _activeLengths.BinarySearch(keyLength);
        if (nextActive >= 0)
            return _lengthStarts[keyLength];

        nextActive = ~nextActive;
        if (nextActive < _activeLengths.Count)
            return _lengthStarts[_activeLengths[nextActive]];

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
