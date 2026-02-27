namespace Plank.DictionaryLab.Nodes;

public sealed class TaggedLinearProbingUltraSparseTunedMissBoundedUnrolled4Utf8Dictionary : IExperimentDictionary<ReadOnlyMemory<byte>>
{
    public static string ExperimentName => "hash.linear.tagged.ultra-sparse.tuned.miss-bounded.unrolled4.v1";

    public static Type? ParentExperimentType => typeof(TaggedLinearProbingUltraSparseUtf8Dictionary.Tuned.MissBounded);

    public static string ExperimentDescription => "UTF-8 tagged ultra-sparse tuned miss-bounded probing with 4-step unrolled probe loop.";

    const float LoadFactor = 0.25f;

    int[] _slots = [];
    byte[] _tags = [];
    ReadOnlyMemory<byte>[] _keys = [];
    ushort[] _maxDistances = [];
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
            Array.Clear(_maxDistances);
        }

        Count = 0;
    }

    public int GetOrAddIndex(ReadOnlyMemory<byte> key)
    {
        if (Count >= _threshold)
            ResizeTo(_slots.Length << 1);

        var slots = _slots;
        var tags = _tags;
        var keys = _keys;
        var maxDistances = _maxDistances;
        var hash = ByteHashing.Hash(key.Span) & int.MaxValue;
        var tag = (byte)((hash >> 24) | 0x80);
        var mask = slots.Length - 1;
        var home = hash & mask;
        var slot = home;
        var probeDistance = 0;
        var maxProbeDistance = maxDistances[home];

        while (probeDistance <= maxProbeDistance)
        {
            var slotTag0 = tags[slot];
            if (slotTag0 == 0)
                return Insert(home, slot, probeDistance, key, tag, slots, tags, keys, maxDistances);
            if (slotTag0 == tag)
            {
                var existing = slots[slot] - 1;
                if (keys[existing].Span.SequenceEqual(key.Span))
                    return existing;
            }

            slot = (slot + 1) & mask;
            probeDistance++;
            if (probeDistance > maxProbeDistance)
                break;

            var slotTag1 = tags[slot];
            if (slotTag1 == 0)
                return Insert(home, slot, probeDistance, key, tag, slots, tags, keys, maxDistances);
            if (slotTag1 == tag)
            {
                var existing = slots[slot] - 1;
                if (keys[existing].Span.SequenceEqual(key.Span))
                    return existing;
            }

            slot = (slot + 1) & mask;
            probeDistance++;
            if (probeDistance > maxProbeDistance)
                break;

            var slotTag2 = tags[slot];
            if (slotTag2 == 0)
                return Insert(home, slot, probeDistance, key, tag, slots, tags, keys, maxDistances);
            if (slotTag2 == tag)
            {
                var existing = slots[slot] - 1;
                if (keys[existing].Span.SequenceEqual(key.Span))
                    return existing;
            }

            slot = (slot + 1) & mask;
            probeDistance++;
            if (probeDistance > maxProbeDistance)
                break;

            var slotTag3 = tags[slot];
            if (slotTag3 == 0)
                return Insert(home, slot, probeDistance, key, tag, slots, tags, keys, maxDistances);
            if (slotTag3 == tag)
            {
                var existing = slots[slot] - 1;
                if (keys[existing].Span.SequenceEqual(key.Span))
                    return existing;
            }

            slot = (slot + 1) & mask;
            probeDistance++;
        }

        while (true)
        {
            if (tags[slot] == 0)
                return Insert(home, slot, probeDistance, key, tag, slots, tags, keys, maxDistances);

            slot = (slot + 1) & mask;
            probeDistance++;
        }
    }

    int Insert(int home, int slot, int probeDistance, ReadOnlyMemory<byte> key, byte tag, int[] slots, byte[] tags, ReadOnlyMemory<byte>[] keys, ushort[] maxDistances)
    {
        var index = Count++;
        keys[index] = key;
        slots[slot] = index + 1;
        tags[slot] = tag;
        if (probeDistance > maxDistances[home])
            maxDistances[home] = checked((ushort)probeDistance);
        return index;
    }

    void ResizeTo(int slotLength)
    {
        var newSlots = new int[slotLength];
        var newTags = new byte[slotLength];
        var newMaxDistances = new ushort[slotLength];
        if (_keys.Length < slotLength)
            Array.Resize(ref _keys, slotLength);

        _threshold = checked((int)(slotLength * LoadFactor));
        if (Count == 0)
        {
            _slots = newSlots;
            _tags = newTags;
            _maxDistances = newMaxDistances;
            return;
        }

        var mask = slotLength - 1;
        for (var i = 0; i < Count; i++)
        {
            var key = _keys[i];
            var hash = ByteHashing.Hash(key.Span) & int.MaxValue;
            var tag = (byte)((hash >> 24) | 0x80);
            var home = hash & mask;
            var slot = home;
            var probeDistance = 0;
            while (newTags[slot] != 0)
            {
                slot = (slot + 1) & mask;
                probeDistance++;
            }

            newSlots[slot] = i + 1;
            newTags[slot] = tag;
            if (probeDistance > newMaxDistances[home])
                newMaxDistances[home] = checked((ushort)probeDistance);
        }

        _slots = newSlots;
        _tags = newTags;
        _maxDistances = newMaxDistances;
    }

    static int Pow2(int value)
    {
        var size = 16;
        while (size < value)
            size <<= 1;
        return size;
    }
}
