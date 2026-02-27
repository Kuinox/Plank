namespace Plank.DictionaryLab.Nodes;

public sealed class RobinHoodStringDictionary : IExperimentDictionary<string>
{
    public static string ExperimentName => "hash.robinhood.v1";

    public static Type? ParentExperimentType => typeof(LinearProbingStringDictionary);

    public static string ExperimentDescription => "Robin Hood probing baseline.";

    const float LoadFactor = 0.80f;

    int[] _slots = [];
    ushort[] _distance = [];
    string[] _keys = [];
    int _threshold;

    public int Count { get; private set; }

    public void Reset(int capacity)
    {
        var minimumCapacity = Math.Max(16, checked((int)(capacity / LoadFactor) + 1));
        if (_slots.Length < minimumCapacity)
            ResizeTo(Pow2(minimumCapacity));
        else
        {
            Array.Clear(_slots);
            Array.Clear(_distance);
        }

        Count = 0;
    }

    public int GetOrAddIndex(string key)
    {
        if (Count >= _threshold)
            ResizeTo(_slots.Length << 1);

        var hash = key.GetHashCode() & int.MaxValue;
        if (TryFindIndex(key, hash, out var existingIndex))
            return existingIndex;

        var index = Count++;
        _keys[index] = key;
        InsertIndex(index, hash);
        return index;
    }

    bool TryFindIndex(string key, int hash, out int index)
    {
        var mask = _slots.Length - 1;
        var slot = hash & mask;
        var probeDistance = 0;

        while (true)
        {
            var entry = _slots[slot];
            if (entry == 0)
            {
                index = -1;
                return false;
            }

            var existingDistance = _distance[slot];
            if (existingDistance < probeDistance)
            {
                index = -1;
                return false;
            }

            var existingIndex = entry - 1;
            if (_keys[existingIndex] == key)
            {
                index = existingIndex;
                return true;
            }

            slot = (slot + 1) & mask;
            probeDistance++;
        }
    }

    void InsertIndex(int incomingIndex, int hash)
    {
        var mask = _slots.Length - 1;
        var slot = hash & mask;
        var probeDistance = 0;

        while (true)
        {
            var entry = _slots[slot];
            if (entry == 0)
            {
                _slots[slot] = incomingIndex + 1;
                _distance[slot] = checked((ushort)probeDistance);
                return;
            }

            var existingDistance = _distance[slot];
            if (existingDistance < probeDistance)
            {
                var displacedIndex = entry - 1;
                _slots[slot] = incomingIndex + 1;
                _distance[slot] = checked((ushort)probeDistance);
                incomingIndex = displacedIndex;
                probeDistance = existingDistance;
            }

            slot = (slot + 1) & mask;
            probeDistance++;
        }
    }

    void ResizeTo(int slotLength)
    {
        var oldKeys = _keys;
        var oldCount = Count;
        if (_keys.Length < slotLength)
            Array.Resize(ref _keys, slotLength);

        _slots = new int[slotLength];
        _distance = new ushort[slotLength];
        _threshold = checked((int)(slotLength * LoadFactor));
        if (oldCount == 0)
            return;

        for (var i = 0; i < oldCount; i++)
            InsertIndex(i, oldKeys[i].GetHashCode() & int.MaxValue);
    }

    static int Pow2(int value)
    {
        var size = 16;
        while (size < value)
            size <<= 1;
        return size;
    }
}
