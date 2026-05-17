using Plank.Writing.Encoding;

namespace Plank.Tests.Mutation;

/// <summary>
/// Exact-value tests for WyHashing.Hash computed from the original implementation.
/// These kill arithmetic mutations (XOR constants, shift amounts, multipliers) that
/// the invariant-based tests in WyHashingTests.cs cannot catch.
/// </summary>
public class WyHashingExactValueTests
{
    // ──────────────── Empty and 1-3 bytes (lines 44-49) ────────────────

    [Test] public void Hash_Empty()
        => ClassicAssert.AreEqual(-585827371, WyHashing.Hash([]));

    [Test] public void Hash_OneByte_0x41()
        => ClassicAssert.AreEqual(494216001, WyHashing.Hash([0x41]));

    [Test] public void Hash_TwoBytes_0x41_0x42()
        => ClassicAssert.AreEqual(2146815733, WyHashing.Hash([0x41, 0x42]));

    [Test] public void Hash_ThreeBytes_0x41_0x42_0x43()
        => ClassicAssert.AreEqual(636034920, WyHashing.Hash([0x41, 0x42, 0x43]));

    // ──────────────── 4-8 bytes (lines 52-57) ────────────────

    [Test] public void Hash_FourBytes()
        => ClassicAssert.AreEqual(-1666197956, WyHashing.Hash([0x41, 0x42, 0x43, 0x44]));

    [Test] public void Hash_SevenBytes()
        => ClassicAssert.AreEqual(-1964023667, WyHashing.Hash([0x41, 0x42, 0x43, 0x44, 0x45, 0x46, 0x47]));

    [Test] public void Hash_EightBytes()
        => ClassicAssert.AreEqual(-1802393459, WyHashing.Hash([0x41, 0x42, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48]));

    // ──────────────── 9-16 bytes (lines 59-63) ────────────────

    [Test] public void Hash_NineBytes()
        => ClassicAssert.AreEqual(1536024446, WyHashing.Hash([0x41, 0x42, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48, 0x49]));

    [Test] public void Hash_FifteenBytes()
        => ClassicAssert.AreEqual(69482798, WyHashing.Hash([0x41, 0x42, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48, 0x49, 0x4a, 0x4b, 0x4c, 0x4d, 0x4e, 0x4f]));

    [Test] public void Hash_SixteenBytes()
        => ClassicAssert.AreEqual(-857652529, WyHashing.Hash([0x41, 0x42, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48, 0x49, 0x4a, 0x4b, 0x4c, 0x4d, 0x4e, 0x4f, 0x50]));

    // ──────────────── >16 bytes — loop path (lines 66-90) ────────────────

    [Test] public void Hash_SeventeenBytes()
        => ClassicAssert.AreEqual(-797036493, WyHashing.Hash([0x41, 0x42, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48, 0x49, 0x4a, 0x4b, 0x4c, 0x4d, 0x4e, 0x4f, 0x50, 0x51]));

    [Test] public void Hash_ThirtyTwoZeroBytes_TwoLoopIterations_NoTail()
        => ClassicAssert.AreEqual(877071260, WyHashing.Hash(new byte[32]));

    [Test] public void Hash_ThirtyThreeZeroBytes_Tail1()
        => ClassicAssert.AreEqual(694608901, WyHashing.Hash(new byte[33]));

    [Test] public void Hash_FortyOneZeroBytes_Tail9()
        => ClassicAssert.AreEqual(496126256, WyHashing.Hash(new byte[41]));
}
