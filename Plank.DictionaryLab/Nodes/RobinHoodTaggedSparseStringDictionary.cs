namespace Plank.DictionaryLab.Nodes;

public sealed class RobinHoodTaggedSparseStringDictionary : IExperimentDictionary<string>
{
    public static string ExperimentName => "hash.robinhood.tagged.sparse.v1";

    public static Type? ParentExperimentType => typeof(RobinHoodTaggedStringDictionary);

    public static string ExperimentDescription => "Robin Hood tagged probing with 40% max load.";

    const float LoadFactor = 0.40f;

    int[] _slots = [];
    ushort[] _distance = [];
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
            Array.Clear(_distance);
            Array.Clear(_tags);
        }

        Count = 0;
    }

    public int GetOrAddIndex(string key)
    {
        if (Count >= _threshold)
            ResizeTo(_slots.Length << 1);

        var hash = key.GetHashCode() & int.MaxValue;
        var tag = (byte)((hash >> 24) | 0x80);
        if (TryFindIndex(key, hash, tag, out var existingIndex))
            return existingIndex;

        var index = Count++;
        _keys[index] = key;
        InsertIndex(index, hash, tag);
        return index;
    }

    bool TryFindIndex(string key, int hash, byte tag, out int index)
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
            if (_tags[slot] == tag && _keys[existingIndex] == key)
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
            var hash = oldKeys[i].GetHashCode() & int.MaxValue;
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

    public sealed class MixedUniquenessThroughput10To70Metadata : IExperimentDictionary<string>
    {
        public static string ExperimentName => "hash.robinhood.tagged.sparse.mixed-uniqueness-10-70.meta.v1";

        public static Type? ParentExperimentType => typeof(RobinHoodTaggedSparseStringDictionary);

        public static string ExperimentDescription => "Robin Hood tagged sparse probing with epoch-stamped recent-hit metadata tuned for mixed uniqueness throughput (10-70).";

        const float LoadFactor = 0.40f;
        const int RecentCacheMask = 127;

        int[] _slots = [];
        ushort[] _distance = [];
        byte[] _tags = [];
        string[] _keys = [];
        int[] _recentHashes = new int[RecentCacheMask + 1];
        int[] _recentIndexes = new int[RecentCacheMask + 1];
        ushort[] _recentEpoch = new ushort[RecentCacheMask + 1];
        ushort _epoch = 1;
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
            AdvanceRecentEpoch();
        }

        public int GetOrAddIndex(string key)
        {
            if (Count >= _threshold)
                ResizeTo(_slots.Length << 1);

            var hash = key.GetHashCode() & int.MaxValue;
            if (TryGetRecentHit(key, hash, out var recentIndex))
                return recentIndex;

            var tag = (byte)((hash >> 24) | 0x80);
            if (TryFindIndex(key, hash, tag, out var existingIndex))
            {
                StoreRecent(hash, existingIndex);
                return existingIndex;
            }

            var index = Count++;
            _keys[index] = key;
            InsertIndex(index, hash, tag);
            StoreRecent(hash, index);
            return index;
        }

        bool TryGetRecentHit(string key, int hash, out int index)
        {
            var slot = hash & RecentCacheMask;
            if (_recentEpoch[slot] != _epoch || _recentHashes[slot] != hash)
            {
                index = -1;
                return false;
            }

            var cachedIndex = _recentIndexes[slot];
            if ((uint)cachedIndex >= (uint)Count)
            {
                index = -1;
                return false;
            }

            if (_keys[cachedIndex] != key)
            {
                index = -1;
                return false;
            }

            index = cachedIndex;
            return true;
        }

        void StoreRecent(int hash, int index)
        {
            var slot = hash & RecentCacheMask;
            _recentHashes[slot] = hash;
            _recentIndexes[slot] = index;
            _recentEpoch[slot] = _epoch;
        }

        void AdvanceRecentEpoch()
        {
            _epoch++;
            if (_epoch != 0)
                return;

            Array.Clear(_recentEpoch);
            _epoch = 1;
        }

        bool TryFindIndex(string key, int hash, byte tag, out int index)
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
                if (_tags[slot] == tag && _keys[existingIndex] == key)
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
            AdvanceRecentEpoch();
            if (oldCount == 0)
                return;

            for (var i = 0; i < oldCount; i++)
            {
                var hash = oldKeys[i].GetHashCode() & int.MaxValue;
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
}

public sealed class RobinHoodTaggedUltraSparseStringDictionary : IExperimentDictionary<string>
{
    public static string ExperimentName => "hash.robinhood.tagged.ultra-sparse.v1";

    public static Type? ParentExperimentType => typeof(RobinHoodTaggedSparseStringDictionary);

    public static string ExperimentDescription => "Robin Hood tagged probing with 25% max load.";

    const float LoadFactor = 0.25f;

    int[] _slots = [];
    ushort[] _distance = [];
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
            Array.Clear(_distance);
            Array.Clear(_tags);
        }

        Count = 0;
    }

    public int GetOrAddIndex(string key)
    {
        if (Count >= _threshold)
            ResizeTo(_slots.Length << 1);

        var hash = key.GetHashCode() & int.MaxValue;
        var tag = (byte)((hash >> 24) | 0x80);
        if (TryFindIndex(key, hash, tag, out var existingIndex))
            return existingIndex;

        var index = Count++;
        _keys[index] = key;
        InsertIndex(index, hash, tag);
        return index;
    }

    bool TryFindIndex(string key, int hash, byte tag, out int index)
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
            if (_tags[slot] == tag && _keys[existingIndex] == key)
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
            var hash = oldKeys[i].GetHashCode() & int.MaxValue;
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

    public sealed class SwapReduced : IExperimentDictionary<string>
    {
        public static string ExperimentName => "hash.robinhood.tagged.ultra-sparse.tuned.swap-reduced.v1";

        public static Type? ParentExperimentType => typeof(RobinHoodTaggedUltraSparseStringDictionary);

        public static string ExperimentDescription => "Robin Hood tagged ultra-sparse probing tuned to reduce swap and probe-distance churn during inserts.";

        const float LoadFactor = 0.25f;

        int[] _slots = [];
        ushort[] _distance = [];
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
                Array.Clear(_distance);
                Array.Clear(_tags);
            }

            Count = 0;
        }

        public int GetOrAddIndex(string key)
        {
            if (Count >= _threshold)
                ResizeTo(_slots.Length << 1);

            var hash = key.GetHashCode() & int.MaxValue;
            var tag = (byte)((hash >> 24) | 0x80);
            if (TryFindIndex(key, hash, tag, out var existingIndex))
                return existingIndex;

            var index = Count++;
            _keys[index] = key;
            InsertIndex(index, hash, tag);
            return index;
        }

        bool TryFindIndex(string key, int hash, byte tag, out int index)
        {
            var slots = _slots;
            var distance = _distance;
            var tags = _tags;
            var keys = _keys;
            var mask = slots.Length - 1;
            var slot = hash & mask;
            var probeDistance = 0;

            while (true)
            {
                var slotTag = tags[slot];
                if (slotTag == 0)
                {
                    index = -1;
                    return false;
                }

                var existingDistance = distance[slot];
                if (existingDistance < probeDistance)
                {
                    index = -1;
                    return false;
                }

                if (slotTag == tag)
                {
                    var existingIndex = slots[slot] - 1;
                    if (keys[existingIndex] == key)
                    {
                        index = existingIndex;
                        return true;
                    }
                }

                slot = (slot + 1) & mask;
                probeDistance++;
            }
        }

        void InsertIndex(int incomingIndex, int hash, byte incomingTag)
        {
            var slots = _slots;
            var distance = _distance;
            var tags = _tags;
            var mask = slots.Length - 1;
            var slot = hash & mask;
            var incomingDistance = 0;

            while (true)
            {
                var slotTag = tags[slot];
                if (slotTag == 0)
                {
                    slots[slot] = incomingIndex + 1;
                    distance[slot] = checked((ushort)incomingDistance);
                    tags[slot] = incomingTag;
                    return;
                }

                var existingDistance = distance[slot];
                if (existingDistance < incomingDistance)
                {
                    var displacedIndex = slots[slot] - 1;
                    slots[slot] = incomingIndex + 1;
                    distance[slot] = checked((ushort)incomingDistance);
                    tags[slot] = incomingTag;
                    incomingIndex = displacedIndex;
                    incomingTag = slotTag;
                    incomingDistance = existingDistance;
                }

                slot = (slot + 1) & mask;
                incomingDistance++;
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
                var hash = oldKeys[i].GetHashCode() & int.MaxValue;
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

    public sealed class SwapReducedProbeReuse : IExperimentDictionary<string>
    {
        public static string ExperimentName => "hash.robinhood.tagged.ultra-sparse.tuned.swap-reduced.probe-reuse.v1";

        public static Type? ParentExperimentType => typeof(SwapReduced);

        public static string ExperimentDescription => "Robin Hood tagged ultra-sparse probing that reuses miss probe state to avoid duplicate insert-path scans.";

        const float LoadFactor = 0.25f;

        int[] _slots = [];
        ushort[] _distance = [];
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
                Array.Clear(_distance);
                Array.Clear(_tags);
            }

            Count = 0;
        }

        public int GetOrAddIndex(string key)
        {
            if (Count >= _threshold)
                ResizeTo(_slots.Length << 1);

            var hash = key.GetHashCode() & int.MaxValue;
            var tag = (byte)((hash >> 24) | 0x80);
            if (TryFindIndex(key, hash, tag, out var existingIndex, out var insertSlot, out var insertDistance))
                return existingIndex;

            var index = Count++;
            _keys[index] = key;
            InsertIndexFromProbe(index, tag, insertSlot, insertDistance);
            return index;
        }

        bool TryFindIndex(string key, int hash, byte tag, out int index, out int insertSlot, out int insertDistance)
        {
            var slots = _slots;
            var distance = _distance;
            var tags = _tags;
            var keys = _keys;
            var mask = slots.Length - 1;
            var slot = hash & mask;
            var probeDistance = 0;

            while (true)
            {
                var slotTag = tags[slot];
                if (slotTag == 0)
                {
                    index = -1;
                    insertSlot = slot;
                    insertDistance = probeDistance;
                    return false;
                }

                var existingDistance = distance[slot];
                if (existingDistance < probeDistance)
                {
                    index = -1;
                    insertSlot = slot;
                    insertDistance = probeDistance;
                    return false;
                }

                if (slotTag == tag)
                {
                    var existingIndex = slots[slot] - 1;
                    if (keys[existingIndex] == key)
                    {
                        index = existingIndex;
                        insertSlot = -1;
                        insertDistance = 0;
                        return true;
                    }
                }

                slot = (slot + 1) & mask;
                probeDistance++;
            }
        }

        void InsertIndex(int incomingIndex, int hash, byte incomingTag)
        {
            var slot = hash & (_slots.Length - 1);
            InsertIndexFromProbe(incomingIndex, incomingTag, slot, 0);
        }

        void InsertIndexFromProbe(int incomingIndex, byte incomingTag, int slot, int incomingDistance)
        {
            var slots = _slots;
            var distance = _distance;
            var tags = _tags;
            var mask = slots.Length - 1;

            while (true)
            {
                var slotTag = tags[slot];
                if (slotTag == 0)
                {
                    slots[slot] = incomingIndex + 1;
                    distance[slot] = checked((ushort)incomingDistance);
                    tags[slot] = incomingTag;
                    return;
                }

                var existingDistance = distance[slot];
                if (existingDistance < incomingDistance)
                {
                    var displacedIndex = slots[slot] - 1;
                    slots[slot] = incomingIndex + 1;
                    distance[slot] = checked((ushort)incomingDistance);
                    tags[slot] = incomingTag;
                    incomingIndex = displacedIndex;
                    incomingTag = slotTag;
                    incomingDistance = existingDistance;
                }

                slot = (slot + 1) & mask;
                incomingDistance++;
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
                var hash = oldKeys[i].GetHashCode() & int.MaxValue;
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

    public sealed class SwapReducedProbeReusePackedMeta : IExperimentDictionary<string>
    {
        public static string ExperimentName => "hash.robinhood.tagged.ultra-sparse.tuned.swap-reduced.probe-reuse.meta-packed.v1";

        public static Type? ParentExperimentType => typeof(SwapReducedProbeReuse);

        public static string ExperimentDescription => "Robin Hood tagged ultra-sparse probing that packs tag+distance metadata to reduce probe-path memory traffic.";

        const float LoadFactor = 0.25f;

        int[] _slots = [];
        uint[] _meta = [];
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
                Array.Clear(_meta);
            }

            Count = 0;
        }

        public int GetOrAddIndex(string key)
        {
            if (Count >= _threshold)
                ResizeTo(_slots.Length << 1);

            var hash = key.GetHashCode() & int.MaxValue;
            var tag = (byte)((hash >> 24) | 0x80);
            if (TryFindIndex(key, hash, tag, out var existingIndex, out var insertSlot, out var insertDistance))
                return existingIndex;

            var index = Count++;
            _keys[index] = key;
            InsertIndexFromProbe(index, tag, insertSlot, insertDistance);
            return index;
        }

        bool TryFindIndex(string key, int hash, byte tag, out int index, out int insertSlot, out int insertDistance)
        {
            var slots = _slots;
            var meta = _meta;
            var keys = _keys;
            var mask = slots.Length - 1;
            var slot = hash & mask;
            var probeDistance = 0;

            while (true)
            {
                var slotMeta = meta[slot];
                if (slotMeta == 0)
                {
                    index = -1;
                    insertSlot = slot;
                    insertDistance = probeDistance;
                    return false;
                }

                var existingDistance = (int)(slotMeta >> 8);
                if (existingDistance < probeDistance)
                {
                    index = -1;
                    insertSlot = slot;
                    insertDistance = probeDistance;
                    return false;
                }

                if ((byte)slotMeta == tag)
                {
                    var existingIndex = slots[slot] - 1;
                    if (keys[existingIndex] == key)
                    {
                        index = existingIndex;
                        insertSlot = -1;
                        insertDistance = 0;
                        return true;
                    }
                }

                slot = (slot + 1) & mask;
                probeDistance++;
            }
        }

        void InsertIndex(int incomingIndex, int hash, byte incomingTag)
        {
            var slot = hash & (_slots.Length - 1);
            InsertIndexFromProbe(incomingIndex, incomingTag, slot, 0);
        }

        void InsertIndexFromProbe(int incomingIndex, byte incomingTag, int slot, int incomingDistance)
        {
            var slots = _slots;
            var meta = _meta;
            var mask = slots.Length - 1;

            while (true)
            {
                var slotMeta = meta[slot];
                if (slotMeta == 0)
                {
                    slots[slot] = incomingIndex + 1;
                    meta[slot] = PackMeta(incomingDistance, incomingTag);
                    return;
                }

                var existingDistance = (int)(slotMeta >> 8);
                if (existingDistance < incomingDistance)
                {
                    var displacedIndex = slots[slot] - 1;
                    slots[slot] = incomingIndex + 1;
                    meta[slot] = PackMeta(incomingDistance, incomingTag);
                    incomingIndex = displacedIndex;
                    incomingTag = (byte)slotMeta;
                    incomingDistance = existingDistance;
                }

                slot = (slot + 1) & mask;
                incomingDistance++;
            }
        }

        void ResizeTo(int slotLength)
        {
            var oldKeys = _keys;
            var oldCount = Count;
            if (_keys.Length < slotLength)
                Array.Resize(ref _keys, slotLength);

            _slots = new int[slotLength];
            _meta = new uint[slotLength];
            _threshold = checked((int)(slotLength * LoadFactor));
            if (oldCount == 0)
                return;

            for (var i = 0; i < oldCount; i++)
            {
                var hash = oldKeys[i].GetHashCode() & int.MaxValue;
                var tag = (byte)((hash >> 24) | 0x80);
                InsertIndex(i, hash, tag);
            }
        }

        static uint PackMeta(int distance, byte tag) => ((uint)checked((ushort)distance) << 8) | tag;

        static int Pow2(int value)
        {
            var size = 16;
            while (size < value)
                size <<= 1;
            return size;
        }
    }

    public sealed class SwapReducedProbeReusePackedMetaResetTouched : IExperimentDictionary<string>
    {
        public static string ExperimentName => "hash.robinhood.tagged.ultra-sparse.tuned.swap-reduced.probe-reuse.meta-packed.reset-touched.v1";

        public static Type? ParentExperimentType => typeof(SwapReducedProbeReusePackedMeta);

        public static string ExperimentDescription => "Robin Hood tagged ultra-sparse probing with packed metadata and touched-slot reset clearing to reduce reset-path memory work.";

        const float LoadFactor = 0.25f;

        int[] _slots = [];
        uint[] _meta = [];
        string[] _keys = [];
        int[] _touchedSlots = [];
        int _touchedCount;
        int _threshold;

        public int Count { get; private set; }

        public void Reset(int capacity)
        {
            var minimumCapacity = Math.Max(16, checked((int)(capacity / LoadFactor) + 1));
            if (_slots.Length < minimumCapacity)
            {
                Count = 0;
                ResizeTo(Pow2(minimumCapacity));
                return;
            }

            var touchedSlots = _touchedSlots;
            var touchedCount = _touchedCount;
            var slots = _slots;
            var meta = _meta;
            for (var i = 0; i < touchedCount; i++)
            {
                var slot = touchedSlots[i];
                slots[slot] = 0;
                meta[slot] = 0;
            }

            _touchedCount = 0;
            Count = 0;
        }

        public int GetOrAddIndex(string key)
        {
            if (Count >= _threshold)
                ResizeTo(_slots.Length << 1);

            var hash = key.GetHashCode() & int.MaxValue;
            var tag = (byte)((hash >> 24) | 0x80);
            if (TryFindIndex(key, hash, tag, out var existingIndex, out var insertSlot, out var insertDistance))
                return existingIndex;

            var index = Count++;
            _keys[index] = key;
            InsertIndexFromProbe(index, tag, insertSlot, insertDistance);
            return index;
        }

        bool TryFindIndex(string key, int hash, byte tag, out int index, out int insertSlot, out int insertDistance)
        {
            var slots = _slots;
            var meta = _meta;
            var keys = _keys;
            var mask = slots.Length - 1;
            var slot = hash & mask;
            var probeDistance = 0;

            while (true)
            {
                var slotMeta = meta[slot];
                if (slotMeta == 0)
                {
                    index = -1;
                    insertSlot = slot;
                    insertDistance = probeDistance;
                    return false;
                }

                var existingDistance = (int)(slotMeta >> 8);
                if (existingDistance < probeDistance)
                {
                    index = -1;
                    insertSlot = slot;
                    insertDistance = probeDistance;
                    return false;
                }

                if ((byte)slotMeta == tag)
                {
                    var existingIndex = slots[slot] - 1;
                    if (keys[existingIndex] == key)
                    {
                        index = existingIndex;
                        insertSlot = -1;
                        insertDistance = 0;
                        return true;
                    }
                }

                slot = (slot + 1) & mask;
                probeDistance++;
            }
        }

        void InsertIndex(int incomingIndex, int hash, byte incomingTag)
        {
            var slot = hash & (_slots.Length - 1);
            InsertIndexFromProbe(incomingIndex, incomingTag, slot, 0);
        }

        void InsertIndexFromProbe(int incomingIndex, byte incomingTag, int slot, int incomingDistance)
        {
            var slots = _slots;
            var meta = _meta;
            var touchedSlots = _touchedSlots;
            var mask = slots.Length - 1;

            while (true)
            {
                var slotMeta = meta[slot];
                if (slotMeta == 0)
                {
                    slots[slot] = incomingIndex + 1;
                    meta[slot] = PackMeta(incomingDistance, incomingTag);
                    touchedSlots[_touchedCount++] = slot;
                    return;
                }

                var existingDistance = (int)(slotMeta >> 8);
                if (existingDistance < incomingDistance)
                {
                    var displacedIndex = slots[slot] - 1;
                    slots[slot] = incomingIndex + 1;
                    meta[slot] = PackMeta(incomingDistance, incomingTag);
                    incomingIndex = displacedIndex;
                    incomingTag = (byte)slotMeta;
                    incomingDistance = existingDistance;
                }

                slot = (slot + 1) & mask;
                incomingDistance++;
            }
        }

        void ResizeTo(int slotLength)
        {
            var oldKeys = _keys;
            var oldCount = Count;
            if (_keys.Length < slotLength)
                Array.Resize(ref _keys, slotLength);

            _slots = new int[slotLength];
            _meta = new uint[slotLength];
            _touchedSlots = new int[slotLength];
            _touchedCount = 0;
            _threshold = checked((int)(slotLength * LoadFactor));
            if (oldCount == 0)
                return;

            for (var i = 0; i < oldCount; i++)
            {
                var hash = oldKeys[i].GetHashCode() & int.MaxValue;
                var tag = (byte)((hash >> 24) | 0x80);
                InsertIndex(i, hash, tag);
            }
        }

        static uint PackMeta(int distance, byte tag) => ((uint)checked((ushort)distance) << 8) | tag;

        static int Pow2(int value)
        {
            var size = 16;
            while (size < value)
                size <<= 1;
            return size;
        }
    }

    public sealed class Tuned : IExperimentDictionary<string>
    {
        public static string ExperimentName => "hash.robinhood.tagged.ultra-sparse.tuned.v1";

        public static Type? ParentExperimentType => typeof(SwapReduced);

        public static string ExperimentDescription => "Robin Hood tagged ultra-sparse probing tuned for tag-first early checks.";

        const float LoadFactor = 0.25f;

        int[] _slots = [];
        ushort[] _distance = [];
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
                Array.Clear(_distance);
                Array.Clear(_tags);
            }

            Count = 0;
        }

        public int GetOrAddIndex(string key)
        {
            if (Count >= _threshold)
                ResizeTo(_slots.Length << 1);

            var hash = key.GetHashCode() & int.MaxValue;
            var tag = (byte)((hash >> 24) | 0x80);
            if (TryFindIndex(key, hash, tag, out var existingIndex))
                return existingIndex;

            var index = Count++;
            _keys[index] = key;
            InsertIndex(index, hash, tag);
            return index;
        }

        bool TryFindIndex(string key, int hash, byte tag, out int index)
        {
            var slots = _slots;
            var distance = _distance;
            var tags = _tags;
            var keys = _keys;
            var mask = slots.Length - 1;
            var slot = hash & mask;
            var probeDistance = 0;

            while (true)
            {
                var slotTag = tags[slot];
                if (slotTag == 0)
                {
                    index = -1;
                    return false;
                }

                var existingDistance = distance[slot];
                if (existingDistance < probeDistance)
                {
                    index = -1;
                    return false;
                }

                if (slotTag == tag)
                {
                    var existingIndex = slots[slot] - 1;
                    if (keys[existingIndex] == key)
                    {
                        index = existingIndex;
                        return true;
                    }
                }

                slot = (slot + 1) & mask;
                probeDistance++;
            }
        }

        void InsertIndex(int incomingIndex, int hash, byte incomingTag)
        {
            var slots = _slots;
            var distance = _distance;
            var tags = _tags;
            var mask = slots.Length - 1;
            var slot = hash & mask;
            var probeDistance = 0;

            while (true)
            {
                var slotTag = tags[slot];
                if (slotTag == 0)
                {
                    slots[slot] = incomingIndex + 1;
                    distance[slot] = checked((ushort)probeDistance);
                    tags[slot] = incomingTag;
                    return;
                }

                var existingDistance = distance[slot];
                if (existingDistance < probeDistance)
                {
                    var displacedIndex = slots[slot] - 1;
                    var displacedTag = slotTag;
                    slots[slot] = incomingIndex + 1;
                    distance[slot] = checked((ushort)probeDistance);
                    tags[slot] = incomingTag;
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
                var hash = oldKeys[i].GetHashCode() & int.MaxValue;
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
}
