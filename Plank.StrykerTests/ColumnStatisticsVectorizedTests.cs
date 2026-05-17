using Plank.Schema;
using Plank.Writing;
using System.Numerics;

namespace Plank.StrykerTests;

/// <summary>
/// Targets surviving mutants in ColumnStatistics.cs vectorized paths (lines 1030-1043):
/// - Line 1030: if (minCandidate &lt; min) — lane reduction for SIMD double min
/// - Line 1032: if (maxCandidate > max) — lane reduction for SIMD double max
/// - Line 1041/1043: tail processing after SIMD blocks
///
/// Requires at least Vector&lt;double&gt;.Count (typically 4) values to hit SIMD.
/// </summary>
public class ColumnStatisticsVectorizedTests
{
    static Column DoubleCol() => new("v", ParquetPhysicalType.Double);
    static Column FloatCol() => new("v", ParquetPhysicalType.Float);
    static Column Int32Col() => new("v", ParquetPhysicalType.Int32);

    static int VectorDoubleWidth => Vector<double>.Count;
    static int VectorIntWidth => Vector<int>.Count;

    // ──────────────── Double vectorized — min in non-first lane ────────────────

    [Fact]
    public void Double_MinInSecondLane_CorrectMinMax()
    {
        // Vector<double>.Count = 4 typically (AVX2). Min at lane[1].
        // After SIMD: minVector = [5,1,3,7] → lane reduction sets min=1 from lane[1]
        var values = new double[] { 5.0, 1.0, 3.0, 7.0, 2.0 }; // 4 SIMD + 1 tail
        var s = ColumnStatistics.Create(DoubleCol(), values, 0);
        Assert.Equal(1.0, BitConverter.Int64BitsToDouble(s.MinBits));
        Assert.Equal(7.0, BitConverter.Int64BitsToDouble(s.MaxBits));
    }

    [Fact]
    public void Double_MaxInThirdLane_CorrectMinMax()
    {
        // Max at lane[2], min at lane[0]
        var values = new double[] { 1.0, 3.0, 10.0, 2.0, 4.0 };
        var s = ColumnStatistics.Create(DoubleCol(), values, 0);
        Assert.Equal(1.0, BitConverter.Int64BitsToDouble(s.MinBits));
        Assert.Equal(10.0, BitConverter.Int64BitsToDouble(s.MaxBits));
    }

    [Fact]
    public void Double_MinInLastLane_CorrectMinMax()
    {
        // Min at the last lane
        var width = VectorDoubleWidth;
        var values = new double[width + 1];
        values[0] = 5.0;
        for (var i = 1; i < width - 1; i++) values[i] = 3.0;
        values[width - 1] = 1.0;  // min is at last SIMD lane
        values[width] = 4.0;       // tail
        var s = ColumnStatistics.Create(DoubleCol(), values, 0);
        Assert.Equal(1.0, BitConverter.Int64BitsToDouble(s.MinBits));
        Assert.Equal(5.0, BitConverter.Int64BitsToDouble(s.MaxBits));
    }

    [Fact]
    public void Double_MaxInLastLane_CorrectMinMax()
    {
        var width = VectorDoubleWidth;
        var values = new double[width];
        values[0] = 2.0;
        for (var i = 1; i < width - 1; i++) values[i] = 3.0;
        values[width - 1] = 100.0;  // max is at last SIMD lane
        var s = ColumnStatistics.Create(DoubleCol(), values, 0);
        Assert.Equal(2.0, BitConverter.Int64BitsToDouble(s.MinBits));
        Assert.Equal(100.0, BitConverter.Int64BitsToDouble(s.MaxBits));
    }

    [Fact]
    public void Double_TailHasMin_CorrectMinMax()
    {
        // Tail value (after SIMD blocks) is the minimum — line 1041
        var width = VectorDoubleWidth;
        var values = new double[width + 1];
        for (var i = 0; i < width; i++) values[i] = 5.0;
        values[width] = -999.0;  // tail is the minimum
        var s = ColumnStatistics.Create(DoubleCol(), values, 0);
        Assert.Equal(-999.0, BitConverter.Int64BitsToDouble(s.MinBits));
        Assert.Equal(5.0, BitConverter.Int64BitsToDouble(s.MaxBits));
    }

    [Fact]
    public void Double_TailHasMax_CorrectMinMax()
    {
        // Tail value is the maximum — line 1043
        var width = VectorDoubleWidth;
        var values = new double[width + 1];
        for (var i = 0; i < width; i++) values[i] = 2.0;
        values[width] = 999.0;  // tail is the maximum
        var s = ColumnStatistics.Create(DoubleCol(), values, 0);
        Assert.Equal(2.0, BitConverter.Int64BitsToDouble(s.MinBits));
        Assert.Equal(999.0, BitConverter.Int64BitsToDouble(s.MaxBits));
    }

    [Fact]
    public void Double_MultipleVectorBlocks_CorrectMinMax()
    {
        // Two full SIMD blocks — exercises the loop in TryGetDoubleMinMaxVectorized
        var width = VectorDoubleWidth;
        var values = Enumerable.Range(0, width * 2).Select(i => (double)(i * 2)).ToArray();
        values[width + 1] = -50.0;  // min is in the second block, lane 1
        var s = ColumnStatistics.Create(DoubleCol(), values, 0);
        Assert.Equal(-50.0, BitConverter.Int64BitsToDouble(s.MinBits));
        Assert.Equal((double)((width * 2 - 1) * 2), BitConverter.Int64BitsToDouble(s.MaxBits));
    }

    [Fact]
    public void Double_ExactlyVectorWidth_CorrectMinMax()
    {
        // Exactly one SIMD block, no tail
        var width = VectorDoubleWidth;
        var values = new double[width];
        for (var i = 0; i < width; i++) values[i] = (double)(i + 1);
        var s = ColumnStatistics.Create(DoubleCol(), values, 0);
        Assert.Equal(1.0, BitConverter.Int64BitsToDouble(s.MinBits));
        Assert.Equal((double)width, BitConverter.Int64BitsToDouble(s.MaxBits));
    }

    // ──────────────── Int32 vectorized — lanes and tail ────────────────

    [Fact]
    public void Int32_MinInNonFirstLane_Vectorized()
    {
        var width = VectorIntWidth;
        if (width < 2) return; // Skip if no SIMD benefit

        // Create values where min is in the last lane of first SIMD vector
        var values = new int[width + 1];
        for (var i = 0; i < width - 1; i++) values[i] = 100;
        values[width - 1] = -999;  // min at last SIMD lane
        values[width] = 50;        // tail
        var s = ColumnStatistics.Create(Int32Col(), values, 0);
        Assert.Equal(-999L, s.MinBits);
        Assert.Equal(100L, s.MaxBits);
    }

    [Fact]
    public void Int32_TailHasMin_Vectorized()
    {
        var width = VectorIntWidth;
        var values = new int[width + 1];
        for (var i = 0; i < width; i++) values[i] = 10;
        values[width] = -500;  // tail is minimum
        var s = ColumnStatistics.Create(Int32Col(), values, 0);
        Assert.Equal(-500L, s.MinBits);
        Assert.Equal(10L, s.MaxBits);
    }

    [Fact]
    public void Int32_TailHasMax_Vectorized()
    {
        var width = VectorIntWidth;
        var values = new int[width + 1];
        for (var i = 0; i < width; i++) values[i] = 10;
        values[width] = 500;  // tail is maximum
        var s = ColumnStatistics.Create(Int32Col(), values, 0);
        Assert.Equal(10L, s.MinBits);
        Assert.Equal(500L, s.MaxBits);
    }

    [Fact]
    public void Int32_LargeVectorized_CorrectMinMax()
    {
        // Large array with min and max at extreme ends
        var values = Enumerable.Range(1, 100).ToArray();
        values[0] = -1000;
        values[99] = 5000;
        var s = ColumnStatistics.Create(Int32Col(), values, 0);
        Assert.Equal(-1000L, s.MinBits);
        Assert.Equal(5000L, s.MaxBits);
    }

    // ──────────────── Float vectorized (similar paths for float) ────────────────

    [Fact]
    public void Float_MinInNonFirstLane_Vectorized()
    {
        var width = Vector<float>.Count;
        var values = new float[width + 1];
        for (var i = 0; i < width - 1; i++) values[i] = 3.0f;
        values[width - 1] = -99.0f;  // min at last SIMD lane
        values[width] = 1.0f;
        var s = ColumnStatistics.Create(FloatCol(), values, 0);
        Assert.Equal(-99.0f, BitConverter.Int32BitsToSingle((int)s.MinBits));
    }

    [Fact]
    public void Float_TailHasMin_Vectorized()
    {
        var width = Vector<float>.Count;
        var values = new float[width + 1];
        for (var i = 0; i < width; i++) values[i] = 5.0f;
        values[width] = -100.0f;
        var s = ColumnStatistics.Create(FloatCol(), values, 0);
        Assert.Equal(-100.0f, BitConverter.Int32BitsToSingle((int)s.MinBits));
        Assert.Equal(5.0f, BitConverter.Int32BitsToSingle((int)s.MaxBits));
    }

    // ──────────────── Nullable float vectorized (line 599) ────────────────

    [Fact]
    public void NullableFloat_LargeInput_VectorizedDensification()
    {
        // More than 256 values → triggers heap allocation instead of stackalloc
        // (line 599: values.Length <= 256 ? stackalloc : new float[values.Length])
        var values = Enumerable.Range(0, 300).Select(i => (float?)i).ToArray();
        values[150] = null;  // one null in the middle
        var s = ColumnStatistics.CreateOptional<float>(FloatCol(), values.AsSpan());
        Assert.Equal(0.0f, BitConverter.Int32BitsToSingle((int)s.MinBits));
        Assert.Equal(299.0f, BitConverter.Int32BitsToSingle((int)s.MaxBits));
        Assert.Equal(1L, s.NullCount);
    }

    [Fact]
    public void NullableFloat_ExactlyAt256Boundary_Stackalloc()
    {
        // 256 values → stackalloc path (line 599: values.Length <= 256)
        var values = Enumerable.Range(0, 256).Select(i => (float?)i).ToArray();
        var s = ColumnStatistics.CreateOptional<float>(FloatCol(), values.AsSpan());
        Assert.Equal(0.0f, BitConverter.Int32BitsToSingle((int)s.MinBits));
        Assert.Equal(255.0f, BitConverter.Int32BitsToSingle((int)s.MaxBits));
    }

    // ──────────────── Nullable double vectorized (line 628) ────────────────

    [Fact]
    public void NullableDouble_LargeInput_VectorizedDensification()
    {
        // 300 double? values → heap allocation (line 628: > 256)
        var values = Enumerable.Range(0, 300).Select(i => (double?)i).ToArray();
        values[200] = null;
        var s = ColumnStatistics.CreateOptional<double>(DoubleCol(), values.AsSpan());
        Assert.Equal(0.0, BitConverter.Int64BitsToDouble(s.MinBits));
        Assert.Equal(299.0, BitConverter.Int64BitsToDouble(s.MaxBits));
        Assert.Equal(1L, s.NullCount);
    }

    [Fact]
    public void NullableDouble_ExactlyAt256_Stackalloc()
    {
        var values = Enumerable.Range(0, 256).Select(i => (double?)i).ToArray();
        var s = ColumnStatistics.CreateOptional<double>(DoubleCol(), values.AsSpan());
        Assert.Equal(0.0, BitConverter.Int64BitsToDouble(s.MinBits));
        Assert.Equal(255.0, BitConverter.Int64BitsToDouble(s.MaxBits));
    }
}
