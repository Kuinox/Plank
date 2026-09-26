using System.Buffers.Binary;
using Plank.Reading;
using Plank.Reading.Logical.Internal;
using Plank.Schema;

namespace Plank.Tests.Reading;

internal sealed class EncodingVarintValidationTests
{
    [Test]
    public void RleLevelHeaderRejectsBitsBeyondUInt32()
    {
        var schema = new ParquetSchema([
            ColumnDefinition.OptionalLeaf("value", ParquetPhysicalType.Int32)
        ]);
        // The fifth byte's high bits used to wrap, turning this into header 2
        // (a one-value RLE run) and allowing the page to decode normally.
        byte[] payload = [0x82, 0x80, 0x80, 0x80, 0x10, 0x01, 0, 0, 0, 0];
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(6), 42);
        var header = new PageHeader(PageHeaderType.DataPageV2, (uint)payload.Length,
            (uint)payload.Length, 1, EncodingKind.Plain, HeaderLength: 1,
            RepetitionLevelsByteLength: 0, DefinitionLevelsByteLength: 6, NullCount: 0,
            IsCompressed: false, RepetitionLevelEncoding: EncodingKind.Rle,
            DefinitionLevelEncoding: EncodingKind.Rle, RowCount: 1);
        var buffers = default(ColumnReadBuffers<int>);
        try
        {
            Assert.Throws<CorruptParquetException>(() =>
                ColumnChunkReader.TryDecodeNestedPageIntoNative(header, payload, schema.LeafColumns[0],
                    ref buffers, DefaultParquetBufferPool.Shared, out _));
        }
        finally
        {
            buffers.Dispose();
        }
    }

    [Test]
    public void DeltaVarintRejectsTenthByteBeyondUInt64()
    {
        byte[] payload = [0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x02];
        var reader = new DeltaBinaryPackedReader(payload);
        try
        {
            _ = reader.ReadUnsignedVarInt();
        }
        catch (CorruptParquetException)
        {
            return;
        }
        throw new InvalidOperationException("An overflowing delta varint decoded as a value.");
    }

    [Test]
    public void DeltaVarintStillAcceptsMaximumUInt64()
    {
        byte[] payload = [0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0x01];
        var reader = new DeltaBinaryPackedReader(payload);
        if (reader.ReadUnsignedVarInt() != ulong.MaxValue)
            throw new InvalidOperationException("The maximum UInt64 varint changed value.");
    }
}
