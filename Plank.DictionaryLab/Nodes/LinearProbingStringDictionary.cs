namespace Plank.DictionaryLab.Nodes;

public sealed class LinearProbingStringDictionary : IExperimentDictionary<string>
{
    public static string ExperimentName => "hash.linear.v1";

    public static Type? ParentExperimentType => null;

    public static string ExperimentDescription => "Classic linear probing over power-of-two table.";

    const float LoadFactor = 0.72f;

    int[] _slots = [];
    string[] _keys = [];
    int _threshold;

    public int Count { get; private set; }

    public void Reset(int capacity)
    {
        var minimumCapacity = Math.Max(16, checked((int)(capacity / LoadFactor) + 1));
        if (_slots.Length < minimumCapacity)
            ResizeTo(Pow2(minimumCapacity));
        else
            Array.Clear(_slots);

        Count = 0;
    }

    public int GetOrAddIndex(string key)
    {
        if (Count >= _threshold)
            ResizeTo(_slots.Length << 1);

        var hash = key.GetHashCode() & int.MaxValue;
        var mask = _slots.Length - 1;
        var slot = hash & mask;

        while (true)
        {
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

            slot = (slot + 1) & mask;
        }
    }

    void ResizeTo(int slotLength)
    {
        var newSlots = new int[slotLength];
        if (_keys.Length < slotLength)
            Array.Resize(ref _keys, slotLength);

        _threshold = checked((int)(slotLength * LoadFactor));
        if (Count == 0)
        {
            _slots = newSlots;
            return;
        }

        var mask = slotLength - 1;
        for (var i = 0; i < Count; i++)
        {
            var key = _keys[i];
            var hash = key.GetHashCode() & int.MaxValue;
            var slot = hash & mask;
            while (newSlots[slot] != 0)
                slot = (slot + 1) & mask;
            newSlots[slot] = i + 1;
        }

        _slots = newSlots;
    }

    static int Pow2(int value)
    {
        var size = 16;
        while (size < value)
            size <<= 1;
        return size;
    }
}
