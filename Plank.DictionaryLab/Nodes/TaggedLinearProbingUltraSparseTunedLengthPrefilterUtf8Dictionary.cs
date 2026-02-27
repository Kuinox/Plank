namespace Plank.DictionaryLab.Nodes;

public sealed class TaggedLinearProbingUltraSparseTunedLengthPrefilterUtf8Dictionary : IExperimentDictionary<ReadOnlyMemory<byte>>
{
    public static string ExperimentName => "hash.linear.tagged.ultra-sparse.tuned.length-prefilter.v1";

    public static Type? ParentExperimentType => typeof(TaggedLinearProbingUltraSparseUtf8Dictionary.Tuned);

    public static string ExperimentDescription => "UTF-8 tagged ultra-sparse tuned probing with per-slot length prefilter before full sequence equality.";

    const float LoadFactor = 0.25f;

    int[] _slots = [];
    byte[] _tags = [];
    int[] _lengths = [];
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
            Array.Clear(_tags);
            Array.Clear(_lengths);
        }

        Count = 0;
    }

    public int GetOrAddIndex(ReadOnlyMemory<byte> key)
    {
        if (Count >= _threshold)
            ResizeTo(_slots.Length << 1);

        var slots = _slots;
        var tags = _tags;
        var lengths = _lengths;
        var keys = _keys;
        var keyLength = key.Length;
        var hash = ByteHashing.Hash(key.Span) & int.MaxValue;
        var tag = (byte)((hash >> 24) | 0x80);
        var mask = slots.Length - 1;
        var slot = hash & mask;

        while (true)
        {
            var slotTag = tags[slot];
            if (slotTag == 0)
            {
                var index = Count++;
                keys[index] = key;
                slots[slot] = index + 1;
                tags[slot] = tag;
                lengths[slot] = keyLength;
                return index;
            }

            if (slotTag == tag && lengths[slot] == keyLength)
            {
                var existingIndex = slots[slot] - 1;
                if (keys[existingIndex].Span.SequenceEqual(key.Span))
                    return existingIndex;
            }

            slot = (slot + 1) & mask;
        }
    }

    void ResizeTo(int slotLength)
    {
        var newSlots = new int[slotLength];
        var newTags = new byte[slotLength];
        var newLengths = new int[slotLength];
        if (_keys.Length < slotLength)
            Array.Resize(ref _keys, slotLength);

        _threshold = checked((int)(slotLength * LoadFactor));
        if (Count == 0)
        {
            _slots = newSlots;
            _tags = newTags;
            _lengths = newLengths;
            return;
        }

        var mask = slotLength - 1;
        for (var i = 0; i < Count; i++)
        {
            var key = _keys[i];
            var hash = ByteHashing.Hash(key.Span) & int.MaxValue;
            var tag = (byte)((hash >> 24) | 0x80);
            var slot = hash & mask;
            while (newTags[slot] != 0)
                slot = (slot + 1) & mask;
            newSlots[slot] = i + 1;
            newTags[slot] = tag;
            newLengths[slot] = key.Length;
        }

        _slots = newSlots;
        _tags = newTags;
        _lengths = newLengths;
    }

    static int Pow2(int value)
    {
        var size = 16;
        while (size < value)
            size <<= 1;
        return size;
    }
}
