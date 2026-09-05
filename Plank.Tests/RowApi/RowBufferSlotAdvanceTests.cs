using Plank.RowApi;

namespace Plank.Tests.RowApi;

internal sealed class RowBufferSlotAdvanceTests
{
    [Test]
    [Arguments(1, 1)]
    [Arguments(1, 8)]
    [Arguments(8, 1)]
    [Arguments(8, 3)]
    [Arguments(8, 8)]
    [Arguments(8, int.MaxValue)]
    public async Task FastAdvanceStopsBeforeEitherBoundary(int capacity, int rowsPerGroup)
    {
        var slot = new EmptySlot(capacity);
        var boundary = Math.Min(capacity, rowsPerGroup);
        while (slot.TryAdvanceBefore(rowsPerGroup))
        {
        }
        await Assert.That(slot.Count).IsEqualTo(boundary - 1);
        slot.Next(); // The cold path commits the boundary row.
        await Assert.That(slot.Count).IsEqualTo(boundary);
        if (boundary == capacity)
            await Assert.That(slot.TryAdvanceBefore(int.MaxValue)).IsFalse();
    }

    [Test]
    public async Task FastAdvanceHonorsChangedCutoffsAndDoesNotWrap()
    {
        var slot = new EmptySlot(int.MaxValue);
        await Assert.That(slot.TryAdvanceBefore(8)).IsTrue();
        await Assert.That(slot.TryAdvanceBefore(1)).IsFalse();
        await Assert.That(slot.TryAdvanceBefore(0)).IsFalse();
        await Assert.That(slot.TryAdvanceBefore(-1)).IsFalse();
        await Assert.That(slot.Count).IsEqualTo(1);

        // No column allocations are needed to exercise the integer boundary.
        typeof(RowBufferSlot).GetProperty("Index",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(slot, int.MaxValue);
        await Assert.That(slot.TryAdvanceBefore(int.MaxValue)).IsFalse();
        await Assert.That(slot.Count).IsEqualTo(int.MaxValue);
    }

    sealed class EmptySlot(int capacity) : RowBufferSlot([], capacity);
}
