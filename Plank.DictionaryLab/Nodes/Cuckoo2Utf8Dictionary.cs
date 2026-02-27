namespace Plank.DictionaryLab.Nodes;

public sealed class Cuckoo2Utf8Dictionary : IExperimentDictionary<ReadOnlyMemory<byte>>
{
    public static string ExperimentName => "hash.cuckoo2.v1";

    public static Type? ParentExperimentType => null;

    public static string ExperimentDescription => "UTF-8 two-choice cuckoo hashing with bounded eviction and resize fallback.";

    const float LoadFactor = 0.86f;
    const int MaxKicks = 64;

    int[] _slotsA = [];
    int[] _slotsB = [];
    ReadOnlyMemory<byte>[] _keys = [];
    int _mask;
    int _threshold;

    public int Count { get; private set; }

    public void Reset(int capacity)
    {
        var minimumCapacity = Math.Max(16, checked((int)(capacity / LoadFactor) + 1));
        if (_slotsA.Length < minimumCapacity)
            ResizeTo(Pow2(minimumCapacity));
        else
        {
            Array.Clear(_slotsA);
            Array.Clear(_slotsB);
        }

        Count = 0;
    }

    public int GetOrAddIndex(ReadOnlyMemory<byte> key)
    {
        if (Count >= _threshold)
            ResizeTo(_slotsA.Length << 1);

        var hash1 = ByteHashing.Hash(key.Span) & int.MaxValue;
        var hash2 = Mix(hash1);
        var slotA = hash1 & _mask;
        var slotB = hash2 & _mask;

        var entryA = _slotsA[slotA];
        if (entryA != 0 && _keys[entryA - 1].Span.SequenceEqual(key.Span))
            return entryA - 1;

        var entryB = _slotsB[slotB];
        if (entryB != 0 && _keys[entryB - 1].Span.SequenceEqual(key.Span))
            return entryB - 1;

        var index = Count++;
        _keys[index] = key;
        if (entryA == 0)
        {
            _slotsA[slotA] = index + 1;
            return index;
        }

        if (entryB == 0)
        {
            _slotsB[slotB] = index + 1;
            return index;
        }

        if (InsertWithKick(index + 1))
            return index;

        ResizeTo(_slotsA.Length << 1);
        return index;
    }

    bool InsertWithKick(int slotValue)
    {
        var useFirst = true;
        for (var kick = 0; kick < MaxKicks; kick++)
        {
            var key = _keys[slotValue - 1];
            var hash1 = ByteHashing.Hash(key.Span) & int.MaxValue;
            var hash2 = Mix(hash1);
            if (useFirst)
            {
                var slot = hash1 & _mask;
                var displaced = _slotsA[slot];
                _slotsA[slot] = slotValue;
                if (displaced == 0)
                    return true;
                slotValue = displaced;
            }
            else
            {
                var slot = hash2 & _mask;
                var displaced = _slotsB[slot];
                _slotsB[slot] = slotValue;
                if (displaced == 0)
                    return true;
                slotValue = displaced;
            }

            useFirst = !useFirst;
        }

        return false;
    }

    void ResizeTo(int capacity)
    {
        var size = Pow2(Math.Max(16, capacity));
        while (true)
        {
            var slotsA = new int[size];
            var slotsB = new int[size];
            if (_keys.Length < size)
                Array.Resize(ref _keys, size);

            _mask = size - 1;
            _threshold = checked((int)(size * LoadFactor));
            if (TryRebuild(slotsA, slotsB))
            {
                _slotsA = slotsA;
                _slotsB = slotsB;
                return;
            }

            size <<= 1;
        }
    }

    bool TryRebuild(int[] slotsA, int[] slotsB)
    {
        for (var i = 0; i < Count; i++)
        {
            var key = _keys[i];
            var hash1 = ByteHashing.Hash(key.Span) & int.MaxValue;
            var hash2 = Mix(hash1);
            var slotA = hash1 & _mask;
            if (slotsA[slotA] == 0)
            {
                slotsA[slotA] = i + 1;
                continue;
            }

            var slotB = hash2 & _mask;
            if (slotsB[slotB] == 0)
            {
                slotsB[slotB] = i + 1;
                continue;
            }

            if (!RebuildKick(slotsA, slotsB, i + 1))
                return false;
        }

        return true;
    }

    bool RebuildKick(int[] slotsA, int[] slotsB, int slotValue)
    {
        var useFirst = true;
        for (var kick = 0; kick < MaxKicks; kick++)
        {
            var key = _keys[slotValue - 1];
            var hash1 = ByteHashing.Hash(key.Span) & int.MaxValue;
            var hash2 = Mix(hash1);
            if (useFirst)
            {
                var slot = hash1 & _mask;
                var displaced = slotsA[slot];
                slotsA[slot] = slotValue;
                if (displaced == 0)
                    return true;
                slotValue = displaced;
            }
            else
            {
                var slot = hash2 & _mask;
                var displaced = slotsB[slot];
                slotsB[slot] = slotValue;
                if (displaced == 0)
                    return true;
                slotValue = displaced;
            }

            useFirst = !useFirst;
        }

        return false;
    }

    static int Mix(int hash)
    {
        unchecked
        {
            var value = (uint)hash;
            value ^= value >> 16;
            value *= 0x7FEB352Du;
            value ^= value >> 15;
            return (int)(value & int.MaxValue);
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
