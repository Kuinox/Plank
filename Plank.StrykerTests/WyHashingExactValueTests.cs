using Plank.Writing.Encoding;

namespace Plank.StrykerTests;

/// <summary>
/// Exact-value tests for WyHashing.Hash computed from the original implementation.
/// These kill arithmetic mutations (XOR constants, shift amounts, multipliers) that
/// the invariant-based tests in WyHashingTests.cs cannot catch.
/// </summary>
public class WyHashingExactValueTests
{
    // ──────────────── Empty and 1-3 bytes (lines 44-49) ────────────────

    [Fact] public void Hash_Empty()
        => Assert.Equal(-585827371, WyHashing.Hash([]));

    [Fact] public void Hash_OneByte_0x41()
        => Assert.Equal(494216001, WyHashing.Hash([0x41]));

    [Fact] public void Hash_TwoBytes_0x41_0x42()
        => Assert.Equal(2146815733, WyHashing.Hash([0x41, 0x42]));

    [Fact] public void Hash_ThreeBytes_0x41_0x42_0x43()
        => Assert.Equal(636034920, WyHashing.Hash([0x41, 0x42, 0x43]));

    // ──────────────── 4-8 bytes (lines 52-57) ────────────────

    [Fact] public void Hash_FourBytes()
        => Assert.Equal(-1666197956, WyHashing.Hash([0x41, 0x42, 0x43, 0x44]));

    [Fact] public void Hash_SevenBytes()
        => Assert.Equal(-1964023667, WyHashing.Hash([0x41, 0x42, 0x43, 0x44, 0x45, 0x46, 0x47]));

    [Fact] public void Hash_EightBytes()
        => Assert.Equal(-1802393459, WyHashing.Hash([0x41, 0x42, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48]));

    // ──────────────── 9-16 bytes (lines 59-63) ────────────────

    [Fact] public void Hash_NineBytes()
        => Assert.Equal(1536024446, WyHashing.Hash([0x41, 0x42, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48, 0x49]));

    [Fact] public void Hash_FifteenBytes()
        => Assert.Equal(69482798, WyHashing.Hash([0x41, 0x42, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48, 0x49, 0x4a, 0x4b, 0x4c, 0x4d, 0x4e, 0x4f]));

    [Fact] public void Hash_SixteenBytes()
        => Assert.Equal(-857652529, WyHashing.Hash([0x41, 0x42, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48, 0x49, 0x4a, 0x4b, 0x4c, 0x4d, 0x4e, 0x4f, 0x50]));

    // ──────────────── >16 bytes — loop path (lines 66-90) ────────────────

    [Fact] public void Hash_SeventeenBytes()
        => Assert.Equal(-797036493, WyHashing.Hash([0x41, 0x42, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48, 0x49, 0x4a, 0x4b, 0x4c, 0x4d, 0x4e, 0x4f, 0x50, 0x51]));

    [Fact] public void Hash_ThirtyTwoZeroBytes_TwoLoopIterations_NoTail()
        => Assert.Equal(877071260, WyHashing.Hash(new byte[32]));

    [Fact] public void Hash_ThirtyThreeZeroBytes_Tail1()
        => Assert.Equal(694608901, WyHashing.Hash(new byte[33]));

    [Fact] public void Hash_FortyOneZeroBytes_Tail9()
        => Assert.Equal(496126256, WyHashing.Hash(new byte[41]));
}
