using Plank.Reading;
using Plank.Reading.Logical;
using Plank.Schema;

namespace Plank.Tests.Reading;

/// <summary>
/// A page can declare zero uncompressed bytes and still carry a compressed
/// payload, and every codec has to read it the same way.
/// </summary>
/// <remarks>
/// Arrow emits exactly that for an all-null column: an empty dictionary page
/// whose payload is the codec's own framing around no content at all — one byte
/// for LZ4_RAW, nine for the Hadoop LZ4 block format. Plank's writer never
/// produces an empty page, so nothing in the suite or the fuzz corpus had one.
/// Snappy, Gzip, Zstd and Brotli decoded those payloads back to zero bytes and
/// read the files; both LZ4 decoders returned -1 and rejected them.
/// </remarks>
internal sealed class EmptyPagePayloadTests
{
    [Test]
    [Arguments(CompressionKind.Snappy)]
    [Arguments(CompressionKind.Gzip)]
    [Arguments(CompressionKind.Zstd)]
    [Arguments(CompressionKind.Brotli)]
    [Arguments(CompressionKind.Lz4)]
    [Arguments(CompressionKind.Lz4Legacy)]
    public async Task DecompressingIntoAnEmptyDestinationProducesNothing(CompressionKind compression)
    {
        // Deliberately not a valid frame for any of them. Once the page says
        // there are no bytes to recover, what the codec wrapped around them is
        // not the reader's business — which is the invariant that makes the six
        // behave alike instead of four by luck of their framing.
        var decoded = ParquetDecompressor.Decompress([0x00, 0x01, 0x02], expectedLength: 0, compression);

        await Assert.That(decoded).IsEmpty();
    }

    [Test]
    [Arguments(ParquetSharp.Compression.Lz4)]
    [Arguments(ParquetSharp.Compression.Lz4Hadoop)]
    [Arguments(ParquetSharp.Compression.Snappy)]
    [Arguments(ParquetSharp.Compression.Zstd)]
    public async Task ReadsAnAllNullColumnWrittenByAnotherImplementation(ParquetSharp.Compression compression)
    {
        var bytes = CreateArrowAllNullFile(compression);
        using var reader = new ParquetReader();
        reader.Reset(new MemoryStream(bytes, writable: false));

        var rows = 0;
        var nulls = 0;
        foreach (var buffer in reader.RowGroups[0].Column<byte>(reader.Schema.LeafColumns[0]))
            for (var i = 0; i < buffer.Count; i++)
            {
                rows++;
                if (buffer.IsNull(i))
                    nulls++;
            }

        await Assert.That(rows).IsEqualTo(3);
        await Assert.That(nulls).IsEqualTo(3);
    }

    static byte[] CreateArrowAllNullFile(ParquetSharp.Compression compression)
    {
        using var stream = new MemoryStream();
        using var properties = new ParquetSharp.WriterPropertiesBuilder()
            .Compression(compression)
            .Build();
        using (var writer = new ParquetSharp.ParquetFileWriter(stream,
            [new ParquetSharp.Column<byte[]?>("value")], null, properties, null, leaveOpen: true))
        {
            using (var rowGroup = writer.AppendRowGroup())
            using (var column = rowGroup.NextColumn())
                column.LogicalWriter<byte[]?>().WriteBatch([null, null, null]);
            writer.Close();
        }

        return stream.ToArray();
    }
}
