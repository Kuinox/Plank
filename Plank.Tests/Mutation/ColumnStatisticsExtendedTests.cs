using Plank.Schema;
using Plank.Writing;

namespace Plank.Tests.Mutation;

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

    [Test]
    public void ByteArray_EqualMinMax_DistinctCountIsOne()
    {
        var val = new byte[] { 1, 2, 3 };
        var s = ColumnStatistics.Create(ByteArrayCol(), new byte[][] { val, val }, 0);
        ClassicAssert.AreEqual(1L, s.DistinctCount);
    }

    [Test]
    public void ByteArray_DifferentMinMax_DistinctCountIsMinusOne()
    {
        var s = ColumnStatistics.Create(ByteArrayCol(),
            new byte[][] { new byte[] { 1 }, new byte[] { 2 } }, 0);
        ClassicAssert.AreEqual(-1L, s.DistinctCount);
    }

    [Test]
    public void ByteArray_MinValueLength_CorrectlyCaptured()
    {
        var min = new byte[] { 1, 2 };
        var max = new byte[] { 3, 4, 5 };
        var s = ColumnStatistics.Create(ByteArrayCol(), new byte[][] { min, max }, 0);
        ClassicAssert.AreEqual(2, s.MinValueLength);
        ClassicAssert.AreEqual(3, s.MaxValueLength);
        ClassicAssert.AreEqual(ColumnStatistics.ColumnStatisticsValueKind.Binary, s.ValueKind);
    }

    // ──────────────── Empty (line 61-62) ────────────────

    [Test]
    public void Empty_HasStatisticsTrue()
    {
        var s = ColumnStatistics.Empty(3);
        ClassicAssert.IsTrue(s.HasStatistics);
        ClassicAssert.AreEqual(3L, s.NullCount);
        ClassicAssert.AreEqual(ColumnStatistics.ColumnStatisticsValueKind.None, s.ValueKind);
    }

    [Test]
    public void Empty_MinAndMaxAreNull()
    {
        var s = ColumnStatistics.Empty(0);
        ClassicAssert.IsNull(s.MinValue);
        ClassicAssert.IsNull(s.MaxValue);
        ClassicAssert.AreEqual(0, s.MinValueLength);
        ClassicAssert.AreEqual(0, s.MaxValueLength);
    }

    // ──────────────── WithNullCount (lines 64-67) ────────────────

    [Test]
    public void WithNullCount_BinaryStats_PreservesMinMax()
    {
        var s = ColumnStatistics.Create(ByteArrayCol(),
            new byte[][] { new byte[] { 5 }, new byte[] { 10 } }, nullCount: 0);
        var updated = s.WithNullCount(7);
        ClassicAssert.AreEqual(7L, updated.NullCount);
        ClassicAssert.AreEqual(ColumnStatistics.ColumnStatisticsValueKind.Binary, updated.ValueKind);
        ClassicAssert.IsNotNull(updated.MinValue);
        ClassicAssert.IsNotNull(updated.MaxValue);
    }

    [Test]
    public void WithNullCount_Int32Stats_PreservesMinMax()
    {
        var s = ColumnStatistics.Create(Int32Col(), new int[] { 1, 5, 3 }, nullCount: 0);
        var updated = s.WithNullCount(11);
        ClassicAssert.AreEqual(11L, updated.NullCount);
        ClassicAssert.AreEqual(ColumnStatistics.ColumnStatisticsValueKind.Int32, updated.ValueKind);
        ClassicAssert.AreEqual(1L, updated.MinBits);
        ClassicAssert.AreEqual(5L, updated.MaxBits);
    }

    [Test]
    public void WithNullCount_BoolStats_PreservesKind()
    {
        var s = ColumnStatistics.Create(BoolCol(), new bool[] { true, false }, nullCount: 0);
        var updated = s.WithNullCount(4);
        ClassicAssert.AreEqual(4L, updated.NullCount);
        ClassicAssert.AreEqual(ColumnStatistics.ColumnStatisticsValueKind.Boolean, updated.ValueKind);
        ClassicAssert.AreEqual(0L, updated.MinBits);
        ClassicAssert.AreEqual(1L, updated.MaxBits);
    }

    // ──────────────── DistinctCount for numeric types (line 57) ────────────────

    [Test]
    public void Int32_SameValue_DistinctCountIsOne()
    {
        var s = ColumnStatistics.Create(Int32Col(), new int[] { 42, 42, 42 }, 0);
        ClassicAssert.AreEqual(1L, s.DistinctCount);
    }

    [Test]
    public void Int32_DifferentValues_DistinctCountIsMinusOne()
    {
        var s = ColumnStatistics.Create(Int32Col(), new int[] { 1, 2 }, 0);
        ClassicAssert.AreEqual(-1L, s.DistinctCount);
    }

    [Test]
    public void Int64_SameValue_DistinctCountIsOne()
    {
        var s = ColumnStatistics.Create(Int64Col(), new long[] { 100L, 100L }, 0);
        ClassicAssert.AreEqual(1L, s.DistinctCount);
    }

    [Test]
    public void Int64_DifferentValues_DistinctCountIsMinusOne()
    {
        var s = ColumnStatistics.Create(Int64Col(), new long[] { 1L, 2L }, 0);
        ClassicAssert.AreEqual(-1L, s.DistinctCount);
    }

    [Test]
    public void Float_SameValue_DistinctCountIsOne()
    {
        var s = ColumnStatistics.Create(FloatCol(), new float[] { 1.5f, 1.5f }, 0);
        ClassicAssert.AreEqual(1L, s.DistinctCount);
    }

    [Test]
    public void Double_DifferentValues_DistinctCountIsMinusOne()
    {
        var s = ColumnStatistics.Create(DoubleCol(), new double[] { 1.0, 2.0 }, 0);
        ClassicAssert.AreEqual(-1L, s.DistinctCount);
    }

    // ──────────────── HasStatistics flag ────────────────

    [Test]
    public void Int32Stats_HasStatisticsIsTrue()
    {
        var s = ColumnStatistics.Create(Int32Col(), new int[] { 1 }, 0);
        ClassicAssert.IsTrue(s.HasStatistics);
    }

    // ──────────────── Nullable Int64 ────────────────

    [Test]
    public void NullableInt64_AllNulls_EmptyResult()
    {
        var s = ColumnStatistics.CreateOptional<long>(Int64Col(), new long?[] { null, null });
        ClassicAssert.AreEqual(ColumnStatistics.ColumnStatisticsValueKind.None, s.ValueKind);
        ClassicAssert.AreEqual(2L, s.NullCount);
    }

    [Test]
    public void NullableInt64_MixedNulls_CorrectMinMax()
    {
        var s = ColumnStatistics.CreateOptional<long>(Int64Col(),
            new long?[] { null, 100L, null, -50L });
        ClassicAssert.AreEqual(ColumnStatistics.ColumnStatisticsValueKind.Int64, s.ValueKind);
        ClassicAssert.AreEqual(-50L, s.MinBits);
        ClassicAssert.AreEqual(100L, s.MaxBits);
        ClassicAssert.AreEqual(2L, s.NullCount);
    }

    // ──────────────── Nullable Float ────────────────

    [Test]
    public void NullableFloat_AllNulls_EmptyResult()
    {
        var s = ColumnStatistics.CreateOptional<float>(FloatCol(), new float?[] { null });
        ClassicAssert.AreEqual(ColumnStatistics.ColumnStatisticsValueKind.None, s.ValueKind);
        ClassicAssert.AreEqual(1L, s.NullCount);
    }

    [Test]
    public void NullableFloat_MixedNulls_CorrectMinMax()
    {
        var s = ColumnStatistics.CreateOptional<float>(FloatCol(),
            new float?[] { 5.0f, null, 2.0f, null, 8.0f });
        ClassicAssert.AreEqual(2.0f, BitConverter.Int32BitsToSingle((int)s.MinBits));
        ClassicAssert.AreEqual(8.0f, BitConverter.Int32BitsToSingle((int)s.MaxBits));
        ClassicAssert.AreEqual(2L, s.NullCount);
    }

    // ──────────────── Nullable Double ────────────────

    [Test]
    public void NullableDouble_MixedNulls_CorrectMinMax()
    {
        var s = ColumnStatistics.CreateOptional<double>(DoubleCol(),
            new double?[] { null, 3.0, 1.5, null });
        ClassicAssert.AreEqual(1.5, BitConverter.Int64BitsToDouble(s.MinBits));
        ClassicAssert.AreEqual(3.0, BitConverter.Int64BitsToDouble(s.MaxBits));
        ClassicAssert.AreEqual(2L, s.NullCount);
    }

    // ──────────────── UInt16 statistics ────────────────

    [Test]
    public void UInt16_SingleValue_MinEqualsMax()
    {
        var s = ColumnStatistics.CreateUInt16(new ushort[] { 500 }, 0);
        ClassicAssert.AreEqual(500L, s.MinBits);
        ClassicAssert.AreEqual(500L, s.MaxBits);
    }

    [Test]
    public void UInt16_MultipleValues_CorrectMinMax()
    {
        var s = ColumnStatistics.CreateUInt16(new ushort[] { 300, 100, 500, 200 }, 0);
        ClassicAssert.AreEqual(100L, s.MinBits);
        ClassicAssert.AreEqual(500L, s.MaxBits);
    }

    [Test]
    public void UInt16_Empty_ReturnsEmpty()
    {
        var s = ColumnStatistics.CreateUInt16(ReadOnlySpan<ushort>.Empty, 5);
        ClassicAssert.AreEqual(ColumnStatistics.ColumnStatisticsValueKind.None, s.ValueKind);
        ClassicAssert.AreEqual(5L, s.NullCount);
    }

    // ──────────────── UInt32 statistics ────────────────

    [Test]
    public void UInt32_CorrectMinMax()
    {
        var s = ColumnStatistics.CreateUInt32(new uint[] { 1000, 500, 2000, 100 }, 0);
        ClassicAssert.AreEqual(100L, s.MinBits);
        ClassicAssert.AreEqual(2000L, s.MaxBits);
    }

    [Test]
    public void UInt32_Empty_ReturnsEmpty()
    {
        var s = ColumnStatistics.CreateUInt32(ReadOnlySpan<uint>.Empty, 0);
        ClassicAssert.AreEqual(ColumnStatistics.ColumnStatisticsValueKind.None, s.ValueKind);
    }

    // ──────────────── UInt64 statistics ────────────────

    [Test]
    public void UInt64_CorrectMinMax()
    {
        var s = ColumnStatistics.CreateUInt64(new ulong[] { 100UL, 999UL, 50UL }, 0);
        ClassicAssert.AreEqual(50L, s.MinBits);   // stored as unchecked cast
        ClassicAssert.AreEqual(999L, s.MaxBits);
    }

    [Test]
    public void UInt64_Empty_ReturnsEmpty()
    {
        var s = ColumnStatistics.CreateUInt64(ReadOnlySpan<ulong>.Empty, 0);
        ClassicAssert.AreEqual(ColumnStatistics.ColumnStatisticsValueKind.None, s.ValueKind);
    }

    // ──────────────── NullableByte ────────────────

    [Test]
    public void NullableByte_AllNulls_ReturnsEmpty()
    {
        var s = ColumnStatistics.CreateNullableByte(new byte?[] { null, null });
        ClassicAssert.AreEqual(ColumnStatistics.ColumnStatisticsValueKind.None, s.ValueKind);
        ClassicAssert.AreEqual(2L, s.NullCount);
    }

    [Test]
    public void NullableByte_MixedValues_CorrectMinMax()
    {
        var s = ColumnStatistics.CreateNullableByte(new byte?[] { null, 5, null, 200, 10 });
        ClassicAssert.AreEqual(5L, s.MinBits);
        ClassicAssert.AreEqual(200L, s.MaxBits);
        ClassicAssert.AreEqual(2L, s.NullCount);
    }

    [Test]
    public void NullableByte_SingleValue()
    {
        var s = ColumnStatistics.CreateNullableByte(new byte?[] { 42 });
        ClassicAssert.AreEqual(42L, s.MinBits);
        ClassicAssert.AreEqual(42L, s.MaxBits);
        ClassicAssert.AreEqual(0L, s.NullCount);
    }

    // ──────────────── NullableUInt16 ────────────────

    [Test]
    public void NullableUInt16_MixedValues_CorrectMinMax()
    {
        var s = ColumnStatistics.CreateNullableUInt16(new ushort?[] { 100, null, 50, 200 });
        ClassicAssert.AreEqual(50L, s.MinBits);
        ClassicAssert.AreEqual(200L, s.MaxBits);
        ClassicAssert.AreEqual(1L, s.NullCount);
    }

    [Test]
    public void NullableUInt16_AllNulls_ReturnsEmpty()
    {
        var s = ColumnStatistics.CreateNullableUInt16(new ushort?[] { null });
        ClassicAssert.AreEqual(ColumnStatistics.ColumnStatisticsValueKind.None, s.ValueKind);
    }

    // ──────────────── NullableUInt32 ────────────────

    [Test]
    public void NullableUInt32_MixedValues_CorrectMinMax()
    {
        var s = ColumnStatistics.CreateNullableUInt32(new uint?[] { 1000, null, 500, 2000 });
        ClassicAssert.AreEqual(500L, s.MinBits);
        ClassicAssert.AreEqual(2000L, s.MaxBits);
    }

    // ──────────────── NullableUInt64 ────────────────

    [Test]
    public void NullableUInt64_MixedValues_CorrectMinMax()
    {
        var s = ColumnStatistics.CreateNullableUInt64(new ulong?[] { null, 100UL, 999UL, null });
        // stored as unchecked(long)
        ClassicAssert.AreEqual(100L, s.MinBits);
        ClassicAssert.AreEqual(999L, s.MaxBits);
        ClassicAssert.AreEqual(2L, s.NullCount);
    }
}
