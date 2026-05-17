using System.Collections.Generic;
using Plank.Writing.Encoding;

namespace Plank.StrykerTests;

public class ReusableDictionaryStateTests
{
    static ReusableDictionaryState<int> MakeInt(int capacity = 16, bool useMap = true)
    {
        var d = new ReusableDictionaryState<int>();
        d.Reset(capacity, useMap, EqualityComparer<int>.Default);
        return d;
    }

    static ReusableDictionaryState<string> MakeString(int capacity = 16, bool useMap = true)
    {
        var d = new ReusableDictionaryState<string>();
        d.Reset(capacity, useMap, EqualityComparer<string>.Default);
        return d;
    }

    // ──────────────── Basic insertion ────────────────

    [Fact]
    public void GetOrAddIndex_FirstItem_Returns0()
    {
        var d = MakeInt();
        Assert.Equal(0, d.GetOrAddIndex(42));
        Assert.Equal(1, d.Count);
    }

    [Fact]
    public void GetOrAddIndex_SecondDistinctItem_Returns1()
    {
        var d = MakeInt();
        d.GetOrAddIndex(10);
        Assert.Equal(1, d.GetOrAddIndex(20));
        Assert.Equal(2, d.Count);
    }

    [Fact]
    public void GetOrAddIndex_DuplicateItem_ReturnsSameIndex()
    {
        var d = MakeInt();
        var first = d.GetOrAddIndex(99);
        var second = d.GetOrAddIndex(99);
        Assert.Equal(first, second);
        Assert.Equal(1, d.Count);
    }

    [Fact]
    public void GetOrAddIndex_ManyItems_IndicesAreSequential()
    {
        var d = MakeInt(100);
        for (var i = 0; i < 50; i++)
            Assert.Equal(i, d.GetOrAddIndex(i * 17));
        Assert.Equal(50, d.Count);
    }

    [Fact]
    public void GetOrAddIndex_AfterReset_StartsFromZero()
    {
        var d = MakeInt();
        d.GetOrAddIndex(1);
        d.GetOrAddIndex(2);
        d.Reset(16, true, EqualityComparer<int>.Default);
        Assert.Equal(0, d.GetOrAddIndex(99));
        Assert.Equal(1, d.Count);
    }

    // ──────────────── String dictionary ────────────────

    [Fact]
    public void GetOrAddIndex_Strings_DuplicatesReturnSameIndex()
    {
        var d = MakeString();
        var i0 = d.GetOrAddIndex("hello");
        var i1 = d.GetOrAddIndex("world");
        var i0b = d.GetOrAddIndex("hello");
        Assert.Equal(0, i0);
        Assert.Equal(1, i1);
        Assert.Equal(0, i0b);
        Assert.Equal(2, d.Count);
    }

    [Fact]
    public void GetOrAddIndex_Strings_ManyUniqueItems()
    {
        var d = MakeString(200);
        var keys = Enumerable.Range(0, 100).Select(i => $"key-{i}").ToArray();
        for (var i = 0; i < keys.Length; i++)
            Assert.Equal(i, d.GetOrAddIndex(keys[i]));

        // re-lookup should return same indices
        for (var i = 0; i < keys.Length; i++)
            Assert.Equal(i, d.GetOrAddIndex(keys[i]));

        Assert.Equal(100, d.Count);
    }

    // ──────────────── AsSpan ────────────────

    [Fact]
    public void AsSpan_ReturnsInsertionOrderValues()
    {
        var d = MakeInt();
        d.GetOrAddIndex(10);
        d.GetOrAddIndex(20);
        d.GetOrAddIndex(30);
        d.GetOrAddIndex(20); // duplicate
        var span = d.AsSpan();
        Assert.Equal(3, span.Length);
        Assert.Equal(10, span[0]);
        Assert.Equal(20, span[1]);
        Assert.Equal(30, span[2]);
    }

    // ──────────────── AddFirst / AddSortedUnique ────────────────

    [Fact]
    public void AddFirst_SetsFirstValueAndReturnsZero()
    {
        var d = new ReusableDictionaryState<int>();
        d.Reset(16, false, EqualityComparer<int>.Default);
        Assert.Equal(0, d.AddFirst(100));
        Assert.Equal(1, d.Count);
    }

    [Fact]
    public void AddSortedUnique_ReturnsSequentialIndices()
    {
        var d = new ReusableDictionaryState<int>();
        d.Reset(16, false, EqualityComparer<int>.Default);
        d.AddFirst(1);
        Assert.Equal(1, d.AddSortedUnique(2));
        Assert.Equal(2, d.AddSortedUnique(3));
        Assert.Equal(3, d.Count);
    }

    // ──────────────── EnableMap ────────────────

    [Fact]
    public void EnableMap_AfterAddingSortedUniques_AllLookupsByIndex()
    {
        var d = new ReusableDictionaryState<int>();
        d.Reset(16, false, EqualityComparer<int>.Default);
        d.AddFirst(10);
        d.AddSortedUnique(20);
        d.AddSortedUnique(30);
        Assert.False(d.IsMapEnabled);
        d.EnableMap();
        Assert.True(d.IsMapEnabled);
        Assert.Equal(0, d.GetOrAddIndex(10));
        Assert.Equal(1, d.GetOrAddIndex(20));
        Assert.Equal(2, d.GetOrAddIndex(30));
        Assert.Equal(3, d.Count); // no new items added
    }

    [Fact]
    public void IsMapEnabled_ReflectsResetArgument()
    {
        var d = new ReusableDictionaryState<int>();
        d.Reset(16, false, EqualityComparer<int>.Default);
        Assert.False(d.IsMapEnabled);
        d.Reset(16, true, EqualityComparer<int>.Default);
        Assert.True(d.IsMapEnabled);
    }

    // ──────────────── Resize under load ────────────────

    [Fact]
    public void GetOrAddIndex_ForcesResize_StillCorrect()
    {
        var d = MakeInt(4);  // small initial size → will resize
        var expected = new Dictionary<int, int>();
        for (var i = 0; i < 200; i++)
        {
            var key = i * 31 + 7;
            var idx = d.GetOrAddIndex(key);
            if (!expected.ContainsKey(key))
                expected[key] = expected.Count;
            Assert.Equal(expected[key], idx);
        }
        Assert.Equal(200, d.Count);
    }

    // ──────────────── MapNotEnabled throws ────────────────

    [Fact]
    public void GetOrAddIndex_MapDisabled_Throws()
    {
        var d = new ReusableDictionaryState<int>();
        d.Reset(16, false, EqualityComparer<int>.Default);
        Assert.Throws<InvalidOperationException>(() => d.GetOrAddIndex(1));
    }

    // ──────────────── Multiple resets ────────────────

    [Fact]
    public void Reset_ClearsAllState()
    {
        var d = MakeInt();
        for (var i = 0; i < 50; i++)
            d.GetOrAddIndex(i);
        Assert.Equal(50, d.Count);

        d.Reset(16, true, EqualityComparer<int>.Default);
        Assert.Equal(0, d.Count);
        // First item after reset should be index 0 again
        Assert.Equal(0, d.GetOrAddIndex(999));
    }
}
