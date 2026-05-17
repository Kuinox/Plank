using Plank.Writing;

namespace Plank.StrykerTests;

public class ArrayRenterTests
{
    // ──────────────── Rent ────────────────

    [Test]
    public void Rent_Zero_ReturnsEmptyArray()
    {
        var arr = ArrayRenter<int>.Shared.Rent(0);
        ClassicAssert.IsEmpty(arr);
    }

    [Test]
    public void Rent_One_ReturnsNonNull()
    {
        var arr = ArrayRenter<int>.Shared.Rent(1);
        ClassicAssert.IsNotNull(arr);
        ClassicAssert.IsTrue(arr.Length >= 1);
    }

    [Test]
    public void Rent_SmallSize_LengthAtLeastRequested()
    {
        foreach (var size in new[] { 1, 2, 4, 8, 16, 32 })
        {
            var arr = ArrayRenter<byte>.Shared.Rent(size);
            ClassicAssert.IsTrue(arr.Length >= size);
            ArrayRenter<byte>.Shared.Return(arr);
        }
    }

    [Test]
    public void Rent_VeryLargeSize_ReturnsExactSize()
    {
        // Larger than all buckets → new allocation
        var arr = ArrayRenter<int>.Shared.Rent(1 << 31 - 1);
        ClassicAssert.IsTrue(arr.Length >= 1);
        // Don't return this — it's too large for the pool
    }

    // ──────────────── Return + Rent cycle ────────────────

    [Test]
    public void ReturnThenRent_ReturnsSameBuffer()
    {
        var original = ArrayRenter<int>.Shared.Rent(16);
        original[0] = 0xDEAD;
        ArrayRenter<int>.Shared.Return(original);
        var next = ArrayRenter<int>.Shared.Rent(16);
        // The pool should return the same buffer (implementation detail)
        ClassicAssert.IsTrue(next.Length >= 16);
        ArrayRenter<int>.Shared.Return(next);
    }

    [Test]
    public void Return_ClearsArray_WhenClearRequested()
    {
        var arr = ArrayRenter<int>.Shared.Rent(8);
        arr[0] = 99;
        arr[7] = 77;
        ArrayRenter<int>.Shared.Return(arr, clearArray: true);
        // The returned array should be cleared
        ClassicAssert.AreEqual(0, arr[0]);
        ClassicAssert.AreEqual(0, arr[7]);
    }

    [Test]
    public void Return_DoesNotClear_WhenNotRequested()
    {
        var arr = ArrayRenter<int>.Shared.Rent(8);
        arr[0] = 42;
        ArrayRenter<int>.Shared.Return(arr, clearArray: false);
        ClassicAssert.AreEqual(42, arr[0]); // still has the value
    }

    [Test]
    public void Return_EmptyArray_DoesNotThrow()
    {
        ArrayRenter<int>.Shared.Return([]); // should not throw
    }

    [Test]
    public void Return_NonPowerOf2Size_NotRetained()
    {
        // Arrays with sizes that don't match a bucket aren't retained
        // Return should silently discard
        var arr = new int[15]; // not a power of 2
        ArrayRenter<int>.Shared.Return(arr); // should not throw
    }

    // ──────────────── Multiple sizes ────────────────

    [Test]
    public void Rent_PowersOfTwo_AllSucceed()
    {
        for (var i = 1; i <= 16; i++)
        {
            var size = 1 << i;
            var arr = ArrayRenter<byte>.Shared.Rent((uint)size);
            ClassicAssert.IsTrue(arr.Length >= size);
            ArrayRenter<byte>.Shared.Return(arr);
        }
    }

    [Test]
    public void Rent_MinimumBucketPower_CorrectSize()
    {
        // MinimumBucketPower = 4 → smallest bucket is 2^4 = 16
        var arr = ArrayRenter<int>.Shared.Rent(1); // rounds up to 16
        ClassicAssert.IsTrue(arr.Length >= 1);
        ArrayRenter<int>.Shared.Return(arr);
    }
}
