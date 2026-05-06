using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Plank.Writing.Encoding;

/// <summary>
/// Reusable dictionary that maps T → insertion-order index (0, 1, 2, ...).
/// Backed by a packed ultra-sparse linear-probing hash table:
///   - Each slot is a single uint: top 8 bits = fingerprint tag, low 24 bits = (index+1). Zero = empty.
///   - 25% load factor keeps average probe distance ~1.17 → almost always 1–2 reads per lookup.
///   - Touched-slot list: Reset only zeroes occupied slots instead of clearing the full table.
/// Hash function: wyhash (MemoryMarshal raw bytes for string/ROM&lt;byte&gt;/byte[],
///                         GetHashCode() for value types).
/// </summary>
sealed class ReusableDictionaryState<T>
    where T : notnull
{
    T[] _values = [];
    int _count;
    bool _mapEnabled;
    int _initialUniqueCapacity;

    // Packed table: entry = (tag << 24) | (index+1), 0 = empty.
    // tag = (hash >> 24) | 0x80 so all occupied entries have bit7 of top byte set.
    uint[] _table = [];
    int[] _touched = [];   // indices into _table that were written since last clear
    int _touchedCount;
    int _threshold;        // grow table when _count reaches this

    public bool IsMapEnabled => _mapEnabled;
    public int Count => _count;

    public void Reset(int initialUniqueCapacity, bool useMap, IEqualityComparer<T> comparer)
    {
        var previousCount = _count;
        if (previousCount > 0 && RuntimeHelpers.IsReferenceOrContainsReferences<T>())
            Array.Clear(_values, 0, previousCount);

        _initialUniqueCapacity = initialUniqueCapacity;
        _count = 0;
        _mapEnabled = useMap;
        if (!_mapEnabled)
        {
            EnsureValueCapacity(initialUniqueCapacity);
            return;
        }

        var minimumTableSize = Math.Max(16, checked((int)(initialUniqueCapacity / 0.25f) + 1));
        if (_table.Length < minimumTableSize)
            ResizeToEmpty(Pow2(minimumTableSize));
        else
            ClearTouched();
    }

    public int AddFirst(T value)
    {
        EnsureValueCapacity(1);
        _values[0] = value;
        _count = 1;
        if (_mapEnabled)
            InsertKnownNew(value, 0);
        return 0;
    }

    public int AddSortedUnique(T value)
    {
        EnsureValueCapacity(_count + 1);
        var index = _count;
        _values[index] = value;
        _count++;
        if (_mapEnabled)
            InsertKnownNew(value, index);
        return index;
    }

    public void EnableMap()
    {
        if (_mapEnabled)
            return;

        var capacity = Math.Max(_initialUniqueCapacity, _count);
        var minimumTableSize = Math.Max(16, checked((int)(capacity / 0.25f) + 1));
        if (_table.Length < minimumTableSize)
            ResizeToEmpty(Pow2(minimumTableSize));
        else
            ClearTouched();

        for (var i = 0; i < _count; i++)
            InsertKnownNew(_values[i], i);

        _mapEnabled = true;
    }

    public int GetOrAddIndex(T value)
    {
        if (!_mapEnabled)
            throw new InvalidOperationException("Dictionary map is not enabled.");

        if (_count >= _threshold)
            Resize();

        var table = _table;
        var hash = HashKey(value);
        var tag = (uint)((hash >> 24) | 0x80);
        var mask = table.Length - 1;
        var slot = hash & mask;

        while (true)
        {
            var entry = table[slot];
            if (entry == 0)
            {
                var index = _count;
                _values[index] = value;
                _count++;
                table[slot] = (tag << 24) | (uint)(index + 1);
                _touched[_touchedCount++] = slot;
                return index;
            }

            if (entry >> 24 == tag)
            {
                var existingIndex = (int)(entry & 0x00FFFFFFu) - 1;
                if (KeysEqual(_values[existingIndex], value))
                    return existingIndex;
            }

            slot = (slot + 1) & mask;
        }
    }

    public ReadOnlySpan<T> AsSpan() => _values.AsSpan(0, _count);

    // Insert a value at a known index without checking for duplicates.
    // Caller guarantees the table has capacity (threshold not exceeded) and value is new.
    void InsertKnownNew(T value, int index)
    {
        var table = _table;
        var hash = HashKey(value);
        var tag = (uint)((hash >> 24) | 0x80);
        var mask = table.Length - 1;
        var slot = hash & mask;

        while (table[slot] != 0)
            slot = (slot + 1) & mask;

        table[slot] = (tag << 24) | (uint)(index + 1);
        _touched[_touchedCount++] = slot;
    }

    void ClearTouched()
    {
        var table = _table;
        var touched = _touched;
        for (var i = 0; i < _touchedCount; i++)
            table[touched[i]] = 0;
        _touchedCount = 0;
    }

    void Resize()
    {
        var newSize = Math.Max(32, _table.Length << 1);
        var newTable = new uint[newSize];
        var newTouched = new int[newSize];

        _threshold = checked((int)(newSize * 0.25f));
        EnsureValueCapacity(_threshold);
        var mask = newSize - 1;
        var touchedCount = 0;
        for (var i = 0; i < _count; i++)
        {
            var hash = HashKey(_values[i]);
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

    void ResizeToEmpty(int slotLength)
    {
        _table = new uint[slotLength];
        _touched = new int[slotLength];
        _threshold = checked((int)(slotLength * 0.25f));
        EnsureValueCapacity(_threshold);
        _touchedCount = 0;
    }

    void EnsureValueCapacity(int required)
    {
        if (_values.Length >= required)
            return;
        var newCapacity = _values.Length == 0 ? 4 : _values.Length;
        while (newCapacity < required)
            newCapacity *= 2;
        Array.Resize(ref _values, newCapacity);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static int HashKey(T key)
    {
        if (typeof(T) == typeof(string))
            return WyHashing.Hash(MemoryMarshal.AsBytes(Unsafe.As<T, string>(ref key).AsSpan())) & int.MaxValue;
        if (typeof(T) == typeof(ReadOnlyMemory<byte>))
            return WyHashing.Hash(Unsafe.As<T, ReadOnlyMemory<byte>>(ref key).Span) & int.MaxValue;
        if (typeof(T) == typeof(byte[]))
            return WyHashing.Hash(Unsafe.As<T, byte[]>(ref key)) & int.MaxValue;
        // float.GetHashCode() = raw IEEE 754 bits with no mixing.
        // For data like (i%10000)/3f, all values share the same lower 16 mantissa bits
        // (binary 1/3 = repeating 0.010101...) → catastrophic clustering into ~48/65536 slots.
        // Murmur3 finalizer distributes bits uniformly.
        if (typeof(T) == typeof(float))
        {
            uint bits = Unsafe.As<T, uint>(ref key);
            bits ^= bits >> 16;
            bits *= 0x45d9f3bu;
            bits ^= bits >> 16;
            return (int)(bits & (uint)int.MaxValue);
        }
        // double.GetHashCode() XORs high/low 32 bits, which is better than float but still
        // has structure that can cluster. Apply the same finalizer for consistency.
        if (typeof(T) == typeof(double))
        {
            ulong bits64 = Unsafe.As<T, ulong>(ref key);
            uint bits = (uint)(bits64 ^ (bits64 >> 32));
            bits ^= bits >> 16;
            bits *= 0x45d9f3bu;
            bits ^= bits >> 16;
            return (int)(bits & (uint)int.MaxValue);
        }
        return key.GetHashCode() & int.MaxValue;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static bool KeysEqual(T a, T b)
    {
        if (typeof(T) == typeof(string))
            return string.Equals(Unsafe.As<T, string>(ref a), Unsafe.As<T, string>(ref b), StringComparison.Ordinal);
        if (typeof(T) == typeof(ReadOnlyMemory<byte>))
            return Unsafe.As<T, ReadOnlyMemory<byte>>(ref a).Span.SequenceEqual(
                Unsafe.As<T, ReadOnlyMemory<byte>>(ref b).Span);
        if (typeof(T) == typeof(byte[]))
            return Unsafe.As<T, byte[]>(ref a).AsSpan().SequenceEqual(Unsafe.As<T, byte[]>(ref b).AsSpan());
        return EqualityComparer<T>.Default.Equals(a, b);
    }

    static int Pow2(int value)
    {
        var size = 16;
        while (size < value)
            size <<= 1;
        return size;
    }
}
