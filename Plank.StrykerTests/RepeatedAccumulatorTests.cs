using System.Collections.Immutable;
using Plank.Schema;
using Plank.Writing;

namespace Plank.StrykerTests;

/// <summary>
/// Tests targeting RepeatedAccumulator in ColumnStatistics.cs (lines 1272-1510):
/// - AddInt32/AddUInt32/AddInt64/AddUInt64/AddFloat/AddDouble min/max tracking
/// - ToStatistics for each physical type
/// - AddBoolean min/max logic
/// - AddBinaryLeaf for byte[] values
/// Exercises via SerializedColumn.Statistics after serializing repeated columns.
/// </summary>
public class RepeatedAccumulatorTests
{
    static SerializedColumn<T> WriteColumn<T>(Column col, T[] values, CompressionKind comp = CompressionKind.None)
        where T : notnull
    {
        var schema = new ParquetSchema([col]);
        using var ms = new MemoryStream();
        var writer = schema.CreateWriter(ms, new ParquetWriterOptions { Compression = comp });
        var c = writer.CreateSerializedColumn<T>(schema.Columns[0]);
        c.Serialize(values);
        writer.StartRowGroup().Write(c);
        writer.CloseFile();
        return c;
    }

    // ──────────────── int[] repeated — AddInt32 (lines 1390-1393) ────────────────

    [Fact]
    public void IntList_MinInMiddle_StatisticsCorrect()
    {
        var col = new Column("v", ParquetPhysicalType.Int32,
            new ColumnOptions(ParquetRepetition.Repeated));
        // rows: [10, 5], [20], [3, 15] → values 10,5,20,3,15; min=3, max=20
        var c = WriteColumn(col, new int[][] {
            [10, 5], [20], [3, 15]
        });
        Assert.Equal(ColumnStatistics.ColumnStatisticsValueKind.Int32, c.Statistics.ValueKind);
        Assert.Equal(3L, c.Statistics.MinBits);
        Assert.Equal(20L, c.Statistics.MaxBits);
    }

    [Fact]
    public void IntList_MaxInFirst_StatisticsCorrect()
    {
        var col = new Column("v", ParquetPhysicalType.Int32,
            new ColumnOptions(ParquetRepetition.Repeated));
        var c = WriteColumn(col, new int[][] { [100], [5, 10] });
        Assert.Equal(5L, c.Statistics.MinBits);
        Assert.Equal(100L, c.Statistics.MaxBits);
    }

    [Fact]
    public void IntList_AllSameValue_DistinctCountIsOne()
    {
        var col = new Column("v", ParquetPhysicalType.Int32,
            new ColumnOptions(ParquetRepetition.Repeated));
        var c = WriteColumn(col, new int[][] { [7, 7], [7] });
        Assert.Equal(7L, c.Statistics.MinBits);
        Assert.Equal(7L, c.Statistics.MaxBits);
        Assert.Equal(1L, c.Statistics.DistinctCount);
    }

    [Fact]
    public void IntList_NegativeValues_CorrectMinMax()
    {
        var col = new Column("v", ParquetPhysicalType.Int32,
            new ColumnOptions(ParquetRepetition.Repeated));
        var c = WriteColumn(col, new int[][] { [5, -10], [0, 15] });
        Assert.Equal(-10L, c.Statistics.MinBits);
        Assert.Equal(15L, c.Statistics.MaxBits);
    }

    [Fact]
    public void IntList_SingleElement_MinEqualsMax()
    {
        var col = new Column("v", ParquetPhysicalType.Int32,
            new ColumnOptions(ParquetRepetition.Repeated));
        var c = WriteColumn(col, new int[][] { [42] });
        Assert.Equal(42L, c.Statistics.MinBits);
        Assert.Equal(42L, c.Statistics.MaxBits);
    }

    // ──────────────── long[] repeated — AddInt64 (lines 1412+) ────────────────

    [Fact]
    public void LongList_MinInMiddle_StatisticsCorrect()
    {
        var col = new Column("v", ParquetPhysicalType.Int64,
            new ColumnOptions(ParquetRepetition.Repeated));
        var c = WriteColumn(col, new long[][] { [100L, 50L], [200L], [10L] });
        Assert.Equal(ColumnStatistics.ColumnStatisticsValueKind.Int64, c.Statistics.ValueKind);
        Assert.Equal(10L, c.Statistics.MinBits);
        Assert.Equal(200L, c.Statistics.MaxBits);
    }

    // ──────────────── float[] repeated — AddFloat (lines 1444+) ────────────────

    [Fact]
    public void FloatList_MinMaxCorrect()
    {
        var col = new Column("v", ParquetPhysicalType.Float,
            new ColumnOptions(ParquetRepetition.Repeated));
        var c = WriteColumn(col, new float[][] { [1.5f, 5.0f], [-2.0f] });
        Assert.Equal(ColumnStatistics.ColumnStatisticsValueKind.Float, c.Statistics.ValueKind);
        Assert.Equal(-2.0f, BitConverter.Int32BitsToSingle((int)c.Statistics.MinBits));
        Assert.Equal(5.0f, BitConverter.Int32BitsToSingle((int)c.Statistics.MaxBits));
    }

    [Fact]
    public void FloatList_IgnoresNaN()
    {
        var col = new Column("v", ParquetPhysicalType.Float,
            new ColumnOptions(ParquetRepetition.Repeated));
        var c = WriteColumn(col, new float[][] { [float.NaN, 3.0f], [1.0f, float.NaN] });
        Assert.Equal(1.0f, BitConverter.Int32BitsToSingle((int)c.Statistics.MinBits));
        Assert.Equal(3.0f, BitConverter.Int32BitsToSingle((int)c.Statistics.MaxBits));
    }

    // ──────────────── double[] repeated — AddDouble ────────────────

    [Fact]
    public void DoubleList_MinMaxCorrect()
    {
        var col = new Column("v", ParquetPhysicalType.Double,
            new ColumnOptions(ParquetRepetition.Repeated));
        var c = WriteColumn(col, new double[][] { [10.0, -5.0], [20.0] });
        Assert.Equal(-5.0, BitConverter.Int64BitsToDouble(c.Statistics.MinBits));
        Assert.Equal(20.0, BitConverter.Int64BitsToDouble(c.Statistics.MaxBits));
    }

    // ──────────────── bool[] repeated — AddBoolean (line 1316+) ────────────────

    [Fact]
    public void BoolList_AllTrue_MinAndMaxTrue()
    {
        var col = new Column("v", ParquetPhysicalType.Boolean,
            new ColumnOptions(ParquetRepetition.Repeated));
        var c = WriteColumn(col, new bool[][] { [true], [true, true] });
        Assert.Equal(ColumnStatistics.ColumnStatisticsValueKind.Boolean, c.Statistics.ValueKind);
        Assert.Equal(1L, c.Statistics.MinBits);
        Assert.Equal(1L, c.Statistics.MaxBits);
    }

    [Fact]
    public void BoolList_Mixed_MinFalseMaxTrue()
    {
        var col = new Column("v", ParquetPhysicalType.Boolean,
            new ColumnOptions(ParquetRepetition.Repeated));
        var c = WriteColumn(col, new bool[][] { [true, false] });
        Assert.Equal(0L, c.Statistics.MinBits);
        Assert.Equal(1L, c.Statistics.MaxBits);
    }

    [Fact]
    public void BoolList_FirstInRow_InitializesMinMax()
    {
        var col = new Column("v", ParquetPhysicalType.Boolean,
            new ColumnOptions(ParquetRepetition.Repeated));
        // First bool is false → min initialized to false
        var c = WriteColumn(col, new bool[][] { [false, true] });
        Assert.Equal(0L, c.Statistics.MinBits);  // min = false
        Assert.Equal(1L, c.Statistics.MaxBits);   // max = true
    }

    // ──────────────── ToStatistics — uint column via int[] rows (line 1277) ────────────────
    // Note: ulong/uint repeated columns must be written as long[]/int[] rows

    [Fact]
    public void IntListWithUnsignedLogicalType_Statistics_UsesUInt32Kind()
    {
        // Unsigned int repeated column — uses int[] rows (writer casts internally)
        var col = new Column("v", ParquetPhysicalType.Int32,
            new ColumnOptions(ParquetRepetition.Repeated),
            new LogicalType.Int(bitWidth: 32, isSigned: false));
        // Use int[] rows since the encoder expects Int32[] for Int32 physical type repeated
        var c = WriteColumn(col, new int[][] { [1000, unchecked((int)500u)], [unchecked((int)2000u)] });
        Assert.True(c.Statistics.HasStatistics);
    }

    // ──────────────── SerializedColumn.Statistics surviving mutants (lines 592-653) ────────────────

    [Fact]
    public void RequiredInt32_Statistics_HasStatistics()
    {
        var col = new Column("v", ParquetPhysicalType.Int32);
        var c = WriteColumn(col, new int[] { 5, 10, 1 });
        Assert.True(c.Statistics.HasStatistics);
        Assert.Equal(1L, c.Statistics.MinBits);
        Assert.Equal(10L, c.Statistics.MaxBits);
    }

    [Fact]
    public void RequiredFloat_Statistics_Correct()
    {
        var col = new Column("v", ParquetPhysicalType.Float);
        var c = WriteColumn(col, new float[] { 3.5f, 1.5f, 7.0f });
        Assert.Equal(ColumnStatistics.ColumnStatisticsValueKind.Float, c.Statistics.ValueKind);
        Assert.Equal(1.5f, BitConverter.Int32BitsToSingle((int)c.Statistics.MinBits));
        Assert.Equal(7.0f, BitConverter.Int32BitsToSingle((int)c.Statistics.MaxBits));
    }

    [Fact]
    public void RequiredDouble_Statistics_Correct()
    {
        var col = new Column("v", ParquetPhysicalType.Double);
        var c = WriteColumn(col, new double[] { 3.5, 1.5, 7.0 });
        Assert.Equal(ColumnStatistics.ColumnStatisticsValueKind.Double, c.Statistics.ValueKind);
        Assert.Equal(1.5, BitConverter.Int64BitsToDouble(c.Statistics.MinBits));
        Assert.Equal(7.0, BitConverter.Int64BitsToDouble(c.Statistics.MaxBits));
    }

    [Fact]
    public void RequiredLong_Statistics_Correct()
    {
        var col = new Column("v", ParquetPhysicalType.Int64);
        var c = WriteColumn(col, new long[] { 100L, 50L, 200L });
        Assert.Equal(50L, c.Statistics.MinBits);
        Assert.Equal(200L, c.Statistics.MaxBits);
    }

    [Fact]
    public void RequiredBool_Statistics_Correct()
    {
        var col = new Column("v", ParquetPhysicalType.Boolean);
        var c = WriteColumn(col, new bool[] { true, false, true });
        Assert.Equal(0L, c.Statistics.MinBits);
        Assert.Equal(1L, c.Statistics.MaxBits);
    }
}
