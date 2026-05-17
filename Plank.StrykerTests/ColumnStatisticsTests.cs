using System.Collections.Immutable;
using Plank.Schema;
using Plank.Writing;

namespace Plank.StrykerTests;

public class ColumnStatisticsTests
{
    static Column Int32Column(string name = "v") => new(name, ParquetPhysicalType.Int32);
    static Column Int64Column(string name = "v") => new(name, ParquetPhysicalType.Int64);
    static Column FloatColumn(string name = "v") => new(name, ParquetPhysicalType.Float);
    static Column DoubleColumn(string name = "v") => new(name, ParquetPhysicalType.Double);
    static Column BoolColumn(string name = "v") => new(name, ParquetPhysicalType.Boolean);
    static Column ByteArrayColumn(string name = "v") => new(name, ParquetPhysicalType.ByteArray);
    static Column StringColumn(string name = "v") => new(name, ParquetPhysicalType.ByteArray, null, new LogicalType.String());

    // ──────────────── Int32 ────────────────

    [Test]
    public void Int32_SingleValue_MinEqualsMax()
    {
        var s = ColumnStatistics.Create(Int32Column(), new int[] { 42 }, 0);
        ClassicAssert.AreEqual(ColumnStatistics.ColumnStatisticsValueKind.Int32, s.ValueKind);
        ClassicAssert.AreEqual(42L, s.MinBits);
        ClassicAssert.AreEqual(42L, s.MaxBits);
        ClassicAssert.AreEqual(0L, s.NullCount);
    }

    [Test]
    public void Int32_Empty_ReturnsEmpty()
    {
        var s = ColumnStatistics.Create(Int32Column(), ReadOnlySpan<int>.Empty, 3);
        ClassicAssert.AreEqual(ColumnStatistics.ColumnStatisticsValueKind.None, s.ValueKind);
        ClassicAssert.AreEqual(3L, s.NullCount);
    }

    [Test]
    public void Int32_MinAndMax_CorrectlyComputed()
    {
        var s = ColumnStatistics.Create(Int32Column(), new int[] { 5, -3, 10, 0, -7, 4 }, 0);
        ClassicAssert.AreEqual(ColumnStatistics.ColumnStatisticsValueKind.Int32, s.ValueKind);
        ClassicAssert.AreEqual(-7L, s.MinBits);
        ClassicAssert.AreEqual(10L, s.MaxBits);
    }

    [Test]
    public void Int32_NullCount_Propagated()
    {
        var s = ColumnStatistics.Create(Int32Column(), new int[] { 1, 2 }, nullCount: 5);
        ClassicAssert.AreEqual(5L, s.NullCount);
    }

    [Test]
    public void Int32_FirstElementIsMin()
    {
        var s = ColumnStatistics.Create(Int32Column(), new int[] { -100, 50, 25 }, 0);
        ClassicAssert.AreEqual(-100L, s.MinBits);
        ClassicAssert.AreEqual(50L, s.MaxBits);
    }

    [Test]
    public void Int32_FirstElementIsMax()
    {
        var s = ColumnStatistics.Create(Int32Column(), new int[] { 100, -50, 25 }, 0);
        ClassicAssert.AreEqual(-50L, s.MinBits);
        ClassicAssert.AreEqual(100L, s.MaxBits);
    }

    [Test]
    public void Int32_AllSameValue_MinEqualsMax()
    {
        var s = ColumnStatistics.Create(Int32Column(), new int[] { 7, 7, 7, 7 }, 0);
        ClassicAssert.AreEqual(7L, s.MinBits);
        ClassicAssert.AreEqual(7L, s.MaxBits);
        ClassicAssert.AreEqual(1L, s.DistinctCount);
    }

    [Test]
    public void Int32_TwoDistinctValues_DistinctCountIsMinusOne()
    {
        var s = ColumnStatistics.Create(Int32Column(), new int[] { 1, 2 }, 0);
        ClassicAssert.AreEqual(-1L, s.DistinctCount);
    }

    [Test]
    public void Int32_MinValueAndMaxValue()
    {
        var s = ColumnStatistics.Create(Int32Column(), new int[] { int.MinValue, int.MaxValue }, 0);
        ClassicAssert.AreEqual((long)int.MinValue, s.MinBits);
        ClassicAssert.AreEqual((long)int.MaxValue, s.MaxBits);
    }

    // ──────────────── Vectorized Int32 (forces SIMD path) ────────────────

    [Test]
    public void Int32_Vectorized_CorrectMinMax()
    {
        // 20 elements forces SIMD path (Vector<int>.Count is typically 4 or 8)
        var values = Enumerable.Range(0, 20).Select(i => i - 10).ToArray();
        var s = ColumnStatistics.Create(Int32Column(), values, 0);
        ClassicAssert.AreEqual(-10L, s.MinBits);
        ClassicAssert.AreEqual(9L, s.MaxBits);
    }

    [Test]
    public void Int32_VectorizedWithTail_CorrectMinMax()
    {
        // 17 elements: 2 full SIMD vectors (8 each) + 1 tail when width=8
        var values = new int[] { 100, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, -99 };
        var s = ColumnStatistics.Create(Int32Column(), values, 0);
        ClassicAssert.AreEqual(-99L, s.MinBits);
        ClassicAssert.AreEqual(100L, s.MaxBits);
    }

    // ──────────────── Int64 ────────────────

    [Test]
    public void Int64_SingleValue_MinEqualsMax()
    {
        var s = ColumnStatistics.Create(Int64Column(), new long[] { 42L }, 0);
        ClassicAssert.AreEqual(ColumnStatistics.ColumnStatisticsValueKind.Int64, s.ValueKind);
        ClassicAssert.AreEqual(42L, s.MinBits);
        ClassicAssert.AreEqual(42L, s.MaxBits);
    }

    [Test]
    public void Int64_MinAndMax_CorrectlyComputed()
    {
        var s = ColumnStatistics.Create(Int64Column(), new long[] { 5L, -3L, 10L, -100L, 7L }, 0);
        ClassicAssert.AreEqual(-100L, s.MinBits);
        ClassicAssert.AreEqual(10L, s.MaxBits);
    }

    [Test]
    public void Int64_Empty_ReturnsEmpty()
    {
        var s = ColumnStatistics.Create(Int64Column(), ReadOnlySpan<long>.Empty, 0);
        ClassicAssert.AreEqual(ColumnStatistics.ColumnStatisticsValueKind.None, s.ValueKind);
    }

    // ──────────────── Float ────────────────

    [Test]
    public void Float_NaNIgnored()
    {
        var s = ColumnStatistics.Create(FloatColumn(), new float[] { float.NaN, 3.5f, 1.0f, float.NaN, 9.0f }, 0);
        ClassicAssert.AreEqual(ColumnStatistics.ColumnStatisticsValueKind.Float, s.ValueKind);
        ClassicAssert.AreEqual(1.0f, BitConverter.Int32BitsToSingle((int)s.MinBits));
        ClassicAssert.AreEqual(9.0f, BitConverter.Int32BitsToSingle((int)s.MaxBits));
    }

    [Test]
    public void Float_AllNaN_ReturnsNone()
    {
        var s = ColumnStatistics.Create(FloatColumn(), new float[] { float.NaN, float.NaN }, 0);
        ClassicAssert.AreEqual(ColumnStatistics.ColumnStatisticsValueKind.None, s.ValueKind);
    }

    [Test]
    public void Float_SingleNonNaN_CorrectMinMax()
    {
        var s = ColumnStatistics.Create(FloatColumn(), new float[] { float.NaN, 5.0f }, 0);
        ClassicAssert.AreEqual(5.0f, BitConverter.Int32BitsToSingle((int)s.MinBits));
        ClassicAssert.AreEqual(5.0f, BitConverter.Int32BitsToSingle((int)s.MaxBits));
    }

    [Test]
    public void Float_LeadingNaN_CorrectMinMax()
    {
        var s = ColumnStatistics.Create(FloatColumn(), new float[] { float.NaN, 5.0f, 2.0f, 8.0f }, 0);
        ClassicAssert.AreEqual(2.0f, BitConverter.Int32BitsToSingle((int)s.MinBits));
        ClassicAssert.AreEqual(8.0f, BitConverter.Int32BitsToSingle((int)s.MaxBits));
    }

    [Test]
    public void Float_Empty_ReturnsEmpty()
    {
        var s = ColumnStatistics.Create(FloatColumn(), ReadOnlySpan<float>.Empty, 0);
        ClassicAssert.AreEqual(ColumnStatistics.ColumnStatisticsValueKind.None, s.ValueKind);
    }

    // ──────────────── Double ────────────────

    [Test]
    public void Double_NaNIgnored()
    {
        var s = ColumnStatistics.Create(DoubleColumn(), new double[] { double.NaN, 3.5, 1.0, double.NaN, 9.0 }, 0);
        ClassicAssert.AreEqual(ColumnStatistics.ColumnStatisticsValueKind.Double, s.ValueKind);
        ClassicAssert.AreEqual(1.0, BitConverter.Int64BitsToDouble(s.MinBits));
        ClassicAssert.AreEqual(9.0, BitConverter.Int64BitsToDouble(s.MaxBits));
    }

    [Test]
    public void Double_AllNaN_ReturnsNone()
    {
        var s = ColumnStatistics.Create(DoubleColumn(), new double[] { double.NaN, double.NaN }, 0);
        ClassicAssert.AreEqual(ColumnStatistics.ColumnStatisticsValueKind.None, s.ValueKind);
    }

    [Test]
    public void Double_Empty_ReturnsEmpty()
    {
        var s = ColumnStatistics.Create(DoubleColumn(), ReadOnlySpan<double>.Empty, 0);
        ClassicAssert.AreEqual(ColumnStatistics.ColumnStatisticsValueKind.None, s.ValueKind);
    }

    // ──────────────── Boolean ────────────────

    [Test]
    public void Boolean_AllTrue_MinAndMaxAreTrue()
    {
        var s = ColumnStatistics.Create(BoolColumn(), new bool[] { true, true, true }, 0);
        ClassicAssert.AreEqual(ColumnStatistics.ColumnStatisticsValueKind.Boolean, s.ValueKind);
        ClassicAssert.AreEqual(1L, s.MinBits);
        ClassicAssert.AreEqual(1L, s.MaxBits);
    }

    [Test]
    public void Boolean_AllFalse_MinAndMaxAreFalse()
    {
        var s = ColumnStatistics.Create(BoolColumn(), new bool[] { false, false }, 0);
        ClassicAssert.AreEqual(0L, s.MinBits);
        ClassicAssert.AreEqual(0L, s.MaxBits);
    }

    [Test]
    public void Boolean_Mixed_MinFalseMaxTrue()
    {
        var s = ColumnStatistics.Create(BoolColumn(), new bool[] { true, false, true }, 0);
        ClassicAssert.AreEqual(0L, s.MinBits);
        ClassicAssert.AreEqual(1L, s.MaxBits);
    }

    [Test]
    public void Boolean_FalseFirst_CorrectMinMax()
    {
        var s = ColumnStatistics.Create(BoolColumn(), new bool[] { false, true }, 0);
        ClassicAssert.AreEqual(0L, s.MinBits);
        ClassicAssert.AreEqual(1L, s.MaxBits);
    }

    // ──────────────── ByteArray ────────────────

    [Test]
    public void ByteArray_SingleElement_MinEqualsMax()
    {
        var data = new byte[] { 1, 2, 3 };
        var s = ColumnStatistics.Create(ByteArrayColumn(), new byte[][] { data }, 0);
        ClassicAssert.AreEqual(ColumnStatistics.ColumnStatisticsValueKind.Binary, s.ValueKind);
        ClassicAssert.IsTrue(s.MinValue?.SequenceEqual(data));
        ClassicAssert.IsTrue(s.MaxValue?.SequenceEqual(data));
    }

    [Test]
    public void ByteArray_LexicographicOrdering()
    {
        var values = new byte[][] {
            new byte[] { 2, 0 },
            new byte[] { 1, 0 },
            new byte[] { 3, 0 }
        };
        var s = ColumnStatistics.Create(ByteArrayColumn(), values, 0);
        ClassicAssert.AreEqual(new byte[] { 1, 0 }, s.MinValue?.Take(s.MinValueLength).ToArray());
        ClassicAssert.AreEqual(new byte[] { 3, 0 }, s.MaxValue?.Take(s.MaxValueLength).ToArray());
    }

    [Test]
    public void ByteArray_ShorterPrefixIsLess()
    {
        var shorter = new byte[] { 1 };
        var longer = new byte[] { 1, 0 };
        var s = ColumnStatistics.Create(ByteArrayColumn(), new byte[][] { longer, shorter }, 0);
        ClassicAssert.AreEqual(1, s.MinValueLength);
        ClassicAssert.AreEqual(shorter, s.MinValue?.Take(s.MinValueLength).ToArray());
    }

    // ──────────────── TryGetInt32MinMax ────────────────

    [Test]
    public void TryGetInt32MinMax_Empty_ReturnsFalse()
    {
        ClassicAssert.IsFalse(ColumnStatistics.TryGetInt32MinMax(ReadOnlySpan<int>.Empty, out _, out _));
    }

    [Test]
    public void TryGetInt32MinMax_SingleElement_ReturnsTrue()
    {
        ClassicAssert.IsTrue(ColumnStatistics.TryGetInt32MinMax(new int[] { 99 }, out var min, out var max));
        ClassicAssert.AreEqual(99, min);
        ClassicAssert.AreEqual(99, max);
    }

    [Test]
    public void TryGetInt32MinMax_MultipleElements()
    {
        ClassicAssert.IsTrue(ColumnStatistics.TryGetInt32MinMax(new int[] { 3, -1, 5, 2 }, out var min, out var max));
        ClassicAssert.AreEqual(-1, min);
        ClassicAssert.AreEqual(5, max);
    }

    // ──────────────── Nullable Int32 ────────────────

    [Test]
    public void NullableInt32_AllNulls_ReturnsEmpty()
    {
        var s = ColumnStatistics.CreateOptional<int>(Int32Column(), new int?[] { null, null });
        ClassicAssert.AreEqual(ColumnStatistics.ColumnStatisticsValueKind.None, s.ValueKind);
        ClassicAssert.AreEqual(2L, s.NullCount);
    }

    [Test]
    public void NullableInt32_MixedNulls_CorrectMinMax()
    {
        var s = ColumnStatistics.CreateOptional<int>(Int32Column(), new int?[] { null, 5, null, -3, null });
        ClassicAssert.AreEqual(ColumnStatistics.ColumnStatisticsValueKind.Int32, s.ValueKind);
        ClassicAssert.AreEqual(-3L, s.MinBits);
        ClassicAssert.AreEqual(5L, s.MaxBits);
        ClassicAssert.AreEqual(3L, s.NullCount);
    }

    [Test]
    public void NullableInt32_NoNulls_CorrectMinMax()
    {
        var s = ColumnStatistics.CreateOptional<int>(Int32Column(), new int?[] { 10, 1, 5 });
        ClassicAssert.AreEqual(1L, s.MinBits);
        ClassicAssert.AreEqual(10L, s.MaxBits);
        ClassicAssert.AreEqual(0L, s.NullCount);
    }

    // ──────────────── Nullable Boolean ────────────────

    [Test]
    public void NullableBoolean_AllNulls_ReturnsEmpty()
    {
        var s = ColumnStatistics.CreateOptional<bool>(BoolColumn(), new bool?[] { null, null });
        ClassicAssert.AreEqual(ColumnStatistics.ColumnStatisticsValueKind.None, s.ValueKind);
        ClassicAssert.AreEqual(2L, s.NullCount);
    }

    [Test]
    public void NullableBoolean_MixedNulls_CorrectMinMax()
    {
        var s = ColumnStatistics.CreateOptional<bool>(BoolColumn(), new bool?[] { null, true, null, false });
        ClassicAssert.AreEqual(0L, s.MinBits);
        ClassicAssert.AreEqual(1L, s.MaxBits);
        ClassicAssert.AreEqual(2L, s.NullCount);
    }

    // ──────────────── Byte statistics ────────────────

    [Test]
    public void Byte_MinAndMax_CorrectlyComputed()
    {
        var s = ColumnStatistics.CreateByte(new byte[] { 10, 5, 200, 3, 150 }, 0);
        ClassicAssert.AreEqual(ColumnStatistics.ColumnStatisticsValueKind.Int32, s.ValueKind);
        ClassicAssert.AreEqual(3L, s.MinBits);
        ClassicAssert.AreEqual(200L, s.MaxBits);
    }

    [Test]
    public void Byte_Empty_ReturnsEmpty()
    {
        var s = ColumnStatistics.CreateByte(ReadOnlySpan<byte>.Empty, 0);
        ClassicAssert.AreEqual(ColumnStatistics.ColumnStatisticsValueKind.None, s.ValueKind);
    }

    [Test]
    public void Byte_SingleValue_MinEqualsMax()
    {
        var s = ColumnStatistics.CreateByte(new byte[] { 42 }, 0);
        ClassicAssert.AreEqual(42L, s.MinBits);
        ClassicAssert.AreEqual(42L, s.MaxBits);
    }
}
