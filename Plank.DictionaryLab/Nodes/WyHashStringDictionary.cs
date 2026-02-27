using System.Runtime.InteropServices;

namespace Plank.DictionaryLab.Nodes;

/// <summary>
/// Uses wyhash on the raw UTF-16 bytes of the string (via MemoryMarshal.AsBytes) instead of
/// string.GetHashCode() which uses Marvin32. For 7–12 char strings (14–24 UTF-16 bytes),
/// wyhash needs 1–2 BigMuls while Marvin32 needs 4–6 iterations.
/// Key equality still uses string == operator (JIT-optimised SIMD).
/// </summary>
public sealed class WyHashUltraSparseStringDictionary : IExperimentDictionary<string>
{
    public static string ExperimentName => "hash.linear.tagged.ultra-sparse.wyhash.v1";
    public static Type? ParentExperimentType => typeof(TaggedLinearProbingUltraSparseStringDictionary);
    public static string ExperimentDescription =>
        "wyhash on raw UTF-16 bytes (MemoryMarshal.AsBytes) replaces Marvin32 (string.GetHashCode). " +
        "Slot-first probe: reads _slots first so tag and key loads issue in parallel (ILP). 25% load.";

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
        var hash = WyHashing.Hash(MemoryMarshal.AsBytes(key.AsSpan())) & int.MaxValue;
        var tag = (byte)((hash >> 24) | 0x80);
        var mask = slots.Length - 1;
        var slot = hash & mask;

        while (true)
        {
            var entry = slots[slot];
            if (entry == 0)
            {
                var index = Count++;
                keys[index] = key;
                slots[slot] = index + 1;
                tags[slot] = tag;
                return index;
            }

            var existingIndex = entry - 1;
            if (tags[slot] == tag && keys[existingIndex] == key)
                return existingIndex;

            slot = (slot + 1) & mask;
        }
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
            var hash = WyHashing.Hash(MemoryMarshal.AsBytes(key.AsSpan())) & int.MaxValue;
            var tag = (byte)((hash >> 24) | 0x80);
            var slot = hash & mask;
            while (newSlots[slot] != 0)
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

    /// <summary>wyhash UTF-16 + tag-first probe + touched-slot reset.</summary>
    public sealed class Touched : IExperimentDictionary<string>
    {
        public static string ExperimentName => "hash.linear.tagged.ultra-sparse.wyhash.touched.v1";
        public static Type? ParentExperimentType => typeof(WyHashUltraSparseStringDictionary);
        public static string ExperimentDescription =>
            "wyhash on UTF-16 bytes + tag-first probe + touched-slot reset.";

        const float LoadFactor = 0.25f;

        int[] _slots = [];
        byte[] _tags = [];
        string[] _keys = [];
        int[] _touchedSlots = [];
        int _touchedCount;
        int _threshold;

        public int Count { get; private set; }

        public void Reset(int capacity)
        {
            var minimumCapacity = Math.Max(16, checked((int)(capacity / LoadFactor) + 1));
            if (_slots.Length < minimumCapacity)
                ResizeToEmpty(Pow2(minimumCapacity));
            else
            {
                var slots = _slots;
                var tags = _tags;
                var touched = _touchedSlots;
                for (var i = 0; i < _touchedCount; i++)
                {
                    slots[touched[i]] = 0;
                    tags[touched[i]] = 0;
                }
                _touchedCount = 0;
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
            var hash = WyHashing.Hash(MemoryMarshal.AsBytes(key.AsSpan())) & int.MaxValue;
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
                    _touchedSlots[_touchedCount++] = slot;
                    return index;
                }

                if (slotTag == tag)
                {
                    var existingIndex = slots[slot] - 1;
                    if (keys[existingIndex] == key)
                        return existingIndex;
                }

                slot = (slot + 1) & mask;
            }
        }

        void ResizeToEmpty(int slotLength)
        {
            _slots = new int[slotLength];
            _tags = new byte[slotLength];
            _touchedSlots = new int[slotLength];
            if (_keys.Length < slotLength)
                Array.Resize(ref _keys, slotLength);
            _threshold = checked((int)(slotLength * LoadFactor));
            _touchedCount = 0;
        }

        void ResizeTo(int slotLength)
        {
            var newSlots = new int[slotLength];
            var newTags = new byte[slotLength];
            var newTouched = new int[slotLength];
            if (_keys.Length < slotLength)
                Array.Resize(ref _keys, slotLength);

            _threshold = checked((int)(slotLength * LoadFactor));
            if (Count == 0)
            {
                _slots = newSlots;
                _tags = newTags;
                _touchedSlots = newTouched;
                _touchedCount = 0;
                return;
            }

            var mask = slotLength - 1;
            var touchedCount = 0;
            for (var i = 0; i < Count; i++)
            {
                var key = _keys[i];
                var hash = WyHashing.Hash(MemoryMarshal.AsBytes(key.AsSpan())) & int.MaxValue;
                var tag = (byte)((hash >> 24) | 0x80);
                var slot = hash & mask;
                while (newTags[slot] != 0)
                    slot = (slot + 1) & mask;
                newSlots[slot] = i + 1;
                newTags[slot] = tag;
                newTouched[touchedCount++] = slot;
            }

            _slots = newSlots;
            _tags = newTags;
            _touchedSlots = newTouched;
            _touchedCount = touchedCount;
        }

        static int Pow2(int value)
        {
            var size = 16;
            while (size < value)
                size <<= 1;
            return size;
        }
    }

    /// <summary>wyhash UTF-16 + tag-first probe.</summary>
    public sealed class Tuned : IExperimentDictionary<string>
    {
        public static string ExperimentName => "hash.linear.tagged.ultra-sparse.wyhash.tuned.v1";
        public static Type? ParentExperimentType => typeof(WyHashUltraSparseStringDictionary);
        public static string ExperimentDescription =>
            "wyhash on UTF-16 bytes + tag-first probe (avoids _slots load on empty/mismatch). 25% load.";

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
            var hash = WyHashing.Hash(MemoryMarshal.AsBytes(key.AsSpan())) & int.MaxValue;
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
                    return index;
                }

                if (slotTag == tag)
                {
                    var existingIndex = slots[slot] - 1;
                    if (keys[existingIndex] == key)
                        return existingIndex;
                }

                slot = (slot + 1) & mask;
            }
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
                var hash = WyHashing.Hash(MemoryMarshal.AsBytes(key.AsSpan())) & int.MaxValue;
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

        /// <summary>wyhash UTF-16 + tag-first + 4-step unrolled probe.</summary>
        public sealed class Unrolled4 : IExperimentDictionary<string>
        {
            public static string ExperimentName => "hash.linear.tagged.ultra-sparse.wyhash.tuned.unrolled4.v1";
            public static Type? ParentExperimentType => typeof(Tuned);
            public static string ExperimentDescription =>
                "wyhash on UTF-16 bytes + tag-first probe + 4-step unrolled loop.";

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
                var hash = WyHashing.Hash(MemoryMarshal.AsBytes(key.AsSpan())) & int.MaxValue;
                var tag = (byte)((hash >> 24) | 0x80);
                var mask = slots.Length - 1;
                var slot = hash & mask;

                while (true)
                {
                    var t0 = tags[slot];
                    if (t0 == 0) return Insert(slot, key, tag, slots, tags, keys);
                    if (t0 == tag) { var idx = slots[slot] - 1; if (keys[idx] == key) return idx; }
                    slot = (slot + 1) & mask;

                    var t1 = tags[slot];
                    if (t1 == 0) return Insert(slot, key, tag, slots, tags, keys);
                    if (t1 == tag) { var idx = slots[slot] - 1; if (keys[idx] == key) return idx; }
                    slot = (slot + 1) & mask;

                    var t2 = tags[slot];
                    if (t2 == 0) return Insert(slot, key, tag, slots, tags, keys);
                    if (t2 == tag) { var idx = slots[slot] - 1; if (keys[idx] == key) return idx; }
                    slot = (slot + 1) & mask;

                    var t3 = tags[slot];
                    if (t3 == 0) return Insert(slot, key, tag, slots, tags, keys);
                    if (t3 == tag) { var idx = slots[slot] - 1; if (keys[idx] == key) return idx; }
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
                    var hash = WyHashing.Hash(MemoryMarshal.AsBytes(key.AsSpan())) & int.MaxValue;
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
    }

    /// <summary>wyhash UTF-16 + slot-first ILP probe + touched-slot reset.</summary>
    public sealed class BasicTouched : IExperimentDictionary<string>
    {
        public static string ExperimentName => "hash.linear.tagged.ultra-sparse.wyhash.basic-touched.v1";
        public static Type? ParentExperimentType => typeof(WyHashUltraSparseStringDictionary);
        public static string ExperimentDescription =>
            "wyhash on UTF-16 bytes + slot-first ILP probe + touched-slot reset.";

        const float LoadFactor = 0.25f;

        int[] _slots = [];
        byte[] _tags = [];
        string[] _keys = [];
        int[] _touchedSlots = [];
        int _touchedCount;
        int _threshold;

        public int Count { get; private set; }

        public void Reset(int capacity)
        {
            var minimumCapacity = Math.Max(16, checked((int)(capacity / LoadFactor) + 1));
            if (_slots.Length < minimumCapacity)
                ResizeTo(Pow2(minimumCapacity));
            else
                ClearTouched();
            Count = 0;
        }

        void ClearTouched()
        {
            if (_touchedCount * 4 >= _slots.Length)
            {
                Array.Clear(_slots);
                Array.Clear(_tags);
                _touchedCount = 0;
                return;
            }
            var slots = _slots;
            var tags = _tags;
            var touched = _touchedSlots;
            for (var i = 0; i < _touchedCount; i++)
            {
                slots[touched[i]] = 0;
                tags[touched[i]] = 0;
            }
            _touchedCount = 0;
        }

        public int GetOrAddIndex(string key)
        {
            if (Count >= _threshold)
                ResizeTo(_slots.Length << 1);

            var slots = _slots;
            var tags = _tags;
            var keys = _keys;
            var hash = WyHashing.Hash(MemoryMarshal.AsBytes(key.AsSpan())) & int.MaxValue;
            var tag = (byte)((hash >> 24) | 0x80);
            var mask = slots.Length - 1;
            var slot = hash & mask;

            while (true)
            {
                var entry = slots[slot];
                if (entry == 0)
                {
                    var index = Count++;
                    keys[index] = key;
                    slots[slot] = index + 1;
                    tags[slot] = tag;
                    _touchedSlots[_touchedCount++] = slot;
                    return index;
                }

                var existingIndex = entry - 1;
                if (tags[slot] == tag && keys[existingIndex] == key)
                    return existingIndex;

                slot = (slot + 1) & mask;
            }
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
                var hash = WyHashing.Hash(MemoryMarshal.AsBytes(key.AsSpan())) & int.MaxValue;
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
}
