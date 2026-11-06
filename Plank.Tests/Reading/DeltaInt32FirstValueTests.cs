using Plank.Reading;
using Plank.Reading.Logical;
using Plank.Reading.Logical.Internal;

namespace Plank.Tests.Reading;

internal sealed class DeltaInt32FirstValueTests
{
    // Required INT32, five rows, DataPage V1. The first value is a six-byte zigzag VLQ.
    const string Sample = "UEFSMRUAFZwCFZwCLBUKFQoVBhUGHBgE////fxgE/////xYAKAT///9/GAT/////EREAAACAAQQF9f7///8PIAAAAAEAAID+//9/AAAAAEHiAQAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAGRECGRgE/////xkYBP///38VABkWAAAZHBYIFf4CFgAAABUCGSxIBnNjaGVtYRUCABUCJQAYAWMAFgoZHBkcJgAcFQIZFQoZGAFjFQAWChb+Ahb+AiYIPBgE////fxgE/////xYAKAT///9/GAT/////EREAABa0AxUWFoYDFS4AFv4CFgomCBb+AgA5HBwAAABwAAAAUEFSMQ==";

    [Test]
    public void RejectsEmbeddedSample()
    {
        Assert.Throws<CorruptParquetException>(() => Read(Convert.FromBase64String(Sample), 0));
    }

    [Test]
    public void FirstValueWidthIsEnforcedThroughPublicReaders()
    {
        foreach (var kind in Enumerable.Range(0, 3))
        foreach (var first in new[] { int.MinValue, int.MaxValue })
        {
            var file = MakeFile(kind, first);
            var values = Read(file, kind);
            if (!values.SequenceEqual(Enumerable.Repeat(first, 5)))
                throw new InvalidOperationException($"Incorrect extreme values for reader {kind}: {first}.");

            // Both extremes have a five-byte first value. Replace it in place, leaving
            // page sizes and metadata unchanged. The following minimum delta is zero.
            byte[] marker = first == int.MinValue ? [0xff, 0xff, 0xff, 0xff, 0x0f] : [0xfe, 0xff, 0xff, 0xff, 0x0f];
            var offset = file.AsSpan().IndexOf(marker);
            if (offset < 0 || file[offset + 5] != 0)
                throw new InvalidOperationException("Expected delta payload not found.");
            var overflow = (byte[])file.Clone();
            overflow[offset + 4] = 0x10; // five bytes, encoded unsigned value > uint.MaxValue
            Assert.Throws<CorruptParquetException>(() => Read(overflow, kind));
            var sixBytes = (byte[])file.Clone();
            sixBytes[offset + 4] = 0x8f; // consumes the zero minimum delta as byte six
            Assert.Throws<CorruptParquetException>(() => Read(sixBytes, kind));
        }
    }

    [Test]
    public void AllInt32DecoderPathsRejectOutOfRangeFirstValue()
    {
        foreach (byte[] first in new byte[][] { [0x80, 0x80, 0x80, 0x80, 0x10], [0x80, 0x80, 0x80, 0x80, 0x80, 0] })
        {
            byte[] payload = [0x80, 1, 4, 1, .. first];
            Assert.Throws<CorruptParquetException>(() => DeltaBinaryPackedDecoder.ReadInt32(payload));
            Assert.Throws<CorruptParquetException>(() => DeltaBinaryPackedDecoder.ReadInt32(payload, new int[1]));
            Assert.Throws<CorruptParquetException>(() => DeltaBinaryPackedDecoder.ReadNullableInt32(payload, new int?[1], true));
            Assert.Throws<CorruptParquetException>(() => DeltaBinaryPackedDecoder.ReadNarrowInt32<byte>(payload, new byte[1]));
            Assert.Throws<CorruptParquetException>(() => DeltaBinaryPackedDecoder.StartBatch(payload, 1, isInt64: false));
            Assert.Throws<CorruptParquetException>(() => DeltaBinaryPackedDecoder.GetEncodedLength(payload, 1));
            // The same VLQ is valid for an INT64 first value.
            _ = DeltaBinaryPackedDecoder.ReadInt64(payload);
            _ = DeltaBinaryPackedDecoder.StartBatch(payload, 1, isInt64: true);

            payload[3] = 0; // zero-value streams still consume the first-value field
            Assert.Throws<CorruptParquetException>(() => DeltaBinaryPackedDecoder.ReadInt32(payload));
            Assert.Throws<CorruptParquetException>(() => DeltaBinaryPackedDecoder.ReadInt32(payload, Span<int>.Empty));
            Assert.Throws<CorruptParquetException>(() => DeltaBinaryPackedDecoder.ReadNullableInt32(payload, Span<int?>.Empty, true));
            Assert.Throws<CorruptParquetException>(() => DeltaBinaryPackedDecoder.ReadNarrowInt32<byte>(payload, Span<byte>.Empty));
            Assert.Throws<CorruptParquetException>(() => DeltaBinaryPackedDecoder.StartBatch(payload, 0, isInt64: false));
            Assert.Throws<CorruptParquetException>(() => DeltaBinaryPackedDecoder.GetEncodedLength(payload, 0));
        }
    }

    static byte[] MakeFile(int kind, int value)
    {
        using var stream = new MemoryStream();
        using var builder = new ParquetSharp.WriterPropertiesBuilder();
        using var properties = builder
            .Compression(ParquetSharp.Compression.Uncompressed).DisableDictionary()
            .Encoding(ParquetSharp.Encoding.DeltaBinaryPacked).Build();
        ParquetSharp.Column column = kind switch
        {
            0 => new ParquetSharp.Column<int>("value"),
            1 => new ParquetSharp.Column<int?>("value"),
            _ => new ParquetSharp.Column<int[]>("value")
        };
        using (var writer = new ParquetSharp.ParquetFileWriter(stream, [column], null, properties, null, true))
        {
            using (var group = writer.AppendRowGroup())
            using (var physical = group.NextColumn())
            {
                if (kind == 0) physical.LogicalWriter<int>().WriteBatch(Enumerable.Repeat(value, 5).ToArray());
                else if (kind == 1) physical.LogicalWriter<int?>().WriteBatch(Enumerable.Repeat<int?>(value, 5).ToArray());
                else physical.LogicalWriter<int[]>().WriteBatch(Enumerable.Range(0, 5).Select(_ => new[] { value }).ToArray());
            }
            writer.Close();
        }
        return stream.ToArray();
    }

    static int[] Read(byte[] file, int kind)
    {
        using var reader = new ParquetReader();
        reader.Reset(new MemoryStream(file));
        var values = new List<int>();
        if (kind == 0)
            foreach (var buffer in reader.RowGroups[0].Column<int>(0)) values.AddRange(buffer.Values.ToArray());
        else if (kind == 1)
            foreach (var buffer in reader.RowGroups[0].Column<int?>(0))
                foreach (var value in buffer.Values) values.Add(value!.Value);
        else
            foreach (var buffer in reader.RowGroups[0].NestedColumn<int>(0)) values.AddRange(buffer.Values.Values.ToArray());
        return values.ToArray();
    }
}
