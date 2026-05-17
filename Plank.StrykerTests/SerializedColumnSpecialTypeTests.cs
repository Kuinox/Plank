using Plank.Reading;
using Plank.Schema;
using Plank.Writing;

namespace Plank.StrykerTests;

/// <summary>
/// Tests specialized serialization paths in SerializedColumn for DateOnly,
/// DateTime, TimeOnly, byte, ushort, uint, ulong etc. (NoCoverage areas).
/// </summary>
public class SerializedColumnSpecialTypeTests
{
    static byte[] WriteAndClose<T>(ParquetSchema schema, T[] values,
        CompressionKind compression = CompressionKind.None)
    {
        using var ms = new MemoryStream();
        var writer = schema.CreateWriter(ms, new ParquetWriterOptions { Compression = compression });
        var col = writer.CreateSerializedColumn<T>(schema.Columns[0]);
        col.Serialize(values);
        writer.StartRowGroup().Write(col);
        writer.CloseFile();
        return ms.ToArray();
    }

    static T[] ReadAll<T>(byte[] data, ParquetSchema schema)
    {
        var src = new MemoryReadSource(data);
        using var reader = schema.CreateReader(src);
        var results = new List<T>();
        foreach (var tok in reader.EnumerateRowGroups())
        {
            using var rg = reader.OpenRowGroup(src, tok);
            foreach (var page in rg.Column<T>(schema.Columns[0]).Pages)
                foreach (var v in page.Values.Span)
                    results.Add(v);
        }
        return results.ToArray();
    }

    static ParquetSchema Int32Schema(LogicalType? logicalType = null)
        => new([new Column("v", ParquetPhysicalType.Int32, null, logicalType)]);

    static ParquetSchema OptionalInt32Schema()
        => new([new Column("v", ParquetPhysicalType.Int32, new ColumnOptions(ParquetRepetition.Optional))]);

    static ParquetSchema Int64Schema(LogicalType? logicalType = null)
        => new([new Column("v", ParquetPhysicalType.Int64, null, logicalType)]);

    // ──────────────── byte → Int32 ────────────────

    [Test]
    public void Byte_Serialize_RoundTripAsInt32()
    {
        var schema = Int32Schema();
        var values = new byte[] { 0, 1, 128, 255, 42 };
        var data = WriteAndClose<byte>(schema, values);
        var result = ReadAll<int>(data, schema);
        ClassicAssert.AreEqual(values.Select(b => (int)b).ToArray(), result);
    }

    [Test]
    public void NullableByte_Serialize_RoundTripAsNullableInt32()
    {
        var schema = OptionalInt32Schema();
        var values = new byte?[] { 1, null, 200, null, 5 };
        var data = WriteAndClose<byte?>(schema, values);
        var result = ReadAll<int?>(data, schema);
        ClassicAssert.AreEqual(values.Select(b => b is { } v ? (int?)v : null).ToArray(), result);
    }

    // ──────────────── ushort → Int32 ────────────────

    [Test]
    public void UShort_Serialize_RoundTripAsInt32()
    {
        var schema = Int32Schema();
        var values = new ushort[] { 0, 1, 1000, 65535 };
        var data = WriteAndClose<ushort>(schema, values);
        var result = ReadAll<int>(data, schema);
        ClassicAssert.AreEqual(values.Select(v => (int)v).ToArray(), result);
    }

    [Test]
    public void NullableUShort_Serialize_RoundTrip()
    {
        var schema = OptionalInt32Schema();
        var values = new ushort?[] { 100, null, 500 };
        var data = WriteAndClose<ushort?>(schema, values);
        var result = ReadAll<int?>(data, schema);
        Assert.That(result, Is.EqualTo(new int?[] {100, null, 500}));
    }

    // ──────────────── uint → Int32 (unsigned) ────────────────

    [Test]
    public void UInt32_Serialize_RoundTrip()
    {
        var schema = new ParquetSchema([new Column("v", ParquetPhysicalType.Int32, null,
            new LogicalType.Int(bitWidth: 32, isSigned: false))]);
        var values = new uint[] { 0, 1, uint.MaxValue, 1000000 };
        var data = WriteAndClose<uint>(schema, values);
        // Reads back as int (bitwise same)
        var result = ReadAll<int>(data, schema);
        ClassicAssert.AreEqual(values.Length, result.Length);
        for (var i = 0; i < values.Length; i++)
            ClassicAssert.AreEqual(unchecked((int)values[i]), result[i]);
    }

    // ──────────────── ulong → Int64 (unsigned) ────────────────

    [Test]
    public void UInt64_Serialize_RoundTrip()
    {
        var schema = new ParquetSchema([new Column("v", ParquetPhysicalType.Int64, null,
            new LogicalType.Int(bitWidth: 64, isSigned: false))]);
        var values = new ulong[] { 0, 1, ulong.MaxValue / 2, 1_000_000_000L };
        var data = WriteAndClose<ulong>(schema, values);
        var result = ReadAll<long>(data, schema);
        ClassicAssert.AreEqual(values.Length, result.Length);
        for (var i = 0; i < values.Length; i++)
            ClassicAssert.AreEqual(unchecked((long)values[i]), result[i]);
    }

    // ──────────────── DateOnly → Int32 ────────────────

    [Test]
    public void DateOnly_Serialize_StoredAsDaysSinceEpoch()
    {
        var schema = new ParquetSchema([new Column("v", ParquetPhysicalType.Int32, null,
            new LogicalType.Date())]);
        var epoch = new DateOnly(1970, 1, 1);
        var values = new DateOnly[]
        {
            epoch,
            new DateOnly(2024, 1, 1),
            new DateOnly(2000, 6, 15)
        };
        var data = WriteAndClose<DateOnly>(schema, values);
        var result = ReadAll<int>(data, schema);
        // Each DateOnly should be stored as day offset from Unix epoch
        ClassicAssert.AreEqual(0, result[0]);
        ClassicAssert.AreEqual(values[1].DayNumber - epoch.DayNumber, result[1]);
        ClassicAssert.AreEqual(values[2].DayNumber - epoch.DayNumber, result[2]);
    }

    [Test]
    public void NullableDateOnly_Serialize_RoundTrip()
    {
        var schema = new ParquetSchema([new Column("v", ParquetPhysicalType.Int32,
            new ColumnOptions(ParquetRepetition.Optional), new LogicalType.Date())]);
        var values = new DateOnly?[] { new DateOnly(2024, 1, 1), null, new DateOnly(2000, 1, 1) };
        var data = WriteAndClose<DateOnly?>(schema, values);
        var result = ReadAll<int?>(data, schema);
        ClassicAssert.AreEqual(3, result.Length);
        ClassicAssert.IsNotNull(result[0]);
        ClassicAssert.IsNull(result[1]);
        ClassicAssert.IsNotNull(result[2]);
    }

    // ──────────────── DateTime → Int64 (timestamp micros) ────────────────

    [Test]
    public void DateTime_Serialize_StoredAsMicros()
    {
        var schema = new ParquetSchema([new Column("v", ParquetPhysicalType.Int64, null,
            new LogicalType.Timestamp(TimeUnit.Micros, IsAdjustedToUtc: true))]);
        var epoch = DateTime.UnixEpoch;
        var ts = new DateTime(2024, 3, 15, 12, 0, 0, DateTimeKind.Utc);
        var data = WriteAndClose<DateTime>(schema, [epoch, ts]);
        var result = ReadAll<long>(data, schema);
        ClassicAssert.AreEqual(0L, result[0]); // epoch → 0 micros
        var expectedMicros = (ts.Ticks - epoch.Ticks) / 10;
        ClassicAssert.AreEqual(expectedMicros, result[1]);
    }

    [Test]
    public void NullableDateTime_Serialize_NullsPreserved()
    {
        var schema = new ParquetSchema([new Column("v", ParquetPhysicalType.Int64,
            new ColumnOptions(ParquetRepetition.Optional),
            new LogicalType.Timestamp(TimeUnit.Micros, IsAdjustedToUtc: true))]);
        var ts = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var data = WriteAndClose<DateTime?>(schema, [ts, null, ts]);
        var result = ReadAll<long?>(data, schema);
        ClassicAssert.IsNotNull(result[0]);
        ClassicAssert.IsNull(result[1]);
        ClassicAssert.IsNotNull(result[2]);
    }

    // ──────────────── TimeOnly → Int64 (time micros) ────────────────

    [Test]
    public void TimeOnly_Serialize_StoredAsMicros()
    {
        var schema = new ParquetSchema([new Column("v", ParquetPhysicalType.Int64, null,
            new LogicalType.Time(TimeUnit.Micros, IsAdjustedToUtc: true))]);
        var midnight = TimeOnly.MinValue;
        var noon = new TimeOnly(12, 0, 0);
        var data = WriteAndClose<TimeOnly>(schema, [midnight, noon]);
        var result = ReadAll<long>(data, schema);
        ClassicAssert.AreEqual(0L, result[0]); // midnight → 0 micros
        var expectedMicros = noon.Ticks / 10L;
        ClassicAssert.AreEqual(expectedMicros, result[1]);
    }

    // ──────────────── HasPendingData / double Serialize guard ────────────────

    [Test]
    public void Serialize_Twice_WithoutWrite_Throws()
    {
        var schema = Int32Schema();
        using var ms = new MemoryStream();
        var writer = schema.CreateWriter(ms);
        var col = writer.CreateSerializedColumn<int>(schema.Columns[0]);
        col.Serialize([1, 2, 3]);
        Assert.Throws<InvalidOperationException>(() => col.Serialize([4, 5]));
    }

    // ──────────────── Serialize array overload ────────────────

    [Test]
    public void Serialize_ArrayOverload_SameAsSpan()
    {
        var schema = Int32Schema();
        var values = new int[] { 10, 20, 30 };
        using var ms = new MemoryStream();
        var writer = schema.CreateWriter(ms, new ParquetWriterOptions { Compression = CompressionKind.None });
        var col = writer.CreateSerializedColumn<int>(schema.Columns[0]);
        col.Serialize(values); // T[] overload
        writer.StartRowGroup().Write(col);
        writer.CloseFile();
        ClassicAssert.AreEqual(values, ReadAll<int>(ms.ToArray(), schema));
    }

    // ──────────────── WritePageIndexes path ────────────────

    [Test]
    public void Int32_WithPageIndexes_StatisticsAssigned()
    {
        var schema = Int32Schema();
        var values = new int[] { 5, 3, 9, 1, 7 };
        using var ms = new MemoryStream();
        var writer = schema.CreateWriter(ms, new ParquetWriterOptions
        {
            Compression = CompressionKind.None,
            WritePageIndexes = true
        });
        var col = writer.CreateSerializedColumn<int>(schema.Columns[0]);
        col.Serialize(values);
        writer.StartRowGroup().Write(col);
        writer.CloseFile();
        ClassicAssert.AreEqual(values, ReadAll<int>(ms.ToArray(), schema));
    }
}
