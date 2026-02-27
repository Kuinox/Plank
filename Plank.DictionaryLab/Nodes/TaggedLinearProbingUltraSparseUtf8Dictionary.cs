namespace Plank.DictionaryLab.Nodes;

public sealed class TaggedLinearProbingUltraSparseUtf8Dictionary : IExperimentDictionary<ReadOnlyMemory<byte>>
{
    public static string ExperimentName => "hash.linear.tagged.ultra-sparse.v1";

    public static Type? ParentExperimentType => typeof(TaggedLinearProbingSparseUtf8Dictionary);

    public static string ExperimentDescription => "UTF-8 tagged probing with 25% max load.";

    const float LoadFactor = 0.25f;

    int[] _slots = [];
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
                return index;
            }

            var existingIndex = entry - 1;
            if (_tags[slot] == tag && _keys[existingIndex].Span.SequenceEqual(key.Span))
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
            var hash = ByteHashing.Hash(key.Span) & int.MaxValue;
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
    public sealed class Tuned : IExperimentDictionary<ReadOnlyMemory<byte>>
    {
        public static string ExperimentName => "hash.linear.tagged.ultra-sparse.tuned.v1";

        public static Type? ParentExperimentType => typeof(TaggedLinearProbingUltraSparseUtf8Dictionary);

        public static string ExperimentDescription => "UTF-8 tagged ultra-sparse probing tuned for tag-first branch behavior.";

        const float LoadFactor = 0.25f;

        int[] _slots = [];
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
                Array.Clear(_tags);
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
                    return index;
                }

                if (slotTag == tag)
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
                var hash = ByteHashing.Hash(key.Span) & int.MaxValue;
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

        public sealed class Touched : IExperimentDictionary<ReadOnlyMemory<byte>>
        {
            public static string ExperimentName => "hash.linear.tagged.ultra-sparse.tuned.touched.v1";

            public static Type? ParentExperimentType => typeof(Tuned);

            public static string ExperimentDescription => "UTF-8 tagged ultra-sparse tuned probing with touched-slot reset to avoid full clears.";

            const float LoadFactor = 0.25f;

            int[] _slots = [];
            byte[] _tags = [];
            ReadOnlyMemory<byte>[] _keys = [];
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
                    for (var i = 0; i < _touchedCount; i++)
                    {
                        var slot = _touchedSlots[i];
                        _slots[slot] = 0;
                        _tags[slot] = 0;
                    }

                    _touchedCount = 0;
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
                        _touchedSlots[_touchedCount++] = slot;
                        return index;
                    }

                    if (slotTag == tag)
                    {
                        var existingIndex = slots[slot] - 1;
                        if (keys[existingIndex].Span.SequenceEqual(key.Span))
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
                var newTouchedSlots = new int[slotLength];
                if (_keys.Length < slotLength)
                    Array.Resize(ref _keys, slotLength);

                _threshold = checked((int)(slotLength * LoadFactor));
                if (Count == 0)
                {
                    _slots = newSlots;
                    _tags = newTags;
                    _touchedSlots = newTouchedSlots;
                    _touchedCount = 0;
                    return;
                }

                var mask = slotLength - 1;
                var touchedCount = 0;
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
                    newTouchedSlots[touchedCount++] = slot;
                }

                _slots = newSlots;
                _tags = newTags;
                _touchedSlots = newTouchedSlots;
                _touchedCount = touchedCount;
            }

            public sealed class Fingerprinted : IExperimentDictionary<ReadOnlyMemory<byte>>
            {
                public static string ExperimentName => "hash.linear.tagged.ultra-sparse.tuned.touched.fingerprint16.v1";

                public static Type? ParentExperimentType => typeof(Touched);

                public static string ExperimentDescription => "UTF-8 tagged ultra-sparse tuned touched probing with a 16-bit fingerprint prefilter before full key compare.";

                const float LoadFactor = 0.25f;

                int[] _slots = [];
                byte[] _tags = [];
                ushort[] _fingerprints = [];
                ReadOnlyMemory<byte>[] _keys = [];
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
                        for (var i = 0; i < _touchedCount; i++)
                        {
                            var slot = _touchedSlots[i];
                            _slots[slot] = 0;
                            _tags[slot] = 0;
                            _fingerprints[slot] = 0;
                        }

                        _touchedCount = 0;
                    }

                    Count = 0;
                }

                public int GetOrAddIndex(ReadOnlyMemory<byte> key)
                {
                    if (Count >= _threshold)
                        ResizeTo(_slots.Length << 1);

                    var slots = _slots;
                    var tags = _tags;
                    var fingerprints = _fingerprints;
                    var keys = _keys;
                    var hash = ByteHashing.Hash(key.Span) & int.MaxValue;
                    var tag = (byte)((hash >> 24) | 0x80);
                    var fingerprint = Fingerprint(hash);
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
                            fingerprints[slot] = fingerprint;
                            _touchedSlots[_touchedCount++] = slot;
                            return index;
                        }

                        if (slotTag == tag && fingerprints[slot] == fingerprint)
                        {
                            var existingIndex = slots[slot] - 1;
                            if (keys[existingIndex].Span.SequenceEqual(key.Span))
                                return existingIndex;
                        }

                        slot = (slot + 1) & mask;
                    }
                }

                void ResizeToEmpty(int slotLength)
                {
                    _slots = new int[slotLength];
                    _tags = new byte[slotLength];
                    _fingerprints = new ushort[slotLength];
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
                    var newFingerprints = new ushort[slotLength];
                    var newTouchedSlots = new int[slotLength];
                    if (_keys.Length < slotLength)
                        Array.Resize(ref _keys, slotLength);

                    _threshold = checked((int)(slotLength * LoadFactor));
                    if (Count == 0)
                    {
                        _slots = newSlots;
                        _tags = newTags;
                        _fingerprints = newFingerprints;
                        _touchedSlots = newTouchedSlots;
                        _touchedCount = 0;
                        return;
                    }

                    var mask = slotLength - 1;
                    var touchedCount = 0;
                    for (var i = 0; i < Count; i++)
                    {
                        var key = _keys[i];
                        var hash = ByteHashing.Hash(key.Span) & int.MaxValue;
                        var tag = (byte)((hash >> 24) | 0x80);
                        var fingerprint = Fingerprint(hash);
                        var slot = hash & mask;
                        while (newTags[slot] != 0)
                            slot = (slot + 1) & mask;
                        newSlots[slot] = i + 1;
                        newTags[slot] = tag;
                        newFingerprints[slot] = fingerprint;
                        newTouchedSlots[touchedCount++] = slot;
                    }

                    _slots = newSlots;
                    _tags = newTags;
                    _fingerprints = newFingerprints;
                    _touchedSlots = newTouchedSlots;
                    _touchedCount = touchedCount;
                }

                static ushort Fingerprint(int hash) => (ushort)(hash ^ (hash >> 16));
            }
        }

        public sealed class Fingerprint16 : IExperimentDictionary<ReadOnlyMemory<byte>>
        {
            public static string ExperimentName => "hash.linear.tagged.ultra-sparse.tuned.fingerprint16.v1";

            public static Type? ParentExperimentType => typeof(Tuned);

            public static string ExperimentDescription => "UTF-8 tagged ultra-sparse tuned probing with dual-byte fingerprints for balanced 50%-uniqueness hit/miss mixes.";

            const float LoadFactor = 0.25f;

            int[] _slots = [];
            byte[] _primaryTags = [];
            byte[] _secondaryTags = [];
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
                    Array.Clear(_primaryTags);
                    Array.Clear(_secondaryTags);
                }

                Count = 0;
            }

            public int GetOrAddIndex(ReadOnlyMemory<byte> key)
            {
                if (Count >= _threshold)
                    ResizeTo(_slots.Length << 1);

                var slots = _slots;
                var primaryTags = _primaryTags;
                var secondaryTags = _secondaryTags;
                var keys = _keys;
                var hash = ByteHashing.Hash(key.Span) & int.MaxValue;
                var primaryTag = (byte)((hash >> 24) | 0x80);
                var secondaryTag = (byte)((hash >> 16) & 0xFF);
                var mask = slots.Length - 1;
                var slot = hash & mask;

                while (true)
                {
                    var slotPrimaryTag = primaryTags[slot];
                    if (slotPrimaryTag == 0)
                    {
                        var index = Count++;
                        keys[index] = key;
                        slots[slot] = index + 1;
                        primaryTags[slot] = primaryTag;
                        secondaryTags[slot] = secondaryTag;
                        return index;
                    }

                    if (slotPrimaryTag == primaryTag && secondaryTags[slot] == secondaryTag)
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
                var newPrimaryTags = new byte[slotLength];
                var newSecondaryTags = new byte[slotLength];
                if (_keys.Length < slotLength)
                    Array.Resize(ref _keys, slotLength);

                _threshold = checked((int)(slotLength * LoadFactor));
                if (Count == 0)
                {
                    _slots = newSlots;
                    _primaryTags = newPrimaryTags;
                    _secondaryTags = newSecondaryTags;
                    return;
                }

                var mask = slotLength - 1;
                for (var i = 0; i < Count; i++)
                {
                    var key = _keys[i];
                    var hash = ByteHashing.Hash(key.Span) & int.MaxValue;
                    var primaryTag = (byte)((hash >> 24) | 0x80);
                    var secondaryTag = (byte)((hash >> 16) & 0xFF);
                    var slot = hash & mask;
                    while (newPrimaryTags[slot] != 0)
                        slot = (slot + 1) & mask;
                    newSlots[slot] = i + 1;
                    newPrimaryTags[slot] = primaryTag;
                    newSecondaryTags[slot] = secondaryTag;
                }

                _slots = newSlots;
                _primaryTags = newPrimaryTags;
                _secondaryTags = newSecondaryTags;
            }
        }

        public sealed class MissBounded : IExperimentDictionary<ReadOnlyMemory<byte>>
        {
            public static string ExperimentName => "hash.linear.tagged.ultra-sparse.tuned.miss-bounded.v1";

            public static Type? ParentExperimentType => typeof(Tuned);

            public static string ExperimentDescription => "UTF-8 tagged ultra-sparse tuned probing with per-home max-distance bounds to cut miss probes.";

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
                    var slotTag = tags[slot];
                    if (slotTag == 0)
                    {
                        var index = Count++;
                        keys[index] = key;
                        slots[slot] = index + 1;
                        tags[slot] = tag;
                        if (probeDistance > maxDistances[home])
                            maxDistances[home] = checked((ushort)probeDistance);
                        return index;
                    }

                    if (slotTag == tag)
                    {
                        var existingIndex = slots[slot] - 1;
                        if (keys[existingIndex].Span.SequenceEqual(key.Span))
                            return existingIndex;
                    }

                    slot = (slot + 1) & mask;
                    probeDistance++;
                }

                while (true)
                {
                    if (tags[slot] == 0)
                    {
                        var index = Count++;
                        keys[index] = key;
                        slots[slot] = index + 1;
                        tags[slot] = tag;
                        if (probeDistance > maxDistances[home])
                            maxDistances[home] = checked((ushort)probeDistance);
                        return index;
                    }

                    slot = (slot + 1) & mask;
                    probeDistance++;
                }
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
        }
    }
}
