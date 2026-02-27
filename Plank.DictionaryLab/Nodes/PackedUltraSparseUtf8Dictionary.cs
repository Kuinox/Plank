using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Numerics;

namespace Plank.DictionaryLab.Nodes;

/// <summary>
/// Tag and index packed into a single uint per slot: top 8 bits = tag, low 24 bits = index+1.
/// Same layout as the string version but for UTF-8 byte sequences.
/// Also includes a FastHash variant that processes 4 bytes per iteration vs FNV-1a's 1 byte.
/// </summary>
public sealed class PackedUltraSparseUtf8Dictionary : IExperimentDictionary<ReadOnlyMemory<byte>>
{
    public static string ExperimentName => "hash.linear.packed.ultra-sparse.v1";

    public static Type? ParentExperimentType => typeof(TaggedLinearProbingUltraSparseUtf8Dictionary);

    public static string ExperimentDescription =>
        "UTF-8 tag+index packed into a single uint per slot (top 8 bits = tag, low 24 bits = index+1), 25% load. " +
        "One array read per probe eliminates the separate tags/slots cache miss split.";

    const float LoadFactor = 0.25f;

    uint[] _table = [];
    ReadOnlyMemory<byte>[] _keys = [];
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

    public int GetOrAddIndex(ReadOnlyMemory<byte> key)
    {
        if (Count >= _threshold)
            ResizeTo(_table.Length << 1);

        var table = _table;
        var keys = _keys;
        var hash = ByteHashing.Hash(key.Span) & int.MaxValue;
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
                if (keys[existingIndex].Span.SequenceEqual(key.Span))
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
            var hash = ByteHashing.Hash(key.Span) & int.MaxValue;
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
    /// Packed table + touched-slot reset for fast Reset without clearing the full 4x table.
    /// </summary>
    public sealed class Touched : IExperimentDictionary<ReadOnlyMemory<byte>>
    {
        public static string ExperimentName => "hash.linear.packed.ultra-sparse.touched.v1";

        public static Type? ParentExperimentType => typeof(PackedUltraSparseUtf8Dictionary);

        public static string ExperimentDescription =>
            "UTF-8 packed tag+index, 25% load, touched-slot reset avoids clearing the full 4x table on Reset.";

        const float LoadFactor = 0.25f;

        uint[] _table = [];
        ReadOnlyMemory<byte>[] _keys = [];
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

        public int GetOrAddIndex(ReadOnlyMemory<byte> key)
        {
            if (Count >= _threshold)
                ResizeTo(_table.Length << 1);

            var table = _table;
            var keys = _keys;
            var hash = ByteHashing.Hash(key.Span) & int.MaxValue;
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
                    if (keys[existingIndex].Span.SequenceEqual(key.Span))
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
                var hash = ByteHashing.Hash(key.Span) & int.MaxValue;
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

    /// <summary>
    /// Packed table + Murmur3-inspired hash reading 4 bytes at a time.
    /// FNV-1a does 1 multiply per byte; this does ~2 multiplies per 4 bytes — about 2× fewer
    /// multiply operations for typical string lengths (7-12 bytes).
    /// </summary>
    public sealed class FastHash : IExperimentDictionary<ReadOnlyMemory<byte>>
    {
        public static string ExperimentName => "hash.linear.packed.ultra-sparse.fast-hash.v1";

        public static Type? ParentExperimentType => typeof(PackedUltraSparseUtf8Dictionary);

        public static string ExperimentDescription =>
            "UTF-8 packed table + 4-bytes-at-a-time Murmur3-style hash instead of byte-per-byte FNV-1a, 25% load.";

        const float LoadFactor = 0.25f;

        uint[] _table = [];
        ReadOnlyMemory<byte>[] _keys = [];
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

        public int GetOrAddIndex(ReadOnlyMemory<byte> key)
        {
            if (Count >= _threshold)
                ResizeTo(_table.Length << 1);

            var table = _table;
            var keys = _keys;
            var hash = Hash(key.Span) & int.MaxValue;
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
                    if (keys[existingIndex].Span.SequenceEqual(key.Span))
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
                var hash = Hash(key.Span) & int.MaxValue;
                var tag = (uint)((hash >> 24) | 0x80);
                var slot = hash & mask;
                while (newTable[slot] != 0)
                    slot = (slot + 1) & mask;
                newTable[slot] = (tag << 24) | (uint)(i + 1);
            }

            _table = newTable;
        }

        // Murmur3_x86_32 — processes 4 bytes per main loop iteration.
        // ~2x fewer multiply operations than byte-per-byte FNV-1a for 8+ byte keys.
        static int Hash(ReadOnlySpan<byte> data)
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

                // Tail (0-3 remaining bytes)
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

                // Finalize
                h ^= (uint)data.Length;
                h ^= h >> 16;
                h *= 0x85ebca6bu;
                h ^= h >> 13;
                h *= 0xc2b2ae35u;
                h ^= h >> 16;
                return (int)h;
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

    /// <summary>
    /// Packed table + FastHash + touched-slot reset — combines all three improvements.
    /// </summary>
    public sealed class FastHashTouched : IExperimentDictionary<ReadOnlyMemory<byte>>
    {
        public static string ExperimentName => "hash.linear.packed.ultra-sparse.fast-hash.touched.v1";

        public static Type? ParentExperimentType => typeof(FastHash);

        public static string ExperimentDescription =>
            "UTF-8 packed table + 4-bytes-at-a-time Murmur3 hash + touched-slot reset, 25% load.";

        const float LoadFactor = 0.25f;

        uint[] _table = [];
        ReadOnlyMemory<byte>[] _keys = [];
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

        public int GetOrAddIndex(ReadOnlyMemory<byte> key)
        {
            if (Count >= _threshold)
                ResizeTo(_table.Length << 1);

            var table = _table;
            var keys = _keys;
            var hash = Hash(key.Span) & int.MaxValue;
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
                    if (keys[existingIndex].Span.SequenceEqual(key.Span))
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
                var hash = Hash(key.Span) & int.MaxValue;
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

        static int Hash(ReadOnlySpan<byte> data)
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

        static int Pow2(int value)
        {
            var size = 16;
            while (size < value)
                size <<= 1;
            return size;
        }
    }
}
