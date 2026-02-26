using System.Collections.Generic;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace Plank.Writing.Encoding;

sealed class ReusableDictionaryState<T>
    where T : notnull
{
    const float MaxLoadFactor = 0.80f;

    IEqualityComparer<T> _comparer = EqualityComparer<T>.Default;
    T[] _values = [];
    int[] _hashes = [];
    long[] _slotEntries = [];
    int _currentEpoch = 1;
    int _count;
    bool _mapEnabled;
    int _initialUniqueCapacity;

    public bool IsMapEnabled
        => _mapEnabled;

    public int Count
        => _count;

    public void Reset(int initialUniqueCapacity, bool useMap, IEqualityComparer<T> comparer)
    {
        _comparer = comparer;
        _initialUniqueCapacity = initialUniqueCapacity;
        EnsureValueCapacity(initialUniqueCapacity);
        _count = 0;
        _mapEnabled = useMap;
        if (!_mapEnabled)
            return;

        EnsureSlotCapacity(initialUniqueCapacity);
        StartNewEpoch();
    }

    public int AddFirst(T value)
    {
        EnsureValueCapacity(1);
        _values[0] = value;
        _hashes[0] = ComputeHash(value);
        _count = 1;
        if (_mapEnabled)
            InsertKnownIndex(0, _hashes[0]);
        return 0;
    }

    public int AddSortedUnique(T value)
    {
        EnsureValueCapacity(_count + 1);
        var index = _count;
        _values[index] = value;
        _hashes[index] = ComputeHash(value);
        _count++;
        if (_mapEnabled)
            InsertKnownIndex(index, _hashes[index]);
        return index;
    }

    public void EnableMap()
    {
        if (_mapEnabled)
            return;

        var capacity = Math.Max(_initialUniqueCapacity, _count);
        EnsureSlotCapacity(capacity);
        StartNewEpoch();
        for (var i = 0; i < _count; i++)
            InsertKnownIndex(i, _hashes[i]);
        _mapEnabled = true;
    }

    public int GetOrAddIndex(T value)
    {
        if (!_mapEnabled)
            throw new InvalidOperationException("Dictionary map is not enabled.");
        EnsureMapInsertCapacity();

        var hash = ComputeHash(value);
        var mask = _slotEntries.Length - 1;
        var position = hash & mask;

        while (true)
        {
            var entry = _slotEntries[position];
            if ((int)(entry >> 32) != _currentEpoch)
            {
                var index = AddNewValue(value, hash);
                _slotEntries[position] = PackEntry(_currentEpoch, index + 1);
                return index;
            }

            var existingIndex = ((int)entry) - 1;
            var existingHash = _hashes[existingIndex];
            if (existingHash == hash && KeysEqual(_values[existingIndex], value))
                return existingIndex;

            position = (position + 1) & mask;
        }
    }

    public ReadOnlySpan<T> AsSpan()
        => _values.AsSpan(0, _count);

    void EnsureMapInsertCapacity()
    {
        if (_slotEntries.Length == 0)
        {
            EnsureSlotCapacity(Math.Max(4, _count + 1));
            StartNewEpoch();
            return;
        }

        var maxCount = (int)(_slotEntries.Length * MaxLoadFactor);
        if (_count < maxCount)
            return;

        ResizeSlots(_slotEntries.Length * 2);
    }

    void ResizeSlots(int newSize)
    {
        var oldEntries = _slotEntries;
        var oldEpoch = _currentEpoch;
        _slotEntries = new long[newSize];
        _currentEpoch = 1;
        for (var i = 0; i < oldEntries.Length; i++)
        {
            var entry = oldEntries[i];
            if ((int)(entry >> 32) != oldEpoch)
                continue;
            var slot = (int)entry;
            if (slot == 0)
                continue;
            var index = slot - 1;
            InsertKnownIndex(index, _hashes[index]);
        }
    }

    int AddNewValue(T value, int hash)
    {
        EnsureValueCapacity(_count + 1);
        var index = _count;
        _values[index] = value;
        _hashes[index] = hash;
        _count++;
        return index;
    }

    void InsertKnownIndex(int index, int hash)
    {
        if (_slotEntries.Length == 0)
            EnsureSlotCapacity(Math.Max(4, _count));

        var mask = _slotEntries.Length - 1;
        var position = hash & mask;
        while ((int)(_slotEntries[position] >> 32) == _currentEpoch)
            position = (position + 1) & mask;
        _slotEntries[position] = PackEntry(_currentEpoch, index + 1);
    }

    int ComputeHash(T value)
    {
        int hash;
        if (typeof(T) == typeof(string))
            hash = Unsafe.As<T, string>(ref value).GetHashCode(StringComparison.Ordinal);
        else if (typeof(T) == typeof(byte[]))
            hash = HashBytes(Unsafe.As<T, byte[]>(ref value));
        else if (typeof(T) == typeof(ReadOnlyMemory<byte>))
            hash = HashBytes(Unsafe.As<T, ReadOnlyMemory<byte>>(ref value).Span);
        else
            hash = _comparer.GetHashCode(value);
        hash &= int.MaxValue;
        return hash == 0 ? 1 : hash;
    }

    void EnsureValueCapacity(int required)
    {
        if (_values.Length >= required)
            return;

        var newCapacity = _values.Length == 0 ? 4 : _values.Length;
        while (newCapacity < required)
            newCapacity *= 2;
        Array.Resize(ref _values, newCapacity);
        Array.Resize(ref _hashes, newCapacity);
    }

    void EnsureSlotCapacity(int requiredItems)
    {
        var minimumSize = (int)Math.Ceiling(requiredItems / MaxLoadFactor);
        var size = 4;
        while (size < minimumSize)
            size *= 2;
        if (_slotEntries.Length >= size)
            return;
        _slotEntries = new long[size];
        _currentEpoch = 1;
    }

    void StartNewEpoch()
    {
        if (_slotEntries.Length == 0)
            return;

        if (_currentEpoch == int.MaxValue)
        {
            Array.Clear(_slotEntries, 0, _slotEntries.Length);
            _currentEpoch = 1;
            return;
        }

        _currentEpoch++;
    }

    static long PackEntry(int epoch, int slotValue)
        => ((long)epoch << 32) | (uint)slotValue;

    bool KeysEqual(T left, T right)
    {
        if (typeof(T) == typeof(string))
            return string.Equals(Unsafe.As<T, string>(ref left), Unsafe.As<T, string>(ref right), StringComparison.Ordinal);
        if (typeof(T) == typeof(byte[]))
            return Unsafe.As<T, byte[]>(ref left).AsSpan().SequenceEqual(Unsafe.As<T, byte[]>(ref right));
        if (typeof(T) == typeof(ReadOnlyMemory<byte>))
            return Unsafe.As<T, ReadOnlyMemory<byte>>(ref left).Span.SequenceEqual(
                Unsafe.As<T, ReadOnlyMemory<byte>>(ref right).Span);
        return _comparer.Equals(left, right);
    }

    static int HashBytes(ReadOnlySpan<byte> bytes)
    {
        unchecked
        {
            var h1 = 0x811C9DC5u;
            var h2 = 0x9E3779B9u;
            var i = 0;
            while (i + 8 <= bytes.Length)
            {
                var v = BinaryPrimitives.ReadUInt64LittleEndian(bytes[i..]);
                h1 = (h1 ^ (uint)v) * 16777619u;
                h2 = (h2 ^ (uint)(v >> 32)) * 2246822519u;
                i += 8;
            }

            while (i < bytes.Length)
            {
                var b = bytes[i++];
                h1 = (h1 ^ b) * 16777619u;
                h2 = (h2 ^ b) * 2246822519u;
            }

            var mixed = h1 ^ RotateLeft(h2, 13) ^ (uint)bytes.Length;
            mixed ^= mixed >> 16;
            mixed *= 0x7FEB352Du;
            mixed ^= mixed >> 15;
            mixed *= 0x846CA68Bu;
            mixed ^= mixed >> 16;
            return (int)mixed;
        }
    }

    static int HashBytes(byte[] bytes)
        => HashBytes(bytes.AsSpan());

    static uint RotateLeft(uint value, int offset)
        => (value << offset) | (value >> (32 - offset));
}
