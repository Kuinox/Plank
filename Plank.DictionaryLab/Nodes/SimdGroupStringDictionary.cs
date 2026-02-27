using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace Plank.DictionaryLab.Nodes;

/// <summary>
/// Swiss Table–inspired group scanning for string keys: probe 16 tag bytes at once via Vector128&lt;byte&gt;.
/// </summary>
public sealed class SimdGroupUltraSparseStringDictionary : IExperimentDictionary<string>
{
    public static string ExperimentName => "hash.simd-group.ultra-sparse.v1";
    public static Type? ParentExperimentType => typeof(TaggedLinearProbingUltraSparseStringDictionary);
    public static string ExperimentDescription =>
        "Swiss Table–style group probing for strings: Vector128<byte> checks 16 tag slots per instruction.";

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
        var hash = key.GetHashCode() & int.MaxValue;
        var tag = (byte)((hash >> 24) | 0x80);
        var tagVec = Vector128.Create(tag);
        var tableLen = tags.Length;
        var groupMask = (tableLen >> 4) - 1;
        var startGroup = (hash & (tableLen - 1)) >> 4;

        ref byte tagsBase = ref MemoryMarshal.GetArrayDataReference(tags);

        for (int g = 0; g <= groupMask; g++)
        {
            var group = (startGroup + g) & groupMask;
            var groupOffset = group << 4;

            var groupTags = Vector128.LoadUnsafe(ref tagsBase, (nuint)groupOffset);

            var matchMask = Vector128.Equals(tagVec, groupTags).ExtractMostSignificantBits();
            while (matchMask != 0)
            {
                var pos = groupOffset + BitOperations.TrailingZeroCount(matchMask);
                var existingIndex = slots[pos] - 1;
                if (keys[existingIndex] == key)
                    return existingIndex;
                matchMask &= matchMask - 1;
            }

            var emptyMask = Vector128.Equals(Vector128<byte>.Zero, groupTags).ExtractMostSignificantBits();
            if (emptyMask != 0)
            {
                var pos = groupOffset + BitOperations.TrailingZeroCount(emptyMask);
                var index = Count++;
                keys[index] = key;
                slots[pos] = index + 1;
                tags[pos] = tag;
                return index;
            }
        }

        throw new InvalidOperationException("SimdGroup table full");
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

        var newMask = slotLength - 1;
        var newGroupMask = (slotLength >> 4) - 1;
        ref byte newTagsBase = ref MemoryMarshal.GetArrayDataReference(newTags);

        for (var i = 0; i < Count; i++)
        {
            var key = _keys[i];
            var hash = key.GetHashCode() & int.MaxValue;
            var tag = (byte)((hash >> 24) | 0x80);
            var startGroup = (hash & newMask) >> 4;

            for (int g = 0; g <= newGroupMask; g++)
            {
                var group = (startGroup + g) & newGroupMask;
                var groupOffset = group << 4;
                var groupTags = Vector128.LoadUnsafe(ref newTagsBase, (nuint)groupOffset);
                var emptyMask = Vector128.Equals(Vector128<byte>.Zero, groupTags).ExtractMostSignificantBits();
                if (emptyMask != 0)
                {
                    var pos = groupOffset + BitOperations.TrailingZeroCount(emptyMask);
                    newSlots[pos] = i + 1;
                    newTags[pos] = tag;
                    break;
                }
            }
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

    /// <summary>SIMD group probing + touched-slot reset.</summary>
    public sealed class Touched : IExperimentDictionary<string>
    {
        public static string ExperimentName => "hash.simd-group.ultra-sparse.touched.v1";
        public static Type? ParentExperimentType => typeof(SimdGroupUltraSparseStringDictionary);
        public static string ExperimentDescription =>
            "SIMD group probing for strings + touched-slot reset.";

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
            var hash = key.GetHashCode() & int.MaxValue;
            var tag = (byte)((hash >> 24) | 0x80);
            var tagVec = Vector128.Create(tag);
            var tableLen = tags.Length;
            var groupMask = (tableLen >> 4) - 1;
            var startGroup = (hash & (tableLen - 1)) >> 4;

            ref byte tagsBase = ref MemoryMarshal.GetArrayDataReference(tags);

            for (int g = 0; g <= groupMask; g++)
            {
                var group = (startGroup + g) & groupMask;
                var groupOffset = group << 4;

                var groupTags = Vector128.LoadUnsafe(ref tagsBase, (nuint)groupOffset);

                var matchMask = Vector128.Equals(tagVec, groupTags).ExtractMostSignificantBits();
                while (matchMask != 0)
                {
                    var pos = groupOffset + BitOperations.TrailingZeroCount(matchMask);
                    var existingIndex = slots[pos] - 1;
                    if (keys[existingIndex] == key)
                        return existingIndex;
                    matchMask &= matchMask - 1;
                }

                var emptyMask = Vector128.Equals(Vector128<byte>.Zero, groupTags).ExtractMostSignificantBits();
                if (emptyMask != 0)
                {
                    var pos = groupOffset + BitOperations.TrailingZeroCount(emptyMask);
                    var index = Count++;
                    keys[index] = key;
                    slots[pos] = index + 1;
                    tags[pos] = tag;
                    _touchedSlots[_touchedCount++] = pos;
                    return index;
                }
            }

            throw new InvalidOperationException("Table full");
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

            var newMask = slotLength - 1;
            var newGroupMask = (slotLength >> 4) - 1;
            ref byte newTagsBase = ref MemoryMarshal.GetArrayDataReference(newTags);
            var touchedCount = 0;

            for (var i = 0; i < Count; i++)
            {
                var key = _keys[i];
                var hash = key.GetHashCode() & int.MaxValue;
                var tag = (byte)((hash >> 24) | 0x80);
                var startGroup = (hash & newMask) >> 4;

                for (int g = 0; g <= newGroupMask; g++)
                {
                    var group = (startGroup + g) & newGroupMask;
                    var groupOffset = group << 4;
                    var groupTags = Vector128.LoadUnsafe(ref newTagsBase, (nuint)groupOffset);
                    var emptyMask = Vector128.Equals(Vector128<byte>.Zero, groupTags).ExtractMostSignificantBits();
                    if (emptyMask != 0)
                    {
                        var pos = groupOffset + BitOperations.TrailingZeroCount(emptyMask);
                        newSlots[pos] = i + 1;
                        newTags[pos] = tag;
                        newTouched[touchedCount++] = pos;
                        break;
                    }
                }
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
}
