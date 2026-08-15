using Plank.Writing.Encoding;

namespace Plank.Tests.Writer;

internal sealed class ReusableDictionaryStateTests
{
    [Test]
    public async Task GetOrAddIndexUsesOrdinalStringEquality()
    {
        var state = new ReusableDictionaryState<string>();
        state.Reset(16, useMap: true);

        var first = state.GetOrAddIndex(new string("same"));
        var second = state.GetOrAddIndex(new string("same"));

        await Assert.That(second).IsEqualTo(first);
        await Assert.That(state.Count).IsEqualTo(1);
    }

    [Test]
    public void ResetClearsStoredReferences()
    {
        var state = new ReusableDictionaryState<object>();
        var references = Populate(state, 512);

        state.Reset(512, useMap: true);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        for (var i = 0; i < references.Length; i++)
        {
            if (references[i].IsAlive)
                throw new InvalidOperationException($"Reference at index {i} is still alive after reset.");
        }
    }

    [Test]
    public void ManyResetCyclesDoNotExposeStaleEntries()
    {
        var state = new ReusableDictionaryState<int>();
        for (var reset = 0; reset < 300; reset++)
        {
            state.Reset(4, useMap: true);
            for (var i = 0; i < 4; i++)
            {
                var value = reset * 4 + i;
                if (state.GetOrAddIndex(value) != i || state.GetOrAddIndex(value) != i)
                    throw new InvalidOperationException($"Reset {reset} returned the wrong index for {value}.");
            }

            if (state.Count != 4)
                throw new InvalidOperationException($"Reset {reset} retained stale entries.");
        }
    }

    [Test]
    public void ExactThresholdGrowthPreservesIndexes()
    {
        var state = new ReusableDictionaryState<int>();
        state.Reset(0, useMap: true);

        for (var i = 0; i < 4; i++)
            if (state.GetOrAddIndex(i) != i)
                throw new InvalidOperationException($"Initial index {i} was not preserved.");
        for (var i = 0; i < 4; i++)
            if (state.GetOrAddIndex(i) != i)
                throw new InvalidOperationException($"Threshold lookup {i} caused an incorrect resize.");

        if (state.GetOrAddIndex(4) != 4)
            throw new InvalidOperationException("The first index after growth was not preserved.");
        for (var i = 0; i < 5; i++)
            if (state.GetOrAddIndex(i) != i)
                throw new InvalidOperationException($"Index {i} was not preserved after growth.");
    }

    [Test]
    public void ExistingLookupAtThresholdDoesNotGrowTheTable()
    {
        var state = new ReusableDictionaryState<int>();
        state.Reset(0, useMap: true);
        for (var i = 0; i < 4; i++)
            state.GetOrAddIndex(i);

        var before = GC.GetAllocatedBytesForCurrentThread();
        var index = state.GetOrAddIndex(2);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        if (index != 2)
            throw new InvalidOperationException($"Expected index 2, got {index}.");
        if (allocated != 0)
            throw new InvalidOperationException($"Existing lookup at the growth threshold allocated {allocated} bytes.");
    }

    [Test]
    public void ZeroCapacityCanGrowAcrossMultipleTableSizes()
    {
        var state = new ReusableDictionaryState<int>();
        state.Reset(0, useMap: true);

        for (var i = 0; i < 1_024; i++)
            if (state.GetOrAddIndex(i) != i)
                throw new InvalidOperationException($"Growing zero-capacity state returned the wrong index for {i}.");
        for (var i = 0; i < 1_024; i++)
            if (state.GetOrAddIndex(i) != i)
                throw new InvalidOperationException($"Grown zero-capacity state lost index {i}.");
    }

    [Test]
    public void EnableMapAfterSortedPopulationPreservesIndexes()
    {
        var state = new ReusableDictionaryState<int>();
        state.Reset(2, useMap: false);
        state.AddFirst(10);
        for (var i = 1; i < 128; i++)
            state.AddSortedUnique(10 + i);

        state.EnableMap();

        for (var i = 0; i < 128; i++)
            if (state.GetOrAddIndex(10 + i) != i)
                throw new InvalidOperationException($"Map enablement lost sorted index {i}.");
    }

    [Test]
    public void CollisionHeavyKeysRemainDistinctAcrossResets()
    {
        var state = new ReusableDictionaryState<CollidingKey>();
        PopulateCollisions(state);
        PopulateCollisions(state);
    }

    static WeakReference[] Populate(ReusableDictionaryState<object> state, int count)
    {
        state.Reset(count, useMap: true);
        var result = new WeakReference[count];
        for (var i = 0; i < count; i++)
        {
            var value = new object();
            result[i] = new WeakReference(value);
            state.GetOrAddIndex(value);
        }

        return result;
    }

    static void PopulateCollisions(ReusableDictionaryState<CollidingKey> state)
    {
        state.Reset(256, useMap: true);
        for (var i = 0; i < 256; i++)
            if (state.GetOrAddIndex(new CollidingKey(i)) != i)
                throw new InvalidOperationException($"Colliding key {i} received the wrong insertion index.");
        for (var i = 255; i >= 0; i--)
            if (state.GetOrAddIndex(new CollidingKey(i)) != i)
                throw new InvalidOperationException($"Colliding key {i} was not found on its probe chain.");
        if (state.Count != 256)
            throw new InvalidOperationException("Collision-heavy lookup inserted duplicates.");
    }

    sealed class CollidingKey(int value) : IEquatable<CollidingKey>
    {
        public int Value { get; } = value;

        public bool Equals(CollidingKey? other) => other is not null && other.Value == Value;
        public override bool Equals(object? obj) => obj is CollidingKey other && Equals(other);
        public override int GetHashCode() => 1;
    }
}
