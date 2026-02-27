namespace Plank.DictionaryLab.Nodes;

public sealed class ChainedHashStringDictionary : IExperimentDictionary<string>
{
    public static string ExperimentName => "hash.chained.v1";

    public static Type? ParentExperimentType => null;

    public static string ExperimentDescription => "String separate chaining with index-linked entry lists.";

    const int MinimumCapacity = 16;
    const int TargetEntriesPerBucket = 2;

    int[] _bucketHeads = [];
    int[] _next = [];
    string[] _keys = [];
    int _resizeThreshold;

    public int Count { get; private set; }

    public void Reset(int capacity)
    {
        var targetEntries = Math.Max(MinimumCapacity, capacity);
        var targetBuckets = Math.Max(MinimumCapacity, DivideRoundUp(targetEntries, TargetEntriesPerBucket));

        EnsureBucketCapacity(Pow2(targetBuckets));
        EnsureEntryCapacity(targetEntries);
        Array.Clear(_bucketHeads);
        Count = 0;
    }

    public int GetOrAddIndex(string key)
    {
        if (_bucketHeads.Length == 0)
            EnsureBucketCapacity(MinimumCapacity);
        if (Count >= _resizeThreshold)
            ResizeBuckets(_bucketHeads.Length << 1);

        var hash = key.GetHashCode() & int.MaxValue;
        var bucket = hash & (_bucketHeads.Length - 1);

        var entry = _bucketHeads[bucket] - 1;
        while (entry >= 0)
        {
            if (_keys[entry] == key)
                return entry;
            entry = _next[entry];
        }

        var index = Count;
        EnsureEntryCapacity(index + 1);
        Count = index + 1;
        _keys[index] = key;
        _next[index] = _bucketHeads[bucket] - 1;
        _bucketHeads[bucket] = index + 1;
        return index;
    }

    void EnsureBucketCapacity(int bucketLength)
    {
        if (_bucketHeads.Length >= bucketLength)
            return;

        _bucketHeads = new int[bucketLength];
        _resizeThreshold = checked(bucketLength * TargetEntriesPerBucket);
    }

    void EnsureEntryCapacity(int target)
    {
        if (_keys.Length >= target)
            return;

        var newCapacity = _keys.Length == 0 ? MinimumCapacity : _keys.Length << 1;
        while (newCapacity < target)
            newCapacity <<= 1;

        Array.Resize(ref _keys, newCapacity);
        Array.Resize(ref _next, newCapacity);
    }

    void ResizeBuckets(int bucketLength)
    {
        var newBuckets = new int[bucketLength];
        var mask = bucketLength - 1;

        for (var i = 0; i < Count; i++)
        {
            var bucket = (_keys[i].GetHashCode() & int.MaxValue) & mask;
            _next[i] = newBuckets[bucket] - 1;
            newBuckets[bucket] = i + 1;
        }

        _bucketHeads = newBuckets;
        _resizeThreshold = checked(bucketLength * TargetEntriesPerBucket);
    }

    static int DivideRoundUp(int numerator, int denominator)
        => (numerator + denominator - 1) / denominator;

    static int Pow2(int value)
    {
        var size = MinimumCapacity;
        while (size < value)
            size <<= 1;
        return size;
    }
}
