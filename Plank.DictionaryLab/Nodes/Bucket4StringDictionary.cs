namespace Plank.DictionaryLab.Nodes;

public sealed class Bucket4StringDictionary : IExperimentDictionary<string>
{
    public static string ExperimentName => "hash.bucket4.v1";

    public static Type? ParentExperimentType => null;

    public static string ExperimentDescription => "4-slot bucketized probing with bucket-wise scans before advancing.";

    const int BucketSize = 4;
    const float LoadFactor = 0.80f;

    int[] _slots = [];
    string[] _keys = [];
    int _bucketMask;
    int _threshold;

    public int Count { get; private set; }

    public void Reset(int capacity)
    {
        var minimumSlots = Math.Max(16, checked((int)(capacity / LoadFactor) + 1));
        var minimumBuckets = Math.Max(4, (minimumSlots + BucketSize - 1) / BucketSize);
        if (_slots.Length < minimumBuckets * BucketSize)
            ResizeTo(Pow2(minimumBuckets));
        else
            Array.Clear(_slots);

        Count = 0;
    }

    public int GetOrAddIndex(string key)
    {
        if (Count >= _threshold)
            ResizeTo((_bucketMask + 1) << 1);

        var hash = key.GetHashCode() & int.MaxValue;
        var bucket = hash & _bucketMask;

        while (true)
        {
            var offset = bucket * BucketSize;
            for (var lane = 0; lane < BucketSize; lane++)
            {
                var slot = offset + lane;
                var entry = _slots[slot];
                if (entry == 0)
                {
                    var index = Count++;
                    _keys[index] = key;
                    _slots[slot] = index + 1;
                    return index;
                }

                var existingIndex = entry - 1;
                if (_keys[existingIndex] == key)
                    return existingIndex;
            }

            bucket = (bucket + 1) & _bucketMask;
        }
    }

    void ResizeTo(int bucketCount)
    {
        var newSlots = new int[bucketCount * BucketSize];
        if (_keys.Length < newSlots.Length)
            Array.Resize(ref _keys, newSlots.Length);

        _bucketMask = bucketCount - 1;
        _threshold = checked((int)(newSlots.Length * LoadFactor));
        if (Count == 0)
        {
            _slots = newSlots;
            return;
        }

        for (var i = 0; i < Count; i++)
        {
            var key = _keys[i];
            var hash = key.GetHashCode() & int.MaxValue;
            var bucket = hash & _bucketMask;
            while (true)
            {
                var offset = bucket * BucketSize;
                var placed = false;
                for (var lane = 0; lane < BucketSize; lane++)
                {
                    var slot = offset + lane;
                    if (newSlots[slot] != 0)
                        continue;
                    newSlots[slot] = i + 1;
                    placed = true;
                    break;
                }

                if (placed)
                    break;
                bucket = (bucket + 1) & _bucketMask;
            }
        }

        _slots = newSlots;
    }

    static int Pow2(int value)
    {
        var size = 4;
        while (size < value)
            size <<= 1;
        return size;
    }
}
