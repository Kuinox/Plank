using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Plank.DictionaryLab.Nodes;

/// <summary>
/// wyhash for UTF-8 byte spans.
/// FNV-1a processes one byte at a time: N sequential XOR+MUL operations with a full data-dependency chain.
/// For a 12-byte key that's 12 dependent multiplications (~36 cycle minimum latency).
///
/// wyhash instead reads 4 or 8 bytes at a time using two OVERLAPPING reads:
/// - 4-8 byte keys:  1 uint read + 1 uint read (with overlap) + 1 BigMul (~10 cycles)
/// - 9-16 byte keys: 1 ulong read + 1 ulong read (with overlap) + 2 BigMul in parallel (~12 cycles)
///
/// BigMul (64×64→128 bit) maps to a single MULQ on x86-64.
/// The two reads are independent so the CPU can issue them in parallel.
/// </summary>
static class WyHashing
{
    const ulong P0 = 0xa0761d6478bd642fUL;
    const ulong P1 = 0xe7037ed1a0b428dbUL;
    const ulong P2 = 0x8ebc6af09c88c6e3UL;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static ulong Mix(ulong a, ulong b)
    {
        ulong hi = Math.BigMul(a, b, out ulong lo);
        return hi ^ lo;
    }

    // Full-avalanche 32-bit finalizer. Required because BigMul with a small seed (e.g., key length = 9)
    // concentrates variation in high bits of the product. Simple h^(h>>32) folds to bits 24-31
    // which are still above typical table slot masks (4K-2M slots). The extra finalize cost (~10 cycles)
    // is still far less than FNV-1a's full chain (~40 cycles).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static int Finalize(ulong h)
    {
        uint f = (uint)(h ^ (h >> 32));
        f ^= f >> 16;
        f *= 0x45d9f3bu;
        f ^= f >> 16;
        return (int)f;
    }

    public static int Hash(ReadOnlySpan<byte> data)
    {
        unchecked
        {
            ref byte p = ref MemoryMarshal.GetReference(data);
            ulong seed = (ulong)data.Length;

            if (data.Length <= 3)
            {
                ulong v = data.Length > 0 ? p : 0u;
                if (data.Length > 1) v |= (ulong)Unsafe.Add(ref p, 1) << 8;
                if (data.Length > 2) v |= (ulong)Unsafe.Add(ref p, 2) << 16;
                return Finalize(Mix(seed ^ P0, v ^ P1));
            }

            if (data.Length <= 8)
            {
                // Two overlapping 4-byte reads: first4 in low bits, last4 in high bits.
                ulong a = Unsafe.ReadUnaligned<uint>(ref p);
                ulong b = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref p, data.Length - 4));
                return Finalize(Mix(seed ^ P0, (a | (b << 32)) ^ P1));
            }

            if (data.Length <= 16)
            {
                // Two overlapping 8-byte reads covering the full span, issued independently.
                ulong a = Unsafe.ReadUnaligned<ulong>(ref p);
                ulong b = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref p, data.Length - 8));
                return Finalize(Mix(seed ^ P0, a ^ P1) ^ Mix(seed, b ^ P2));
            }

            // Longer keys: process 16-byte blocks then handle tail
            ulong acc = seed;
            int i = 0;
            while (i + 16 <= data.Length)
            {
                ulong la = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref p, i));
                ulong lb = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref p, i + 8));
                acc ^= Mix(acc ^ P0, la ^ P1) ^ Mix(acc, lb ^ P2);
                i += 16;
            }
            int tail = data.Length - i;
            if (tail > 0)
            {
                if (tail <= 8)
                {
                    ulong ta = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref p, i));
                    ulong tb = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref p, data.Length - 4));
                    acc ^= Mix(acc ^ P0, (ta | (tb << 32)) ^ P1);
                }
                else
                {
                    ulong ta = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref p, i));
                    ulong tb = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref p, data.Length - 8));
                    acc ^= Mix(acc ^ P0, ta ^ P1) ^ Mix(acc, tb ^ P2);
                }
            }
            return Finalize(acc);
        }
    }
}

/// <summary>
/// Ultra-sparse linear probing using wyhash instead of FNV-1a.
/// Identical structure to TaggedLinearProbingUltraSparseUtf8Dictionary (the current champion).
/// </summary>
public sealed class WyHashUltraSparseUtf8Dictionary : IExperimentDictionary<ReadOnlyMemory<byte>>
{
    public static string ExperimentName => "hash.linear.tagged.ultra-sparse.wyhash.v1";
    public static Type? ParentExperimentType => typeof(TaggedLinearProbingUltraSparseUtf8Dictionary);
    public static string ExperimentDescription =>
        "Ultra-sparse (25% load) with wyhash replacing FNV-1a. " +
        "wyhash uses 2 overlapping 8-byte reads + 2 BigMul (MULQ) for 9-16 byte keys: ~12 cycles vs FNV-1a's ~36.";

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
        var hash = WyHashing.Hash(key.Span) & int.MaxValue;
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
            if (tags[slot] == tag && keys[existingIndex].Span.SequenceEqual(key.Span))
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
            var hash = WyHashing.Hash(key.Span) & int.MaxValue;
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

    /// <summary>wyhash + touched-slot reset (avoids full Array.Clear on Reset).</summary>
    public sealed class Touched : IExperimentDictionary<ReadOnlyMemory<byte>>
    {
        public static string ExperimentName => "hash.linear.tagged.ultra-sparse.wyhash.touched.v1";
        public static Type? ParentExperimentType => typeof(WyHashUltraSparseUtf8Dictionary);
        public static string ExperimentDescription =>
            "Ultra-sparse wyhash + touched-slot reset.";

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

        public int GetOrAddIndex(ReadOnlyMemory<byte> key)
        {
            if (Count >= _threshold)
                ResizeTo(_slots.Length << 1);

            var slots = _slots;
            var tags = _tags;
            var keys = _keys;
            var hash = WyHashing.Hash(key.Span) & int.MaxValue;
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
                var hash = WyHashing.Hash(key.Span) & int.MaxValue;
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

    /// <summary>
    /// wyhash + "tuned" tag-first probe order (check tag before loading slot index).
    /// The Tuned variant reads tag first, then slot only on a tag match —
    /// avoids loading _slots[] on empty-slot and tag-mismatch cases.
    /// </summary>
    public sealed class Tuned : IExperimentDictionary<ReadOnlyMemory<byte>>
    {
        public static string ExperimentName => "hash.linear.tagged.ultra-sparse.wyhash.tuned.v1";
        public static Type? ParentExperimentType => typeof(WyHashUltraSparseUtf8Dictionary);
        public static string ExperimentDescription =>
            "wyhash + tag-first probe: read _tags[slot] first, load _slots[slot] only on tag match.";

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
            var hash = WyHashing.Hash(key.Span) & int.MaxValue;
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
                var hash = WyHashing.Hash(key.Span) & int.MaxValue;
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

    /// <summary>
    /// wyhash + slot-first ILP probe (read _slots first so tag load and key load can issue in parallel)
    /// + touched-slot reset (selective clear of occupied slots only, fallback to full clear when touched &gt; 25% of table).
    /// </summary>
    public sealed class BasicTouched : IExperimentDictionary<ReadOnlyMemory<byte>>
    {
        public static string ExperimentName => "hash.linear.tagged.ultra-sparse.wyhash.basic-touched.v1";
        public static Type? ParentExperimentType => typeof(WyHashUltraSparseUtf8Dictionary);
        public static string ExperimentDescription =>
            "wyhash + slot-first ILP probe (parallel tag+key loads on hit path) + touched-slot reset " +
            "(clears only occupied slots on Reset; falls back to full Array.Clear when > 25% occupied).";

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

        public int GetOrAddIndex(ReadOnlyMemory<byte> key)
        {
            if (Count >= _threshold)
                ResizeTo(_slots.Length << 1);

            var slots = _slots;
            var tags = _tags;
            var keys = _keys;
            var hash = WyHashing.Hash(key.Span) & int.MaxValue;
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
                if (tags[slot] == tag && keys[existingIndex].Span.SequenceEqual(key.Span))
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
                var hash = WyHashing.Hash(key.Span) & int.MaxValue;
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

    /// <summary>
    /// wyhash + tag-first probe + 4-step unrolled probe loop.
    /// Unrolling reduces branch/loop overhead and lets the CPU schedule 4 sequential tag reads
    /// (often in the same cache line) as independent loads.
    /// </summary>
    public sealed class TunedUnrolled4 : IExperimentDictionary<ReadOnlyMemory<byte>>
    {
        public static string ExperimentName => "hash.linear.tagged.ultra-sparse.wyhash.tuned.unrolled4.v1";
        public static Type? ParentExperimentType => typeof(WyHashUltraSparseUtf8Dictionary.Tuned);
        public static string ExperimentDescription =>
            "wyhash + tag-first probe with 4-step unrolled loop: reduces per-probe branch overhead " +
            "and enables better scheduling of sequential tag reads (often co-located on one cache line).";

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
            var hash = WyHashing.Hash(key.Span) & int.MaxValue;
            var tag = (byte)((hash >> 24) | 0x80);
            var mask = slots.Length - 1;
            var slot = hash & mask;

            while (true)
            {
                var t0 = tags[slot];
                if (t0 == 0) return Insert(slot, key, tag, slots, tags, keys);
                if (t0 == tag) { var idx = slots[slot] - 1; if (keys[idx].Span.SequenceEqual(key.Span)) return idx; }
                slot = (slot + 1) & mask;

                var t1 = tags[slot];
                if (t1 == 0) return Insert(slot, key, tag, slots, tags, keys);
                if (t1 == tag) { var idx = slots[slot] - 1; if (keys[idx].Span.SequenceEqual(key.Span)) return idx; }
                slot = (slot + 1) & mask;

                var t2 = tags[slot];
                if (t2 == 0) return Insert(slot, key, tag, slots, tags, keys);
                if (t2 == tag) { var idx = slots[slot] - 1; if (keys[idx].Span.SequenceEqual(key.Span)) return idx; }
                slot = (slot + 1) & mask;

                var t3 = tags[slot];
                if (t3 == 0) return Insert(slot, key, tag, slots, tags, keys);
                if (t3 == tag) { var idx = slots[slot] - 1; if (keys[idx].Span.SequenceEqual(key.Span)) return idx; }
                slot = (slot + 1) & mask;
            }
        }

        int Insert(int slot, ReadOnlyMemory<byte> key, byte tag, int[] slots, byte[] tags, ReadOnlyMemory<byte>[] keys)
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
                var hash = WyHashing.Hash(key.Span) & int.MaxValue;
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
