namespace Plank.DictionaryLab.Nodes;

public sealed class RobinHoodTaggedUtf8Dictionary : IExperimentDictionary<ReadOnlyMemory<byte>>
{
    public static string ExperimentName => "hash.robinhood.tagged.v1";

    public static Type? ParentExperimentType => typeof(RobinHoodUtf8Dictionary);

    public static string ExperimentDescription => "UTF-8 Robin Hood probing with tag fingerprint.";

    const float LoadFactor = 0.60f;

    int[] _slots = [];
    ushort[] _distance = [];
    byte[] _tags = [];
    ReadOnlyMemory<byte>[] _keys = [];
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
            Array.Clear(_tags);
        }

        Count = 0;
    }

    public int GetOrAddIndex(ReadOnlyMemory<byte> key)
    {
        if (Count >= _threshold)
            ResizeTo(_slots.Length << 1);

        var hash = ByteHashing.Hash(key.Span) & int.MaxValue;
        var tag = (byte)((hash >> 24) | 0x80);
        if (TryFindIndex(key, hash, tag, out var existingIndex))
            return existingIndex;

        var index = Count++;
        _keys[index] = key;
        InsertIndex(index, hash, tag);
        return index;
    }

    bool TryFindIndex(ReadOnlyMemory<byte> key, int hash, byte tag, out int index)
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
            if (_tags[slot] == tag && _keys[existingIndex].Span.SequenceEqual(key.Span))
            {
                index = existingIndex;
                return true;
            }

            slot = (slot + 1) & mask;
            probeDistance++;
        }
    }

    void InsertIndex(int incomingIndex, int hash, byte incomingTag)
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
                _tags[slot] = incomingTag;
                return;
            }

            var existingDistance = _distance[slot];
            if (existingDistance < probeDistance)
            {
                var displacedIndex = entry - 1;
                var displacedTag = _tags[slot];
                _slots[slot] = incomingIndex + 1;
                _distance[slot] = checked((ushort)probeDistance);
                _tags[slot] = incomingTag;
                incomingIndex = displacedIndex;
                incomingTag = displacedTag;
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
        _tags = new byte[slotLength];
        _threshold = checked((int)(slotLength * LoadFactor));
        if (oldCount == 0)
            return;

        for (var i = 0; i < oldCount; i++)
        {
            var hash = ByteHashing.Hash(oldKeys[i].Span) & int.MaxValue;
            var tag = (byte)((hash >> 24) | 0x80);
            InsertIndex(i, hash, tag);
        }
    }

    static int Pow2(int value)
    {
        var size = 16;
        while (size < value)
            size <<= 1;
        return size;
    }
}
