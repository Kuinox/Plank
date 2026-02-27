namespace Plank.DictionaryLab.Nodes;

public sealed class TaggedSparseTouchedUtf8Dictionary : IExperimentDictionary<ReadOnlyMemory<byte>>
{
    public static string ExperimentName => "hash.linear.tagged.sparse.touched.v1";

    public static Type? ParentExperimentType => typeof(TaggedLinearProbingSparseUtf8Dictionary);

    public static string ExperimentDescription => "UTF-8 tagged sparse probing with touched reset.";

    const float LoadFactor = 0.35f;

    int[] _slots = [];
    byte[] _tags = [];
    int[] _touchedSlots = [];
    ReadOnlyMemory<byte>[] _keys = [];
    int _threshold;
    int _touchedCount;

    public int Count { get; private set; }

    public void Reset(int capacity)
    {
        var minimumCapacity = Math.Max(16, checked((int)(capacity / LoadFactor) + 1));
        if (_slots.Length < minimumCapacity)
            ResizeTo(Pow2(minimumCapacity));
        else
            ClearTouchedSlots();

        Count = 0;
    }

    public int GetOrAddIndex(ReadOnlyMemory<byte> key)
    {
        if (Count >= _threshold)
            ResizeTo(_slots.Length << 1);

        var hash = ByteHashing.Hash(key.Span) & int.MaxValue;
        var tag = (byte)((hash >> 24) | 0x80);
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
                _tags[slot] = tag;
                _touchedSlots[_touchedCount++] = slot;
                return index;
            }

            var existingIndex = entry - 1;
            if (_tags[slot] == tag && _keys[existingIndex].Span.SequenceEqual(key.Span))
                return existingIndex;

            slot = (slot + 1) & mask;
        }
    }

    void ClearTouchedSlots()
    {
        if (_touchedCount * 4 >= _slots.Length)
        {
            Array.Clear(_slots);
            Array.Clear(_tags);
            _touchedCount = 0;
            return;
        }

        for (var i = 0; i < _touchedCount; i++)
        {
            var slot = _touchedSlots[i];
            _slots[slot] = 0;
            _tags[slot] = 0;
        }
        _touchedCount = 0;
    }

    void ResizeTo(int slotLength)
    {
        var newSlots = new int[slotLength];
        var newTags = new byte[slotLength];
        if (_keys.Length < slotLength)
            Array.Resize(ref _keys, slotLength);
        if (_touchedSlots.Length < slotLength)
            Array.Resize(ref _touchedSlots, slotLength);

        _threshold = checked((int)(slotLength * LoadFactor));
        _touchedCount = 0;
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
            var hash = ByteHashing.Hash(key.Span) & int.MaxValue;
            var tag = (byte)((hash >> 24) | 0x80);
            var slot = hash & mask;
            while (newSlots[slot] != 0)
                slot = (slot + 1) & mask;
            newSlots[slot] = i + 1;
            newTags[slot] = tag;
            _touchedSlots[_touchedCount++] = slot;
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
