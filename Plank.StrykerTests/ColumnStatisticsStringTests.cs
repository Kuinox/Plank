using Plank.Schema;
using Plank.Writing;

namespace Plank.StrykerTests;

/// <summary>
/// Targets surviving mutants in ColumnStatistics.cs at lines 709-715, 851-857:
/// - CreateOptionalByteArray: min/max comparison and return (lines 709-715)
/// - CreateOptionalString: min/max comparison and return (lines 851-857)
/// - CompareUtf8Strings: comparison logic (lines 862-874)
/// </summary>
public class ColumnStatisticsStringTests
{
    static Column ByteArrayCol() => new("v", ParquetPhysicalType.ByteArray);
    static Column StringCol() => new("v", ParquetPhysicalType.ByteArray, null, new LogicalType.String());

    // ──────────────── CreateOptionalByteArray (lines 709-715) ────────────────

    [Test]
    public void OptionalByteArray_FirstMinThenMax_CorrectMinMax()
    {
        // Min comes after non-min, max comes after non-max → both branches trigger
        var values = new byte[][] {
            new byte[] { 5, 0 },  // first
            new byte[] { 1, 0 },  // new min (triggers line 709)
            new byte[] { 9, 0 }   // new max (triggers line 711)
        };
        var s = ColumnStatistics.CreateOptional<byte[]>(ByteArrayCol(), values.AsSpan());
        ClassicAssert.AreEqual(ColumnStatistics.ColumnStatisticsValueKind.Binary, s.ValueKind);
        ClassicAssert.AreEqual(new byte[] { 1, 0 }, s.MinValue?.Take(s.MinValueLength).ToArray());
        ClassicAssert.AreEqual(new byte[] { 9, 0 }, s.MaxValue?.Take(s.MaxValueLength).ToArray());
    }

    [Test]
    public void OptionalByteArray_AllNulls_ReturnsEmpty()
    {
        // byte[] is a reference type, so nulls are null references in the span
        var values = new byte[][] { null!, null!, null! };
        var s = ColumnStatistics.CreateOptional<byte[]>(ByteArrayCol(), values.AsSpan());
        // line 715: min is null → Empty
        ClassicAssert.AreEqual(ColumnStatistics.ColumnStatisticsValueKind.None, s.ValueKind);
        ClassicAssert.AreEqual(3L, s.NullCount);
    }

    [Test]
    public void OptionalByteArray_SingleNonNull_MinEqualsMax()
    {
        var values = new byte[][] { null!, new byte[] { 42 }, null! };
        var s = ColumnStatistics.CreateOptional<byte[]>(ByteArrayCol(), values.AsSpan());
        ClassicAssert.AreEqual(ColumnStatistics.ColumnStatisticsValueKind.Binary, s.ValueKind);
        ClassicAssert.AreEqual(1, s.MinValueLength);
        ClassicAssert.AreEqual(42, s.MinValue![0]);
        ClassicAssert.AreEqual(42, s.MaxValue![0]);
        ClassicAssert.AreEqual(2L, s.NullCount);
    }

    [Test]
    public void OptionalByteArray_LexicographicMin_CorrectOrder()
    {
        var values = new byte[][] {
            new byte[] { 2, 0 },  // first (non-null)
            null!,
            new byte[] { 1, 0 },  // becomes min (< 2,0)
            new byte[] { 3, 0 }   // becomes max (> 2,0)
        };
        var s = ColumnStatistics.CreateOptional<byte[]>(ByteArrayCol(), values.AsSpan());
        ClassicAssert.AreEqual(new byte[] { 1, 0 }, s.MinValue?.Take(s.MinValueLength).ToArray());
        ClassicAssert.AreEqual(new byte[] { 3, 0 }, s.MaxValue?.Take(s.MaxValueLength).ToArray());
        ClassicAssert.AreEqual(1L, s.NullCount);
    }

    [Test]
    public void OptionalByteArray_ShorterPrefixIsSmaller()
    {
        var values = new byte[][] {
            new byte[] { 1, 0 },  // first
            new byte[] { 1 }      // shorter → becomes min
        };
        var s = ColumnStatistics.CreateOptional<byte[]>(ByteArrayCol(), values.AsSpan());
        ClassicAssert.AreEqual(1, s.MinValueLength);
        ClassicAssert.AreEqual(new byte[] { 1 }, s.MinValue?.Take(s.MinValueLength).ToArray());
    }

    // ──────────────── CreateOptionalString (lines 851-857) ────────────────

    [Test]
    public void OptionalString_FirstMinThenMax_CorrectMinMax()
    {
        // Exercises both < 0 and > 0 branches
        var values = new string?[] { "beta", null, "alpha", "gamma" };
        var s = ColumnStatistics.CreateOptional<string>(StringCol(), values.AsSpan());
        // alpha (UTF-8 0x61) < beta (0x62) < gamma (0x67)
        var minBytes = s.MinValue?.Take(s.MinValueLength).ToArray();
        var maxBytes = s.MaxValue?.Take(s.MaxValueLength).ToArray();
        ClassicAssert.AreEqual(System.Text.Encoding.UTF8.GetBytes("alpha"), minBytes);
        ClassicAssert.AreEqual(System.Text.Encoding.UTF8.GetBytes("gamma"), maxBytes);
    }

    [Test]
    public void OptionalString_AllNulls_ReturnsEmpty()
    {
        var values = new string?[] { null, null };
        var s = ColumnStatistics.CreateOptional<string>(StringCol(), values.AsSpan());
        // line 857: min is null → Empty
        ClassicAssert.AreEqual(ColumnStatistics.ColumnStatisticsValueKind.None, s.ValueKind);
        ClassicAssert.AreEqual(2L, s.NullCount);
    }

    [Test]
    public void OptionalString_SingleNonNull_MinEqualsMax()
    {
        var values = new string?[] { null, "hello", null };
        var s = ColumnStatistics.CreateOptional<string>(StringCol(), values.AsSpan());
        var expected = System.Text.Encoding.UTF8.GetBytes("hello");
        ClassicAssert.AreEqual(expected, s.MinValue?.Take(s.MinValueLength).ToArray());
        ClassicAssert.AreEqual(expected, s.MaxValue?.Take(s.MaxValueLength).ToArray());
        ClassicAssert.AreEqual(1L, s.DistinctCount); // same min and max → distinct=1
        ClassicAssert.AreEqual(2L, s.NullCount);
    }

    [Test]
    public void OptionalString_Ascending_MinIsFirst()
    {
        var values = new string?[] { "a", "b", "c", "d" };
        var s = ColumnStatistics.CreateOptional<string>(StringCol(), values.AsSpan());
        ClassicAssert.AreEqual(new byte[] { 0x61 }, s.MinValue?.Take(s.MinValueLength).ToArray()); // "a"
        ClassicAssert.AreEqual(new byte[] { 0x64 }, s.MaxValue?.Take(s.MaxValueLength).ToArray()); // "d"
    }

    [Test]
    public void OptionalString_Descending_MinIsLast()
    {
        var values = new string?[] { "z", "m", "a" };
        var s = ColumnStatistics.CreateOptional<string>(StringCol(), values.AsSpan());
        ClassicAssert.AreEqual(new byte[] { 0x61 }, s.MinValue?.Take(s.MinValueLength).ToArray()); // "a"
        ClassicAssert.AreEqual(new byte[] { 0x7a }, s.MaxValue?.Take(s.MaxValueLength).ToArray()); // "z"
    }

    // ──────────────── CompareUtf8Strings with non-ASCII chars (line 867) ────────────────

    [Test]
    public void String_NonAsciiComparison_CorrectOrder()
    {
        // Non-ASCII chars trigger CompareUtf8StringsSlow (line 877)
        // "café" (4 chars, 5 bytes UTF-8) vs "cafe" (4 chars, 4 bytes)
        var values = new string[] { "café", "cafe" };
        var s = ColumnStatistics.Create(StringCol(), values, 0);
        // "cafe" < "café" in UTF-8 byte comparison (é = 0xC3 0xA9 > any ASCII char a-z)
        var minBytes = s.MinValue?.Take(s.MinValueLength).ToArray();
        var maxBytes = s.MaxValue?.Take(s.MaxValueLength).ToArray();
        ClassicAssert.AreEqual(System.Text.Encoding.UTF8.GetBytes("cafe"), minBytes);
        ClassicAssert.AreEqual(System.Text.Encoding.UTF8.GetBytes("café"), maxBytes);
    }

    [Test]
    public void String_DifferentLengths_ShorterLexicographicWins()
    {
        // "ab" vs "abc" — "ab" < "abc" (same prefix, shorter is smaller)
        var values = new string[] { "abc", "ab" };
        var s = ColumnStatistics.Create(StringCol(), values, 0);
        ClassicAssert.AreEqual(System.Text.Encoding.UTF8.GetBytes("ab"), s.MinValue?.Take(s.MinValueLength).ToArray());
        ClassicAssert.AreEqual(System.Text.Encoding.UTF8.GetBytes("abc"), s.MaxValue?.Take(s.MaxValueLength).ToArray());
    }

    // ──────────────── CreateString with reusable buffers (lines 808-827) ────────────────

    [Test]
    public void CreateStringWithBuffers_MinInMiddle_CorrectMinMax()
    {
        var s = ColumnStatistics.Create(StringCol(), new string[] { "beta", "alpha", "gamma" }, 0);
        ClassicAssert.AreEqual(System.Text.Encoding.UTF8.GetBytes("alpha"), s.MinValue?.Take(s.MinValueLength).ToArray());
        ClassicAssert.AreEqual(System.Text.Encoding.UTF8.GetBytes("gamma"), s.MaxValue?.Take(s.MaxValueLength).ToArray());
    }

    [Test]
    public void CreateByteArray_MinInMiddle_CorrectMinMax()
    {
        var values = new byte[][] {
            new byte[] { 10 },
            new byte[] { 5 },   // becomes min
            new byte[] { 20 }   // becomes max
        };
        var s = ColumnStatistics.Create(ByteArrayCol(), values, 0);
        ClassicAssert.AreEqual(new byte[] { 5 }, s.MinValue?.Take(s.MinValueLength).ToArray());
        ClassicAssert.AreEqual(new byte[] { 20 }, s.MaxValue?.Take(s.MaxValueLength).ToArray());
    }

    // ──────────────── CreateByteArray with reusable buffers ────────────────

    [Test]
    public void CreateByteArrayWithReusableBuffers_RoundTrip()
    {
        byte[]? minBuf = null, maxBuf = null;
        var values = new byte[][] { new byte[] { 5 }, new byte[] { 1 }, new byte[] { 9 } };
        var s = ColumnStatistics.CreateWithReusableBinaryBuffers(ByteArrayCol(), values, 0,
            ref minBuf, ref maxBuf);
        ClassicAssert.AreEqual(new byte[] { 1 }, s.MinValue?.Take(s.MinValueLength).ToArray());
        ClassicAssert.AreEqual(new byte[] { 9 }, s.MaxValue?.Take(s.MaxValueLength).ToArray());
        ClassicAssert.IsNotNull(minBuf);
        ClassicAssert.IsNotNull(maxBuf);
    }
}
