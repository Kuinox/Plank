using System.Collections.Immutable;
using Plank.Schema;
using Plank.Writing;
using Plank.Writing.PageStrategy;

namespace Plank.StrykerTests;

public class DefaultStrategyTests
{
    static DefaultStrategy MakeStrategy(uint targetPageBytes = 128 * 1024,
        EncodingKind encoding = EncodingKind.Plain)
    {
        var col = new Column("v", ParquetPhysicalType.Int32,
            new ColumnOptions(encodings: [encoding]));
        return new DefaultStrategy(col, targetPageBytes);
    }

    static DefaultStrategy MakeDictStrategy(uint targetPageBytes = 128 * 1024)
    {
        var col = new Column("v", ParquetPhysicalType.Int32,
            new ColumnOptions(encodings: [EncodingKind.RleDictionary]));
        return new DefaultStrategy(col, targetPageBytes);
    }

    // ──────────────── DictionaryMode ────────────────

    [Test]
    public void Plain_DictionaryMode_IsDisabled()
    {
        var s = MakeStrategy(encoding: EncodingKind.Plain);
        ClassicAssert.AreEqual(DictionaryMode.Disabled, s.GetDictionaryMode());
    }

    [Test]
    public void RleDictionary_DictionaryMode_IsMaybe()
    {
        var s = MakeDictStrategy();
        ClassicAssert.AreEqual(DictionaryMode.Maybe, s.GetDictionaryMode());
    }

    [Test]
    public void PlainDictionary_DictionaryMode_IsMaybe()
    {
        var col = new Column("v", ParquetPhysicalType.Int32,
            new ColumnOptions(encodings: [EncodingKind.PlainDictionary]));
        var s = new DefaultStrategy(col, 128 * 1024);
        ClassicAssert.AreEqual(DictionaryMode.Maybe, s.GetDictionaryMode());
    }

    // ──────────────── SortOrder ────────────────

    [Test]
    public void InitialSortOrder_IsUnknown()
    {
        var s = MakeDictStrategy();
        ClassicAssert.AreEqual(DictionarySortOrder.Unknown, s.GetDictionarySortOrder());
    }

    [Test]
    public void SetSortOrder_Ascending_RoundTrips()
    {
        var s = MakeDictStrategy();
        s.SetDictionarySortOrder(DictionarySortOrder.Ascending);
        ClassicAssert.AreEqual(DictionarySortOrder.Ascending, s.GetDictionarySortOrder());
    }

    [Test]
    public void SetSortOrder_Unsorted_RoundTrips()
    {
        var s = MakeDictStrategy();
        s.SetDictionarySortOrder(DictionarySortOrder.Unsorted);
        ClassicAssert.AreEqual(DictionarySortOrder.Unsorted, s.GetDictionarySortOrder());
    }

    [Test]
    public void SetSortOrder_Descending_RoundTrips()
    {
        var s = MakeDictStrategy();
        s.SetDictionarySortOrder(DictionarySortOrder.Descending);
        ClassicAssert.AreEqual(DictionarySortOrder.Descending, s.GetDictionarySortOrder());
    }

    [Test]
    public void SetSortOrder_OutOfRange_Throws()
    {
        var s = MakeDictStrategy();
        Assert.Throws<ArgumentOutOfRangeException>(() => s.SetDictionarySortOrder((DictionarySortOrder)99));
    }

    // ──────────────── ShouldDropDictionary ────────────────

    [Test]
    public void ShouldDropDictionary_ZeroRowsSeen_ReturnsFalse()
    {
        var s = MakeDictStrategy();
        ClassicAssert.IsFalse(s.ShouldDropDictionary(100, 1000, rowsSeen: 0));
    }

    [Test]
    public void ShouldDropDictionary_ZeroTotalRows_ReturnsFalse()
    {
        var s = MakeDictStrategy();
        ClassicAssert.IsFalse(s.ShouldDropDictionary(100, totalRowCount: 0, rowsSeen: 1000));
    }

    [Test]
    public void ShouldDropDictionary_TooFewRowsSeen_ReturnsFalse()
    {
        // minRowsForDecision = max(16384, 100000/8) = max(16384, 12500) = 16384
        // rowsSeen = 1000 < 16384, and 1000 < 100000 → false
        var s = MakeDictStrategy();
        ClassicAssert.IsFalse(s.ShouldDropDictionary(500, totalRowCount: 100_000, rowsSeen: 1_000));
    }

    [Test]
    public void ShouldDropDictionary_AllUnique_ReturnsTrue()
    {
        // uniqueCount == rowsSeen: 100% unique → should drop
        var s = MakeDictStrategy();
        ClassicAssert.IsTrue(s.ShouldDropDictionary(uniqueCount: 20_000, totalRowCount: 100_000, rowsSeen: 20_000));
    }

    [Test]
    public void ShouldDropDictionary_OneUnique_ReturnsFalse()
    {
        // uniqueCount=1: no cardinality issue
        var s = MakeDictStrategy();
        ClassicAssert.IsFalse(s.ShouldDropDictionary(uniqueCount: 1, totalRowCount: 100_000, rowsSeen: 50_000));
    }

    [Test]
    public void ShouldDropDictionary_LowCardinality_ReturnsFalse()
    {
        // 100 unique / 50000 seen → 0.2% unique → don't drop
        var s = MakeDictStrategy();
        ClassicAssert.IsFalse(s.ShouldDropDictionary(uniqueCount: 100, totalRowCount: 100_000, rowsSeen: 50_000));
    }

    [Test]
    public void ShouldDropDictionary_NearlyAllUnique_ReturnsTrue()
    {
        // 49000 / 50000 = 98% unique → 49000*100 >= 50000*98 → drop
        var s = MakeDictStrategy();
        ClassicAssert.IsTrue(s.ShouldDropDictionary(uniqueCount: 49_000, totalRowCount: 100_000, rowsSeen: 50_000));
    }

    [Test]
    public void ShouldDropDictionary_HighProjectedCardinality_ReturnsTrue()
    {
        // 85% seen, projected stays high → should drop
        // rowsSeen=50000 uniqueCount=42500 → 42500/50000 = 85%
        // projected = min(100000, 42500*100000/50000) = min(100000, 85000) = 85000
        // 85000*4 >= 100000*3 → 340000 >= 300000 ✓
        // uniqueCount*100 >= rowsSeen*85 → 4250000 >= 4250000 ✓ → drop
        var s = MakeDictStrategy();
        ClassicAssert.IsTrue(s.ShouldDropDictionary(uniqueCount: 42_500, totalRowCount: 100_000, rowsSeen: 50_000));
    }

    [Test]
    public void ShouldDropDictionary_RowsSeenEqualsTotal_SkipsDecisionThresholdCheck()
    {
        // rowsSeen == totalRowCount → no early return from the threshold check
        // 50 unique in 100 rows → 50% unique
        // 50*100 >= 100*98 → 5000 >= 9800 → false (not nearly all unique)
        // projected = min(100, 50*100/100) = 50
        // 50*4 >= 100*3 → 200 >= 300 → false → don't drop
        var s = MakeDictStrategy();
        ClassicAssert.IsFalse(s.ShouldDropDictionary(uniqueCount: 50, totalRowCount: 100, rowsSeen: 100));
    }

    // ──────────────── TryGetTargetDataPageSizeBytes ────────────────

    [Test]
    public void TryGetTargetDataPageSizeBytes_ReturnsConfiguredValue()
    {
        var s = MakeStrategy(targetPageBytes: 65536);
        ClassicAssert.IsTrue(s.TryGetTargetDataPageSizeBytes(out var size));
        ClassicAssert.AreEqual(65536u, size);
    }

    [Test]
    public void ShouldStartNewDataPage_AlwaysReturnsFalse()
    {
        var s = MakeStrategy();
        ClassicAssert.IsFalse(s.ShouldStartNewDataPage(1000, 500, 500));
        ClassicAssert.IsFalse(s.ShouldStartNewDataPage(0, 0, 0));
        ClassicAssert.IsFalse(s.ShouldStartNewDataPage(1, 1, 1));
    }
}
