namespace Plank.DictionaryLab.Nodes;

/// <summary>
/// Tag and index packed into a single uint per slot: top 8 bits = tag, low 24 bits = index+1.
/// Entry 0 means empty. This reduces the hot probe path from 3 potential cache misses (slots + tags + keys)
/// to 2 (table + keys), and the table is 20% smaller than the two-array approach (4 vs 5 bytes/slot).
/// </summary>
public sealed class PackedUltraSparseStringDictionary : IExperimentDictionary<string>
{
    public static string ExperimentName => "hash.linear.packed.ultra-sparse.v1";

    public static Type? ParentExperimentType => typeof(TaggedLinearProbingUltraSparseStringDictionary);

    public static string ExperimentDescription =>
        "Tag+index packed into a single uint per slot (top 8 bits = tag, low 24 bits = index+1), 25% load. " +
        "One array read per probe eliminates the separate tags/slots cache miss split.";

    const float LoadFactor = 0.25f;

    // Encoding: (tag << 24) | (index + 1), where tag = (hash >> 24) | 0x80. Zero = empty.
    uint[] _table = [];
    string[] _keys = [];
    int _threshold;

    public int Count { get; private set; }

    public void Reset(int capacity)
    {
        var minimumCapacity = Math.Max(16, checked((int)(capacity / LoadFactor) + 1));
        if (_table.Length < minimumCapacity)
            ResizeTo(Pow2(minimumCapacity));
        else
            Array.Clear(_table);
        Count = 0;
    }

    public int GetOrAddIndex(string key)
    {
        if (Count >= _threshold)
            ResizeTo(_table.Length << 1);

        var table = _table;
        var keys = _keys;
        var hash = key.GetHashCode() & int.MaxValue;
        var tag = (uint)((hash >> 24) | 0x80);
        var mask = table.Length - 1;
        var slot = hash & mask;

        while (true)
        {
            var entry = table[slot];
            if (entry == 0)
            {
                var index = Count++;
                keys[index] = key;
                table[slot] = (tag << 24) | (uint)(index + 1);
                return index;
            }

            if (entry >> 24 == tag)
            {
                var existingIndex = (int)(entry & 0x00FFFFFFu) - 1;
                if (keys[existingIndex] == key)
                    return existingIndex;
            }

            slot = (slot + 1) & mask;
        }
    }

    void ResizeTo(int slotLength)
    {
        var newTable = new uint[slotLength];
        if (_keys.Length < slotLength)
            Array.Resize(ref _keys, slotLength);

        _threshold = checked((int)(slotLength * LoadFactor));
        if (Count == 0)
        {
            _table = newTable;
            return;
        }

        var mask = slotLength - 1;
        for (var i = 0; i < Count; i++)
        {
            var key = _keys[i];
            var hash = key.GetHashCode() & int.MaxValue;
            var tag = (uint)((hash >> 24) | 0x80);
            var slot = hash & mask;
            while (newTable[slot] != 0)
                slot = (slot + 1) & mask;
            newTable[slot] = (tag << 24) | (uint)(i + 1);
        }

        _table = newTable;
    }

    static int Pow2(int value)
    {
        var size = 16;
        while (size < value)
            size <<= 1;
        return size;
    }

    /// <summary>
    /// Packed table with touched-slot reset: only zeros out occupied slots on Reset
    /// instead of clearing the full 4x table. Saves clearing 75% of the allocation.
    /// </summary>
    public sealed class Touched : IExperimentDictionary<string>
    {
        public static string ExperimentName => "hash.linear.packed.ultra-sparse.touched.v1";

        public static Type? ParentExperimentType => typeof(PackedUltraSparseStringDictionary);

        public static string ExperimentDescription =>
            "Tag+index packed in single uint, 25% load, touched-slot reset avoids clearing the full 4x table on Reset.";

        const float LoadFactor = 0.25f;

        uint[] _table = [];
        string[] _keys = [];
        int[] _touched = [];
        int _touchedCount;
        int _threshold;

        public int Count { get; private set; }

        public void Reset(int capacity)
        {
            var minimumCapacity = Math.Max(16, checked((int)(capacity / LoadFactor) + 1));
            if (_table.Length < minimumCapacity)
                ResizeToEmpty(Pow2(minimumCapacity));
            else
            {
                var table = _table;
                var touched = _touched;
                for (var i = 0; i < _touchedCount; i++)
                    table[touched[i]] = 0;
                _touchedCount = 0;
            }

            Count = 0;
        }

        public int GetOrAddIndex(string key)
        {
            if (Count >= _threshold)
                ResizeTo(_table.Length << 1);

            var table = _table;
            var keys = _keys;
            var hash = key.GetHashCode() & int.MaxValue;
            var tag = (uint)((hash >> 24) | 0x80);
            var mask = table.Length - 1;
            var slot = hash & mask;

            while (true)
            {
                var entry = table[slot];
                if (entry == 0)
                {
                    var index = Count++;
                    keys[index] = key;
                    table[slot] = (tag << 24) | (uint)(index + 1);
                    _touched[_touchedCount++] = slot;
                    return index;
                }

                if (entry >> 24 == tag)
                {
                    var existingIndex = (int)(entry & 0x00FFFFFFu) - 1;
                    if (keys[existingIndex] == key)
                        return existingIndex;
                }

                slot = (slot + 1) & mask;
            }
        }

        void ResizeToEmpty(int slotLength)
        {
            _table = new uint[slotLength];
            _touched = new int[slotLength];
            if (_keys.Length < slotLength)
                Array.Resize(ref _keys, slotLength);
            _threshold = checked((int)(slotLength * LoadFactor));
            _touchedCount = 0;
        }

        void ResizeTo(int slotLength)
        {
            var newTable = new uint[slotLength];
            var newTouched = new int[slotLength];
            if (_keys.Length < slotLength)
                Array.Resize(ref _keys, slotLength);

            _threshold = checked((int)(slotLength * LoadFactor));
            if (Count == 0)
            {
                _table = newTable;
                _touched = newTouched;
                _touchedCount = 0;
                return;
            }

            var mask = slotLength - 1;
            var touchedCount = 0;
            for (var i = 0; i < Count; i++)
            {
                var key = _keys[i];
                var hash = key.GetHashCode() & int.MaxValue;
                var tag = (uint)((hash >> 24) | 0x80);
                var slot = hash & mask;
                while (newTable[slot] != 0)
                    slot = (slot + 1) & mask;
                newTable[slot] = (tag << 24) | (uint)(i + 1);
                newTouched[touchedCount++] = slot;
            }

            _table = newTable;
            _touched = newTouched;
            _touchedCount = touchedCount;
        }
    }
}
