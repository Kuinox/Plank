namespace Plank.DictionaryLab.Nodes;

public sealed class TaggedLinearProbingUltraSparseTunedUnrolled4StringDictionary : IExperimentDictionary<string>
{
    public static string ExperimentName => "hash.linear.tagged.ultra-sparse.tuned.unrolled4.v1";

    public static Type? ParentExperimentType => typeof(TaggedLinearProbingUltraSparseStringDictionary.Tuned);

    public static string ExperimentDescription => "Tagged ultra-sparse tuned probing with 4-step unrolled probe loop to reduce branch overhead.";

    const float LoadFactor = 0.25f;

    int[] _slots = [];
    byte[] _tags = [];
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
            Array.Clear(_tags);
        }

        Count = 0;
    }

    public int GetOrAddIndex(string key)
    {
        if (Count >= _threshold)
            ResizeTo(_slots.Length << 1);

        var slots = _slots;
        var tags = _tags;
        var keys = _keys;
        var hash = key.GetHashCode() & int.MaxValue;
        var tag = (byte)((hash >> 24) | 0x80);
        var mask = slots.Length - 1;
        var slot = hash & mask;

        while (true)
        {
            var tag0 = tags[slot];
            if (tag0 == 0)
                return Insert(slot, key, tag, slots, tags, keys);
            if (tag0 == tag)
            {
                var existing = slots[slot] - 1;
                if (keys[existing] == key)
                    return existing;
            }

            slot = (slot + 1) & mask;
            var tag1 = tags[slot];
            if (tag1 == 0)
                return Insert(slot, key, tag, slots, tags, keys);
            if (tag1 == tag)
            {
                var existing = slots[slot] - 1;
                if (keys[existing] == key)
                    return existing;
            }

            slot = (slot + 1) & mask;
            var tag2 = tags[slot];
            if (tag2 == 0)
                return Insert(slot, key, tag, slots, tags, keys);
            if (tag2 == tag)
            {
                var existing = slots[slot] - 1;
                if (keys[existing] == key)
                    return existing;
            }

            slot = (slot + 1) & mask;
            var tag3 = tags[slot];
            if (tag3 == 0)
                return Insert(slot, key, tag, slots, tags, keys);
            if (tag3 == tag)
            {
                var existing = slots[slot] - 1;
                if (keys[existing] == key)
                    return existing;
            }

            slot = (slot + 1) & mask;
        }
    }

    int Insert(int slot, string key, byte tag, int[] slots, byte[] tags, string[] keys)
    {
        var index = Count++;
        keys[index] = key;
        slots[slot] = index + 1;
        tags[slot] = tag;
        return index;
    }

    void ResizeTo(int slotLength)
    {
        var newSlots = new int[slotLength];
        var newTags = new byte[slotLength];
        if (_keys.Length < slotLength)
            Array.Resize(ref _keys, slotLength);

        _threshold = checked((int)(slotLength * LoadFactor));
        if (Count == 0)
        {
            _slots = newSlots;
            _tags = newTags;
            return;
        }

        var mask = slotLength - 1;
        for (var i = 0; i < Count; i++)
        {
            var key = _keys[i];
            var hash = key.GetHashCode() & int.MaxValue;
            var tag = (byte)((hash >> 24) | 0x80);
            var slot = hash & mask;
            while (newTags[slot] != 0)
                slot = (slot + 1) & mask;
            newSlots[slot] = i + 1;
            newTags[slot] = tag;
        }

        _slots = newSlots;
        _tags = newTags;
    }

    static int Pow2(int value)
    {
        var size = 16;
        while (size < value)
            size <<= 1;
        return size;
    }
}
