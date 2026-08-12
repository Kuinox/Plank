using System.Buffers.Binary;
using System.Collections.Immutable;
using Plank.Reading;
using Plank.Reading.Logical.Internal;
using Plank.Schema;
using Plank.Writing;

namespace Plank.Tests.Reading;

internal sealed class RequiredPlainTimestampDecodingTests
{
    [Test]
    public void RawValuesPreserveUnitsKindsAndBoundariesAcrossPageVersions()
    {
        PageHeaderType[] pageTypes = [PageHeaderType.DataPage, PageHeaderType.DataPageV2];
        foreach (var pageType in pageTypes)
        foreach (var unit in Enum.GetValues<TimeUnit>())
        foreach (var adjustedToUtc in new[] { false, true })
        {
            long[] rawValues = unit switch
            {
                TimeUnit.Millis => [-62_135_596_800_000L, -1, 0, 1, 253_402_300_799_999L],
                TimeUnit.Micros => [-62_135_596_800_000_000L, -1, 0, 1, 253_402_300_799_999_999L],
                TimeUnit.Nanos => [long.MinValue, -100L, -1, 0, 1, 100, long.MaxValue],
                _ => throw new ArgumentOutOfRangeException(nameof(unit), unit,
                    "Time unit must be a defined TimeUnit value.")
            };
            var actual = Decode(rawValues, unit, adjustedToUtc, pageType);
            var kind = adjustedToUtc ? DateTimeKind.Utc : DateTimeKind.Unspecified;

            for (var i = 0; i < rawValues.Length; i++)
            {
                var scaledTicks = unit switch
                {
                    TimeUnit.Millis => checked(rawValues[i] * TimeSpan.TicksPerMillisecond),
                    TimeUnit.Micros => checked(rawValues[i] * 10),
                    TimeUnit.Nanos => rawValues[i] / 100,
                    _ => throw new ArgumentOutOfRangeException(nameof(unit), unit,
                        "Time unit must be a defined TimeUnit value.")
                };
                var expected = new DateTime(checked(DateTime.UnixEpoch.Ticks + scaledTicks), kind);
                if (actual[i].Ticks != expected.Ticks || actual[i].Kind != expected.Kind)
                    throw new InvalidOperationException(
                        $"{pageType}/{unit}/adjusted={adjustedToUtc}: raw value {rawValues[i]} " +
                        $"decoded as ticks={actual[i].Ticks}, kind={actual[i].Kind}; expected " +
                        $"ticks={expected.Ticks}, kind={expected.Kind}.");
            }
        }
    }

    [Test]
    public void OutOfRangeRawValuesAreRejectedAcrossPageVersionsAndKinds()
    {
        PageHeaderType[] pageTypes = [PageHeaderType.DataPage, PageHeaderType.DataPageV2];
        foreach (var pageType in pageTypes)
        foreach (var adjustedToUtc in new[] { false, true })
        {
            AssertRejected([-62_135_596_800_001], TimeUnit.Millis, adjustedToUtc, pageType);
            AssertRejected([253_402_300_800_000], TimeUnit.Millis, adjustedToUtc, pageType);
            AssertRejected([-62_135_596_800_000_001], TimeUnit.Micros, adjustedToUtc, pageType);
            AssertRejected([253_402_300_800_000_000], TimeUnit.Micros, adjustedToUtc, pageType);
        }
    }

    [Test]
    public void PayloadShorterThanDeclaredValueCountIsRejectedAcrossPageVersions()
    {
        PageHeaderType[] pageTypes = [PageHeaderType.DataPage, PageHeaderType.DataPageV2];
        foreach (var pageType in pageTypes)
        {
            var payload = new byte[sizeof(long)];
            var schema = CreateSchema(TimeUnit.Micros, adjustedToUtc: true);
            var header = CreateHeader(pageType, payload.Length, valueCount: 2);
            var buffers = default(ColumnReadBuffers<DateTime>);
            try
            {
                Assert.Throws<CorruptParquetException>(() =>
                    ColumnChunkReader.TryDecodeRequiredPageIntoNative(
                        header, payload, schema.LeafColumns[0].Column, rowCount: 2, ref buffers,
                        DefaultParquetBufferPool.Shared, out _));
            }
            finally
            {
                buffers.Dispose();
            }
        }
    }

    static DateTime[] Decode(long[] rawValues, TimeUnit unit, bool adjustedToUtc, PageHeaderType pageType)
    {
        var payload = new byte[checked(rawValues.Length * sizeof(long))];
        for (var i = 0; i < rawValues.Length; i++)
            BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(i * sizeof(long)), rawValues[i]);

        var schema = CreateSchema(unit, adjustedToUtc);
        var header = CreateHeader(pageType, payload.Length, checked((uint)rawValues.Length));
        var buffers = default(ColumnReadBuffers<DateTime>);
        try
        {
            if (!ColumnChunkReader.TryDecodeRequiredPageIntoNative(
                    header, payload, schema.LeafColumns[0].Column, checked((uint)rawValues.Length), ref buffers,
                    DefaultParquetBufferPool.Shared, out var values))
                throw new InvalidOperationException("Required Plain timestamp fast path was not used.");
            return values.Values.ToArray();
        }
        finally
        {
            buffers.Dispose();
        }
    }

    static ParquetSchema CreateSchema(TimeUnit unit, bool adjustedToUtc)
        => new([
            ColumnDefinition.RequiredLeaf("value", ParquetPhysicalType.Int64,
                new ColumnOptions(encodings: ImmutableArray.Create(EncodingKind.Plain)),
                new LogicalType.Timestamp(unit, adjustedToUtc))
        ]);

    static PageHeader CreateHeader(PageHeaderType pageType, int payloadLength, uint valueCount)
        => new(pageType, checked((uint)payloadLength), checked((uint)payloadLength), valueCount,
            EncodingKind.Plain, HeaderLength: 1, RepetitionLevelsByteLength: 0,
            DefinitionLevelsByteLength: 0, NullCount: 0, IsCompressed: false,
            RepetitionLevelEncoding: EncodingKind.Rle, DefinitionLevelEncoding: EncodingKind.Rle,
            RowCount: valueCount);

    static void AssertRejected(long[] rawValues, TimeUnit unit, bool adjustedToUtc, PageHeaderType pageType)
    {
        try
        {
            Decode(rawValues, unit, adjustedToUtc, pageType);
        }
        catch (Exception exception) when (exception is ArgumentOutOfRangeException or OverflowException)
        {
            return;
        }

        throw new InvalidOperationException(
            $"{pageType}/{unit}/adjusted={adjustedToUtc}: out-of-range raw timestamp was accepted.");
    }
}
