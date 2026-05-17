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

    [Fact]
    public void Int32_SingleValue_MinEqualsMax()
    {
        var s = ColumnStatistics.Create(Int32Column(), new int[] { 42 }, 0);
        Assert.Equal(ColumnStatistics.ColumnStatisticsValueKind.Int32, s.ValueKind);
        Assert.Equal(42L, s.MinBits);
        Assert.Equal(42L, s.MaxBits);
        Assert.Equal(0L, s.NullCount);
    }

    [Fact]
    public void Int32_Empty_ReturnsEmpty()
    {
        var s = ColumnStatistics.Create(Int32Column(), ReadOnlySpan<int>.Empty, 3);
        Assert.Equal(ColumnStatistics.ColumnStatisticsValueKind.None, s.ValueKind);
        Assert.Equal(3L, s.NullCount);
    }

    [Fact]
    public void Int32_MinAndMax_CorrectlyComputed()
    {
        var s = ColumnStatistics.Create(Int32Column(), new int[] { 5, -3, 10, 0, -7, 4 }, 0);
        Assert.Equal(ColumnStatistics.ColumnStatisticsValueKind.Int32, s.ValueKind);
        Assert.Equal(-7L, s.MinBits);
        Assert.Equal(10L, s.MaxBits);
    }

    [Fact]
    public void Int32_NullCount_Propagated()
    {
        var s = ColumnStatistics.Create(Int32Column(), new int[] { 1, 2 }, nullCount: 5);
        Assert.Equal(5L, s.NullCount);
    }

    [Fact]
    public void Int32_FirstElementIsMin()
    {
        var s = ColumnStatistics.Create(Int32Column(), new int[] { -100, 50, 25 }, 0);
        Assert.Equal(-100L, s.MinBits);
        Assert.Equal(50L, s.MaxBits);
    }

    [Fact]
    public void Int32_FirstElementIsMax()
    {
        var s = ColumnStatistics.Create(Int32Column(), new int[] { 100, -50, 25 }, 0);
        Assert.Equal(-50L, s.MinBits);
        Assert.Equal(100L, s.MaxBits);
    }

    [Fact]
    public void Int32_AllSameValue_MinEqualsMax()
    {
        var s = ColumnStatistics.Create(Int32Column(), new int[] { 7, 7, 7, 7 }, 0);
        Assert.Equal(7L, s.MinBits);
        Assert.Equal(7L, s.MaxBits);
        Assert.Equal(1L, s.DistinctCount);
    }

    [Fact]
    public void Int32_TwoDistinctValues_DistinctCountIsMinusOne()
    {
        var s = ColumnStatistics.Create(Int32Column(), new int[] { 1, 2 }, 0);
        Assert.Equal(-1L, s.DistinctCount);
    }

    [Fact]
    public void Int32_MinValueAndMaxValue()
    {
        var s = ColumnStatistics.Create(Int32Column(), new int[] { int.MinValue, int.MaxValue }, 0);
        Assert.Equal((long)int.MinValue, s.MinBits);
        Assert.Equal((long)int.MaxValue, s.MaxBits);
    }

    // ──────────────── Vectorized Int32 (forces SIMD path) ────────────────

    [Fact]
    public void Int32_Vectorized_CorrectMinMax()
    {
        // 20 elements forces SIMD path (Vector<int>.Count is typically 4 or 8)
        var values = Enumerable.Range(0, 20).Select(i => i - 10).ToArray();
        var s = ColumnStatistics.Create(Int32Column(), values, 0);
        Assert.Equal(-10L, s.MinBits);
        Assert.Equal(9L, s.MaxBits);
    }

    [Fact]
    public void Int32_VectorizedWithTail_CorrectMinMax()
    {
        // 17 elements: 2 full SIMD vectors (8 each) + 1 tail when width=8
        var values = new int[] { 100, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, -99 };
        var s = ColumnStatistics.Create(Int32Column(), values, 0);
        Assert.Equal(-99L, s.MinBits);
        Assert.Equal(100L, s.MaxBits);
    }

    // ──────────────── Int64 ────────────────

    [Fact]
    public void Int64_SingleValue_MinEqualsMax()
    {
        var s = ColumnStatistics.Create(Int64Column(), new long[] { 42L }, 0);
        Assert.Equal(ColumnStatistics.ColumnStatisticsValueKind.Int64, s.ValueKind);
        Assert.Equal(42L, s.MinBits);
        Assert.Equal(42L, s.MaxBits);
    }

    [Fact]
    public void Int64_MinAndMax_CorrectlyComputed()
    {
        var s = ColumnStatistics.Create(Int64Column(), new long[] { 5L, -3L, 10L, -100L, 7L }, 0);
        Assert.Equal(-100L, s.MinBits);
        Assert.Equal(10L, s.MaxBits);
    }

    [Fact]
    public void Int64_Empty_ReturnsEmpty()
    {
        var s = ColumnStatistics.Create(Int64Column(), ReadOnlySpan<long>.Empty, 0);
        Assert.Equal(ColumnStatistics.ColumnStatisticsValueKind.None, s.ValueKind);
    }

    // ──────────────── Float ────────────────

    [Fact]
    public void Float_NaNIgnored()
    {
        var s = ColumnStatistics.Create(FloatColumn(), new float[] { float.NaN, 3.5f, 1.0f, float.NaN, 9.0f }, 0);
        Assert.Equal(ColumnStatistics.ColumnStatisticsValueKind.Float, s.ValueKind);
        Assert.Equal(1.0f, BitConverter.Int32BitsToSingle((int)s.MinBits));
        Assert.Equal(9.0f, BitConverter.Int32BitsToSingle((int)s.MaxBits));
    }

    [Fact]
    public void Float_AllNaN_ReturnsNone()
    {
        var s = ColumnStatistics.Create(FloatColumn(), new float[] { float.NaN, float.NaN }, 0);
        Assert.Equal(ColumnStatistics.ColumnStatisticsValueKind.None, s.ValueKind);
    }

    [Fact]
    public void Float_SingleNonNaN_CorrectMinMax()
    {
        var s = ColumnStatistics.Create(FloatColumn(), new float[] { float.NaN, 5.0f }, 0);
        Assert.Equal(5.0f, BitConverter.Int32BitsToSingle((int)s.MinBits));
        Assert.Equal(5.0f, BitConverter.Int32BitsToSingle((int)s.MaxBits));
    }

    [Fact]
    public void Float_LeadingNaN_CorrectMinMax()
    {
        var s = ColumnStatistics.Create(FloatColumn(), new float[] { float.NaN, 5.0f, 2.0f, 8.0f }, 0);
        Assert.Equal(2.0f, BitConverter.Int32BitsToSingle((int)s.MinBits));
        Assert.Equal(8.0f, BitConverter.Int32BitsToSingle((int)s.MaxBits));
    }

    [Fact]
    public void Float_Empty_ReturnsEmpty()
    {
        var s = ColumnStatistics.Create(FloatColumn(), ReadOnlySpan<float>.Empty, 0);
        Assert.Equal(ColumnStatistics.ColumnStatisticsValueKind.None, s.ValueKind);
    }

    // ──────────────── Double ────────────────

    [Fact]
    public void Double_NaNIgnored()
    {
        var s = ColumnStatistics.Create(DoubleColumn(), new double[] { double.NaN, 3.5, 1.0, double.NaN, 9.0 }, 0);
        Assert.Equal(ColumnStatistics.ColumnStatisticsValueKind.Double, s.ValueKind);
        Assert.Equal(1.0, BitConverter.Int64BitsToDouble(s.MinBits));
        Assert.Equal(9.0, BitConverter.Int64BitsToDouble(s.MaxBits));
    }

    [Fact]
    public void Double_AllNaN_ReturnsNone()
    {
        var s = ColumnStatistics.Create(DoubleColumn(), new double[] { double.NaN, double.NaN }, 0);
        Assert.Equal(ColumnStatistics.ColumnStatisticsValueKind.None, s.ValueKind);
    }

    [Fact]
    public void Double_Empty_ReturnsEmpty()
    {
        var s = ColumnStatistics.Create(DoubleColumn(), ReadOnlySpan<double>.Empty, 0);
        Assert.Equal(ColumnStatistics.ColumnStatisticsValueKind.None, s.ValueKind);
    }

    // ──────────────── Boolean ────────────────

    [Fact]
    public void Boolean_AllTrue_MinAndMaxAreTrue()
    {
        var s = ColumnStatistics.Create(BoolColumn(), new bool[] { true, true, true }, 0);
        Assert.Equal(ColumnStatistics.ColumnStatisticsValueKind.Boolean, s.ValueKind);
        Assert.Equal(1L, s.MinBits);
        Assert.Equal(1L, s.MaxBits);
    }

    [Fact]
    public void Boolean_AllFalse_MinAndMaxAreFalse()
    {
        var s = ColumnStatistics.Create(BoolColumn(), new bool[] { false, false }, 0);
        Assert.Equal(0L, s.MinBits);
        Assert.Equal(0L, s.MaxBits);
    }

    [Fact]
    public void Boolean_Mixed_MinFalseMaxTrue()
    {
        var s = ColumnStatistics.Create(BoolColumn(), new bool[] { true, false, true }, 0);
        Assert.Equal(0L, s.MinBits);
        Assert.Equal(1L, s.MaxBits);
    }

    [Fact]
    public void Boolean_FalseFirst_CorrectMinMax()
    {
        var s = ColumnStatistics.Create(BoolColumn(), new bool[] { false, true }, 0);
        Assert.Equal(0L, s.MinBits);
        Assert.Equal(1L, s.MaxBits);
    }

    // ──────────────── ByteArray ────────────────

    [Fact]
    public void ByteArray_SingleElement_MinEqualsMax()
    {
        var data = new byte[] { 1, 2, 3 };
        var s = ColumnStatistics.Create(ByteArrayColumn(), new byte[][] { data }, 0);
        Assert.Equal(ColumnStatistics.ColumnStatisticsValueKind.Binary, s.ValueKind);
        Assert.True(s.MinValue?.SequenceEqual(data));
        Assert.True(s.MaxValue?.SequenceEqual(data));
    }

    [Fact]
    public void ByteArray_LexicographicOrdering()
    {
        var values = new byte[][] {
            new byte[] { 2, 0 },
            new byte[] { 1, 0 },
            new byte[] { 3, 0 }
        };
        var s = ColumnStatistics.Create(ByteArrayColumn(), values, 0);
        Assert.Equal(new byte[] { 1, 0 }, s.MinValue?.Take(s.MinValueLength).ToArray());
        Assert.Equal(new byte[] { 3, 0 }, s.MaxValue?.Take(s.MaxValueLength).ToArray());
    }

    [Fact]
    public void ByteArray_ShorterPrefixIsLess()
    {
        var shorter = new byte[] { 1 };
        var longer = new byte[] { 1, 0 };
        var s = ColumnStatistics.Create(ByteArrayColumn(), new byte[][] { longer, shorter }, 0);
        Assert.Equal(1, s.MinValueLength);
        Assert.Equal(shorter, s.MinValue?.Take(s.MinValueLength).ToArray());
    }

    // ──────────────── TryGetInt32MinMax ────────────────

    [Fact]
    public void TryGetInt32MinMax_Empty_ReturnsFalse()
    {
        Assert.False(ColumnStatistics.TryGetInt32MinMax(ReadOnlySpan<int>.Empty, out _, out _));
    }

    [Fact]
    public void TryGetInt32MinMax_SingleElement_ReturnsTrue()
    {
        Assert.True(ColumnStatistics.TryGetInt32MinMax(new int[] { 99 }, out var min, out var max));
        Assert.Equal(99, min);
        Assert.Equal(99, max);
    }

    [Fact]
    public void TryGetInt32MinMax_MultipleElements()
    {
        Assert.True(ColumnStatistics.TryGetInt32MinMax(new int[] { 3, -1, 5, 2 }, out var min, out var max));
        Assert.Equal(-1, min);
        Assert.Equal(5, max);
    }

    // ──────────────── Nullable Int32 ────────────────

    [Fact]
    public void NullableInt32_AllNulls_ReturnsEmpty()
    {
        var s = ColumnStatistics.CreateOptional<int>(Int32Column(), new int?[] { null, null });
        Assert.Equal(ColumnStatistics.ColumnStatisticsValueKind.None, s.ValueKind);
        Assert.Equal(2L, s.NullCount);
    }

    [Fact]
    public void NullableInt32_MixedNulls_CorrectMinMax()
    {
        var s = ColumnStatistics.CreateOptional<int>(Int32Column(), new int?[] { null, 5, null, -3, null });
        Assert.Equal(ColumnStatistics.ColumnStatisticsValueKind.Int32, s.ValueKind);
        Assert.Equal(-3L, s.MinBits);
        Assert.Equal(5L, s.MaxBits);
        Assert.Equal(3L, s.NullCount);
    }

    [Fact]
    public void NullableInt32_NoNulls_CorrectMinMax()
    {
        var s = ColumnStatistics.CreateOptional<int>(Int32Column(), new int?[] { 10, 1, 5 });
        Assert.Equal(1L, s.MinBits);
        Assert.Equal(10L, s.MaxBits);
        Assert.Equal(0L, s.NullCount);
    }

    // ──────────────── Nullable Boolean ────────────────

    [Fact]
    public void NullableBoolean_AllNulls_ReturnsEmpty()
    {
        var s = ColumnStatistics.CreateOptional<bool>(BoolColumn(), new bool?[] { null, null });
        Assert.Equal(ColumnStatistics.ColumnStatisticsValueKind.None, s.ValueKind);
        Assert.Equal(2L, s.NullCount);
    }

    [Fact]
    public void NullableBoolean_MixedNulls_CorrectMinMax()
    {
        var s = ColumnStatistics.CreateOptional<bool>(BoolColumn(), new bool?[] { null, true, null, false });
        Assert.Equal(0L, s.MinBits);
        Assert.Equal(1L, s.MaxBits);
        Assert.Equal(2L, s.NullCount);
    }

    // ──────────────── Byte statistics ────────────────

    [Fact]
    public void Byte_MinAndMax_CorrectlyComputed()
    {
        var s = ColumnStatistics.CreateByte(new byte[] { 10, 5, 200, 3, 150 }, 0);
        Assert.Equal(ColumnStatistics.ColumnStatisticsValueKind.Int32, s.ValueKind);
        Assert.Equal(3L, s.MinBits);
        Assert.Equal(200L, s.MaxBits);
    }

    [Fact]
    public void Byte_Empty_ReturnsEmpty()
    {
        var s = ColumnStatistics.CreateByte(ReadOnlySpan<byte>.Empty, 0);
        Assert.Equal(ColumnStatistics.ColumnStatisticsValueKind.None, s.ValueKind);
    }

    [Fact]
    public void Byte_SingleValue_MinEqualsMax()
    {
        var s = ColumnStatistics.CreateByte(new byte[] { 42 }, 0);
        Assert.Equal(42L, s.MinBits);
        Assert.Equal(42L, s.MaxBits);
    }
}
