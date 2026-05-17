using Plank.Schema;
using Plank.Writing;

namespace Plank.StrykerTests;

/// <summary>
/// Targets surviving mutants in ColumnStatistics.cs that the baseline tests missed.
/// Focuses on: Binary constructor (lines 35-43), DistinctCount logic (line 57),
/// WithNullCount (line 65), Empty (line 62), and nullable/optional paths.
/// </summary>
public class ColumnStatisticsExtendedTests
{
    static Column Int32Col(ParquetRepetition rep = ParquetRepetition.Required)
        => new("v", ParquetPhysicalType.Int32, new ColumnOptions(rep));

    static Column FloatCol() => new("v", ParquetPhysicalType.Float);
    static Column DoubleCol() => new("v", ParquetPhysicalType.Double);
    static Column BoolCol() => new("v", ParquetPhysicalType.Boolean);
    static Column ByteArrayCol() => new("v", ParquetPhysicalType.ByteArray);
    static Column Int64Col() => new("v", ParquetPhysicalType.Int64);

    // ──────────────── Binary stats (DistinctCount logic, lines 41-43) ────────────────

    [Fact]
    public void ByteArray_EqualMinMax_DistinctCountIsOne()
    {
        var val = new byte[] { 1, 2, 3 };
        var s = ColumnStatistics.Create(ByteArrayCol(), new byte[][] { val, val }, 0);
        Assert.Equal(1L, s.DistinctCount);
    }

    [Fact]
    public void ByteArray_DifferentMinMax_DistinctCountIsMinusOne()
    {
        var s = ColumnStatistics.Create(ByteArrayCol(),
            new byte[][] { new byte[] { 1 }, new byte[] { 2 } }, 0);
        Assert.Equal(-1L, s.DistinctCount);
    }

    [Fact]
    public void ByteArray_MinValueLength_CorrectlyCaptured()
    {
        var min = new byte[] { 1, 2 };
        var max = new byte[] { 3, 4, 5 };
        var s = ColumnStatistics.Create(ByteArrayCol(), new byte[][] { min, max }, 0);
        Assert.Equal(2, s.MinValueLength);
        Assert.Equal(3, s.MaxValueLength);
        Assert.Equal(ColumnStatistics.ColumnStatisticsValueKind.Binary, s.ValueKind);
    }

    // ──────────────── Empty (line 61-62) ────────────────

    [Fact]
    public void Empty_HasStatisticsTrue()
    {
        var s = ColumnStatistics.Empty(3);
        Assert.True(s.HasStatistics);
        Assert.Equal(3L, s.NullCount);
        Assert.Equal(ColumnStatistics.ColumnStatisticsValueKind.None, s.ValueKind);
    }

    [Fact]
    public void Empty_MinAndMaxAreNull()
    {
        var s = ColumnStatistics.Empty(0);
        Assert.Null(s.MinValue);
        Assert.Null(s.MaxValue);
        Assert.Equal(0, s.MinValueLength);
        Assert.Equal(0, s.MaxValueLength);
    }

    // ──────────────── WithNullCount (lines 64-67) ────────────────

    [Fact]
    public void WithNullCount_BinaryStats_PreservesMinMax()
    {
        var s = ColumnStatistics.Create(ByteArrayCol(),
            new byte[][] { new byte[] { 5 }, new byte[] { 10 } }, nullCount: 0);
        var updated = s.WithNullCount(7);
        Assert.Equal(7L, updated.NullCount);
        Assert.Equal(ColumnStatistics.ColumnStatisticsValueKind.Binary, updated.ValueKind);
        Assert.NotNull(updated.MinValue);
        Assert.NotNull(updated.MaxValue);
    }

    [Fact]
    public void WithNullCount_Int32Stats_PreservesMinMax()
    {
        var s = ColumnStatistics.Create(Int32Col(), new int[] { 1, 5, 3 }, nullCount: 0);
        var updated = s.WithNullCount(11);
        Assert.Equal(11L, updated.NullCount);
        Assert.Equal(ColumnStatistics.ColumnStatisticsValueKind.Int32, updated.ValueKind);
        Assert.Equal(1L, updated.MinBits);
        Assert.Equal(5L, updated.MaxBits);
    }

    [Fact]
    public void WithNullCount_BoolStats_PreservesKind()
    {
        var s = ColumnStatistics.Create(BoolCol(), new bool[] { true, false }, nullCount: 0);
        var updated = s.WithNullCount(4);
        Assert.Equal(4L, updated.NullCount);
        Assert.Equal(ColumnStatistics.ColumnStatisticsValueKind.Boolean, updated.ValueKind);
        Assert.Equal(0L, updated.MinBits);
        Assert.Equal(1L, updated.MaxBits);
    }

    // ──────────────── DistinctCount for numeric types (line 57) ────────────────

    [Fact]
    public void Int32_SameValue_DistinctCountIsOne()
    {
        var s = ColumnStatistics.Create(Int32Col(), new int[] { 42, 42, 42 }, 0);
        Assert.Equal(1L, s.DistinctCount);
    }

    [Fact]
    public void Int32_DifferentValues_DistinctCountIsMinusOne()
    {
        var s = ColumnStatistics.Create(Int32Col(), new int[] { 1, 2 }, 0);
        Assert.Equal(-1L, s.DistinctCount);
    }

    [Fact]
    public void Int64_SameValue_DistinctCountIsOne()
    {
        var s = ColumnStatistics.Create(Int64Col(), new long[] { 100L, 100L }, 0);
        Assert.Equal(1L, s.DistinctCount);
    }

    [Fact]
    public void Int64_DifferentValues_DistinctCountIsMinusOne()
    {
        var s = ColumnStatistics.Create(Int64Col(), new long[] { 1L, 2L }, 0);
        Assert.Equal(-1L, s.DistinctCount);
    }

    [Fact]
    public void Float_SameValue_DistinctCountIsOne()
    {
        var s = ColumnStatistics.Create(FloatCol(), new float[] { 1.5f, 1.5f }, 0);
        Assert.Equal(1L, s.DistinctCount);
    }

    [Fact]
    public void Double_DifferentValues_DistinctCountIsMinusOne()
    {
        var s = ColumnStatistics.Create(DoubleCol(), new double[] { 1.0, 2.0 }, 0);
        Assert.Equal(-1L, s.DistinctCount);
    }

    // ──────────────── HasStatistics flag ────────────────

    [Fact]
    public void Int32Stats_HasStatisticsIsTrue()
    {
        var s = ColumnStatistics.Create(Int32Col(), new int[] { 1 }, 0);
        Assert.True(s.HasStatistics);
    }

    // ──────────────── Nullable Int64 ────────────────

    [Fact]
    public void NullableInt64_AllNulls_EmptyResult()
    {
        var s = ColumnStatistics.CreateOptional<long>(Int64Col(), new long?[] { null, null });
        Assert.Equal(ColumnStatistics.ColumnStatisticsValueKind.None, s.ValueKind);
        Assert.Equal(2L, s.NullCount);
    }

    [Fact]
    public void NullableInt64_MixedNulls_CorrectMinMax()
    {
        var s = ColumnStatistics.CreateOptional<long>(Int64Col(),
            new long?[] { null, 100L, null, -50L });
        Assert.Equal(ColumnStatistics.ColumnStatisticsValueKind.Int64, s.ValueKind);
        Assert.Equal(-50L, s.MinBits);
        Assert.Equal(100L, s.MaxBits);
        Assert.Equal(2L, s.NullCount);
    }

    // ──────────────── Nullable Float ────────────────

    [Fact]
    public void NullableFloat_AllNulls_EmptyResult()
    {
        var s = ColumnStatistics.CreateOptional<float>(FloatCol(), new float?[] { null });
        Assert.Equal(ColumnStatistics.ColumnStatisticsValueKind.None, s.ValueKind);
        Assert.Equal(1L, s.NullCount);
    }

    [Fact]
    public void NullableFloat_MixedNulls_CorrectMinMax()
    {
        var s = ColumnStatistics.CreateOptional<float>(FloatCol(),
            new float?[] { 5.0f, null, 2.0f, null, 8.0f });
        Assert.Equal(2.0f, BitConverter.Int32BitsToSingle((int)s.MinBits));
        Assert.Equal(8.0f, BitConverter.Int32BitsToSingle((int)s.MaxBits));
        Assert.Equal(2L, s.NullCount);
    }

    // ──────────────── Nullable Double ────────────────

    [Fact]
    public void NullableDouble_MixedNulls_CorrectMinMax()
    {
        var s = ColumnStatistics.CreateOptional<double>(DoubleCol(),
            new double?[] { null, 3.0, 1.5, null });
        Assert.Equal(1.5, BitConverter.Int64BitsToDouble(s.MinBits));
        Assert.Equal(3.0, BitConverter.Int64BitsToDouble(s.MaxBits));
        Assert.Equal(2L, s.NullCount);
    }

    // ──────────────── UInt16 statistics ────────────────

    [Fact]
    public void UInt16_SingleValue_MinEqualsMax()
    {
        var s = ColumnStatistics.CreateUInt16(new ushort[] { 500 }, 0);
        Assert.Equal(500L, s.MinBits);
        Assert.Equal(500L, s.MaxBits);
    }

    [Fact]
    public void UInt16_MultipleValues_CorrectMinMax()
    {
        var s = ColumnStatistics.CreateUInt16(new ushort[] { 300, 100, 500, 200 }, 0);
        Assert.Equal(100L, s.MinBits);
        Assert.Equal(500L, s.MaxBits);
    }

    [Fact]
    public void UInt16_Empty_ReturnsEmpty()
    {
        var s = ColumnStatistics.CreateUInt16(ReadOnlySpan<ushort>.Empty, 5);
        Assert.Equal(ColumnStatistics.ColumnStatisticsValueKind.None, s.ValueKind);
        Assert.Equal(5L, s.NullCount);
    }

    // ──────────────── UInt32 statistics ────────────────

    [Fact]
    public void UInt32_CorrectMinMax()
    {
        var s = ColumnStatistics.CreateUInt32(new uint[] { 1000, 500, 2000, 100 }, 0);
        Assert.Equal(100L, s.MinBits);
        Assert.Equal(2000L, s.MaxBits);
    }

    [Fact]
    public void UInt32_Empty_ReturnsEmpty()
    {
        var s = ColumnStatistics.CreateUInt32(ReadOnlySpan<uint>.Empty, 0);
        Assert.Equal(ColumnStatistics.ColumnStatisticsValueKind.None, s.ValueKind);
    }

    // ──────────────── UInt64 statistics ────────────────

    [Fact]
    public void UInt64_CorrectMinMax()
    {
        var s = ColumnStatistics.CreateUInt64(new ulong[] { 100UL, 999UL, 50UL }, 0);
        Assert.Equal(50L, s.MinBits);   // stored as unchecked cast
        Assert.Equal(999L, s.MaxBits);
    }

    [Fact]
    public void UInt64_Empty_ReturnsEmpty()
    {
        var s = ColumnStatistics.CreateUInt64(ReadOnlySpan<ulong>.Empty, 0);
        Assert.Equal(ColumnStatistics.ColumnStatisticsValueKind.None, s.ValueKind);
    }

    // ──────────────── NullableByte ────────────────

    [Fact]
    public void NullableByte_AllNulls_ReturnsEmpty()
    {
        var s = ColumnStatistics.CreateNullableByte(new byte?[] { null, null });
        Assert.Equal(ColumnStatistics.ColumnStatisticsValueKind.None, s.ValueKind);
        Assert.Equal(2L, s.NullCount);
    }

    [Fact]
    public void NullableByte_MixedValues_CorrectMinMax()
    {
        var s = ColumnStatistics.CreateNullableByte(new byte?[] { null, 5, null, 200, 10 });
        Assert.Equal(5L, s.MinBits);
        Assert.Equal(200L, s.MaxBits);
        Assert.Equal(2L, s.NullCount);
    }

    [Fact]
    public void NullableByte_SingleValue()
    {
        var s = ColumnStatistics.CreateNullableByte(new byte?[] { 42 });
        Assert.Equal(42L, s.MinBits);
        Assert.Equal(42L, s.MaxBits);
        Assert.Equal(0L, s.NullCount);
    }

    // ──────────────── NullableUInt16 ────────────────

    [Fact]
    public void NullableUInt16_MixedValues_CorrectMinMax()
    {
        var s = ColumnStatistics.CreateNullableUInt16(new ushort?[] { 100, null, 50, 200 });
        Assert.Equal(50L, s.MinBits);
        Assert.Equal(200L, s.MaxBits);
        Assert.Equal(1L, s.NullCount);
    }

    [Fact]
    public void NullableUInt16_AllNulls_ReturnsEmpty()
    {
        var s = ColumnStatistics.CreateNullableUInt16(new ushort?[] { null });
        Assert.Equal(ColumnStatistics.ColumnStatisticsValueKind.None, s.ValueKind);
    }

    // ──────────────── NullableUInt32 ────────────────

    [Fact]
    public void NullableUInt32_MixedValues_CorrectMinMax()
    {
        var s = ColumnStatistics.CreateNullableUInt32(new uint?[] { 1000, null, 500, 2000 });
        Assert.Equal(500L, s.MinBits);
        Assert.Equal(2000L, s.MaxBits);
    }

    // ──────────────── NullableUInt64 ────────────────

    [Fact]
    public void NullableUInt64_MixedValues_CorrectMinMax()
    {
        var s = ColumnStatistics.CreateNullableUInt64(new ulong?[] { null, 100UL, 999UL, null });
        // stored as unchecked(long)
        Assert.Equal(100L, s.MinBits);
        Assert.Equal(999L, s.MaxBits);
        Assert.Equal(2L, s.NullCount);
    }
}
