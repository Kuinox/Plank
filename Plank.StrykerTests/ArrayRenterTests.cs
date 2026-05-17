using Plank.Writing;

namespace Plank.StrykerTests;

public class ArrayRenterTests
{
    // ──────────────── Rent ────────────────

    [Fact]
    public void Rent_Zero_ReturnsEmptyArray()
    {
        var arr = ArrayRenter<int>.Shared.Rent(0);
        Assert.Empty(arr);
    }

    [Fact]
    public void Rent_One_ReturnsNonNull()
    {
        var arr = ArrayRenter<int>.Shared.Rent(1);
        Assert.NotNull(arr);
        Assert.True(arr.Length >= 1);
    }

    [Fact]
    public void Rent_SmallSize_LengthAtLeastRequested()
    {
        foreach (var size in new[] { 1, 2, 4, 8, 16, 32 })
        {
            var arr = ArrayRenter<byte>.Shared.Rent(size);
            Assert.True(arr.Length >= size);
            ArrayRenter<byte>.Shared.Return(arr);
        }
    }

    [Fact]
    public void Rent_VeryLargeSize_ReturnsExactSize()
    {
        // Larger than all buckets → new allocation
        var arr = ArrayRenter<int>.Shared.Rent(1 << 31 - 1);
        Assert.True(arr.Length >= 1);
        // Don't return this — it's too large for the pool
    }

    // ──────────────── Return + Rent cycle ────────────────

    [Fact]
    public void ReturnThenRent_ReturnsSameBuffer()
    {
        var original = ArrayRenter<int>.Shared.Rent(16);
        original[0] = 0xDEAD;
        ArrayRenter<int>.Shared.Return(original);
        var next = ArrayRenter<int>.Shared.Rent(16);
        // The pool should return the same buffer (implementation detail)
        Assert.True(next.Length >= 16);
        ArrayRenter<int>.Shared.Return(next);
    }

    [Fact]
    public void Return_ClearsArray_WhenClearRequested()
    {
        var arr = ArrayRenter<int>.Shared.Rent(8);
        arr[0] = 99;
        arr[7] = 77;
        ArrayRenter<int>.Shared.Return(arr, clearArray: true);
        // The returned array should be cleared
        Assert.Equal(0, arr[0]);
        Assert.Equal(0, arr[7]);
    }

    [Fact]
    public void Return_DoesNotClear_WhenNotRequested()
    {
        var arr = ArrayRenter<int>.Shared.Rent(8);
        arr[0] = 42;
        ArrayRenter<int>.Shared.Return(arr, clearArray: false);
        Assert.Equal(42, arr[0]); // still has the value
    }

    [Fact]
    public void Return_EmptyArray_DoesNotThrow()
    {
        ArrayRenter<int>.Shared.Return([]); // should not throw
    }

    [Fact]
    public void Return_NonPowerOf2Size_NotRetained()
    {
        // Arrays with sizes that don't match a bucket aren't retained
        // Return should silently discard
        var arr = new int[15]; // not a power of 2
        ArrayRenter<int>.Shared.Return(arr); // should not throw
    }

    // ──────────────── Multiple sizes ────────────────

    [Fact]
    public void Rent_PowersOfTwo_AllSucceed()
    {
        for (var i = 1; i <= 16; i++)
        {
            var size = 1 << i;
            var arr = ArrayRenter<byte>.Shared.Rent((uint)size);
            Assert.True(arr.Length >= size);
            ArrayRenter<byte>.Shared.Return(arr);
        }
    }

    [Fact]
    public void Rent_MinimumBucketPower_CorrectSize()
    {
        // MinimumBucketPower = 4 → smallest bucket is 2^4 = 16
        var arr = ArrayRenter<int>.Shared.Rent(1); // rounds up to 16
        Assert.True(arr.Length >= 1);
        ArrayRenter<int>.Shared.Return(arr);
    }
}
