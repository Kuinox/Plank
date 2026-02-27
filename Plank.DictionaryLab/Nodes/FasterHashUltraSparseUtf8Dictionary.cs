using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Numerics;

namespace Plank.DictionaryLab.Nodes;

/// <summary>
/// Fast hash helpers shared by the FasterHashUltraSparse variants.
/// All existing UTF-8 dictionary implementations use byte-per-byte FNV-1a.
/// These helpers read 4 bytes at a time, giving ~2-4x fewer multiply operations.
/// </summary>
static class Utf8FastHash
{
    // Light hash: 1 XOR + 1 MUL per 4 bytes, light two-step finalize.
    // Knuth multiplicative constant (golden ratio * 2^32).
    // For 8-byte keys: ~14 cycles vs FNV-1a's ~40 cycles.
    public static int Light(ReadOnlySpan<byte> data)
    {
        unchecked
        {
            ref byte ptr = ref MemoryMarshal.GetReference(data);
            uint h = (uint)data.Length * 2654435761u;
            int i = 0;

            while (i + 4 <= data.Length)
            {
                uint k = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref ptr, i));
                h ^= k;
                h *= 2654435761u;
                i += 4;
            }

            if (i + 2 <= data.Length)
            {
                h ^= Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref ptr, i));
                h *= 2654435761u;
                i += 2;
            }

            if (i < data.Length)
            {
                h ^= Unsafe.Add(ref ptr, i);
                h *= 2654435761u;
            }

            h ^= h >> 16;
            h *= 0x45d9f3bu;
            h ^= h >> 16;
            return (int)h;
        }
    }

    // Murmur3_x86_32: 2 MUL + 2 ROT per 4 bytes, full avalanche finalize.
    // Better distribution than Light; slightly more compute per iteration.
    public static int Murmur3(ReadOnlySpan<byte> data)
    {
        unchecked
        {
            ref byte ptr = ref MemoryMarshal.GetReference(data);
            uint h = (uint)data.Length;
            int i = 0;

            while (i + 4 <= data.Length)
            {
                uint k = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref ptr, i));
                k *= 0xcc9e2d51u;
                k = BitOperations.RotateLeft(k, 15);
                k *= 0x1b873593u;
                h ^= k;
                h = BitOperations.RotateLeft(h, 13);
                h = h * 5u + 0xe6546b64u;
                i += 4;
            }

            uint tail = 0;
            switch (data.Length - i)
            {
                case 3:
                    tail = (uint)Unsafe.Add(ref ptr, i)
                         | (uint)Unsafe.Add(ref ptr, i + 1) << 8
                         | (uint)Unsafe.Add(ref ptr, i + 2) << 16;
                    break;
                case 2:
                    tail = (uint)Unsafe.Add(ref ptr, i)
                         | (uint)Unsafe.Add(ref ptr, i + 1) << 8;
                    break;
                case 1:
                    tail = Unsafe.Add(ref ptr, i);
                    break;
            }

            if ((data.Length & 3) != 0)
            {
                tail *= 0xcc9e2d51u;
                tail = BitOperations.RotateLeft(tail, 15);
                tail *= 0x1b873593u;
                h ^= tail;
            }

            h ^= (uint)data.Length;
            h ^= h >> 16;
            h *= 0x85ebca6bu;
            h ^= h >> 13;
            h *= 0xc2b2ae35u;
            h ^= h >> 16;
            return (int)h;
        }
    }

    public static int Pow2(int value)
    {
        var size = 16;
        while (size < value)
            size <<= 1;
        return size;
    }
}

/// <summary>
/// Ultra-sparse linear probing with faster UTF-8 hash functions.
/// FNV-1a (used by all existing implementations) processes one byte at a time (N multiplies for N bytes).
/// These variants read 4 bytes at a time, giving ~2-4x fewer iterations for typical 7-12 byte key lengths.
/// </summary>
public sealed class FasterHashUltraSparseUtf8Dictionary : IExperimentDictionary<ReadOnlyMemory<byte>>
{
    public static string ExperimentName => "hash.linear.tagged.ultra-sparse.fast-hash.light.v1";

    public static Type? ParentExperimentType => typeof(TaggedLinearProbingUltraSparseUtf8Dictionary);

    public static string ExperimentDescription =>
        "Ultra-sparse (25% load) with a light 4-bytes-at-a-time XOR+MUL hash replacing byte-per-byte FNV-1a. " +
        "~4x fewer multiply operations for typical 8-12 byte UTF-8 keys.";

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
            ResizeTo(Utf8FastHash.Pow2(minimumCapacity));
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
        var hash = Utf8FastHash.Light(key.Span) & int.MaxValue;
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
            var hash = Utf8FastHash.Light(key.Span) & int.MaxValue;
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

    /// <summary>Light hash + touched-slot reset for fast Reset().</summary>
    public sealed class Touched : IExperimentDictionary<ReadOnlyMemory<byte>>
    {
        public static string ExperimentName => "hash.linear.tagged.ultra-sparse.fast-hash.light.touched.v1";

        public static Type? ParentExperimentType => typeof(FasterHashUltraSparseUtf8Dictionary);

        public static string ExperimentDescription =>
            "Ultra-sparse light 4-bytes-at-a-time hash + touched-slot reset to avoid full array clear.";

        const float LoadFactor = 0.25f;

        int[] _slots = [];
        byte[] _tags = [];
        ReadOnlyMemory<byte>[] _keys = [];
        int[] _touched = [];
        int _touchedCount;
        int _threshold;

        public int Count { get; private set; }

        public void Reset(int capacity)
        {
            var minimumCapacity = Math.Max(16, checked((int)(capacity / LoadFactor) + 1));
            if (_slots.Length < minimumCapacity)
                ResizeToEmpty(Utf8FastHash.Pow2(minimumCapacity));
            else
            {
                var slots = _slots;
                var tags = _tags;
                var touched = _touched;
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
            var hash = Utf8FastHash.Light(key.Span) & int.MaxValue;
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
                    _touched[_touchedCount++] = slot;
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
            _touched = new int[slotLength];
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
                _touched = newTouched;
                _touchedCount = 0;
                return;
            }

            var mask = slotLength - 1;
            var touchedCount = 0;
            for (var i = 0; i < Count; i++)
            {
                var key = _keys[i];
                var hash = Utf8FastHash.Light(key.Span) & int.MaxValue;
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
            _touched = newTouched;
            _touchedCount = touchedCount;
        }
    }

    /// <summary>
    /// Murmur3_x86_32 body (4 bytes/iteration) — better avalanche than the light hash.
    /// Comparison point to see if hash quality vs speed trade-off matters for this workload.
    /// </summary>
    public sealed class Murmur3 : IExperimentDictionary<ReadOnlyMemory<byte>>
    {
        public static string ExperimentName => "hash.linear.tagged.ultra-sparse.fast-hash.murmur3.v1";

        public static Type? ParentExperimentType => typeof(FasterHashUltraSparseUtf8Dictionary);

        public static string ExperimentDescription =>
            "Ultra-sparse (25% load) with Murmur3_x86_32 (4 bytes/iter, full finalize) replacing FNV-1a.";

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
                ResizeTo(Utf8FastHash.Pow2(minimumCapacity));
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
            var hash = Utf8FastHash.Murmur3(key.Span) & int.MaxValue;
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
                var hash = Utf8FastHash.Murmur3(key.Span) & int.MaxValue;
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
    }

    /// <summary>Murmur3 hash + touched-slot reset.</summary>
    public sealed class Murmur3Touched : IExperimentDictionary<ReadOnlyMemory<byte>>
    {
        public static string ExperimentName => "hash.linear.tagged.ultra-sparse.fast-hash.murmur3.touched.v1";

        public static Type? ParentExperimentType => typeof(Murmur3);

        public static string ExperimentDescription =>
            "Ultra-sparse Murmur3 4-bytes-at-a-time hash + touched-slot reset to avoid full array clear.";

        const float LoadFactor = 0.25f;

        int[] _slots = [];
        byte[] _tags = [];
        ReadOnlyMemory<byte>[] _keys = [];
        int[] _touched = [];
        int _touchedCount;
        int _threshold;

        public int Count { get; private set; }

        public void Reset(int capacity)
        {
            var minimumCapacity = Math.Max(16, checked((int)(capacity / LoadFactor) + 1));
            if (_slots.Length < minimumCapacity)
                ResizeToEmpty(Utf8FastHash.Pow2(minimumCapacity));
            else
            {
                var slots = _slots;
                var tags = _tags;
                var touched = _touched;
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
            var hash = Utf8FastHash.Murmur3(key.Span) & int.MaxValue;
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
                    _touched[_touchedCount++] = slot;
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
            _touched = new int[slotLength];
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
                _touched = newTouched;
                _touchedCount = 0;
                return;
            }

            var mask = slotLength - 1;
            var touchedCount = 0;
            for (var i = 0; i < Count; i++)
            {
                var key = _keys[i];
                var hash = Utf8FastHash.Murmur3(key.Span) & int.MaxValue;
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
            _touched = newTouched;
            _touchedCount = touchedCount;
        }
    }
}
