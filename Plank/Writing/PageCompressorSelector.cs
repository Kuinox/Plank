using System.IO.Compression;
using K4os.Compression.LZ4;
using K4os.Compression.LZ4.Encoders;
using Snappier;
using ZstdSharp;

namespace Plank.Writing;

sealed class PageCompressorSelector : IDisposable
{
    readonly BrotliPageCompressor _brotli;
    readonly GzipPageCompressor _gzip;
    readonly SnappyPageCompressor _snappy;
    readonly Lz4PageCompressor _lz4;
    readonly ZstdPageCompressor _zstd;
    bool _disposed;

    internal PageCompressorSelector()
    {
        _brotli = new BrotliPageCompressor();
        _gzip = new GzipPageCompressor();
        _snappy = new SnappyPageCompressor();
        _lz4 = new Lz4PageCompressor();
        _zstd = new ZstdPageCompressor();
        _disposed = false;
    }

    internal IPageCompressor Select(CompressionKind compression)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return compression switch
        {
            CompressionKind.Brotli => _brotli,
            CompressionKind.Gzip => _gzip,
            CompressionKind.Snappy => _snappy,
            CompressionKind.Lz4 => _lz4,
            CompressionKind.Zstd => _zstd,
            CompressionKind.None => throw new InvalidOperationException("Compression 'None' does not use a page compressor."),
            _ => throw new NotSupportedException($"Compression '{compression}' is not supported yet.")
        };
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _brotli.Dispose();
        _gzip.Dispose();
        _snappy.Dispose();
        _lz4.Dispose();
        _zstd.Dispose();
        _disposed = true;
    }

    sealed class BrotliPageCompressor : IPageCompressor
    {
        public bool UsesStreamingOutput
            => false;

        public int Compress(ReadOnlySpan<byte> source, Span<byte> destination)
        {
            if (!BrotliEncoder.TryCompress(source, destination, out var written))
                throw new InvalidOperationException("Brotli compressed payload exceeds MaxCompressedBytes.");
            return written;
        }

        public int Compress(ReadOnlySpan<byte> source, GrowableBufferWriter destination)
            => throw new InvalidOperationException("Brotli compressor requires contiguous destination.");

        public void Dispose()
        {
        }
    }

    sealed class GzipPageCompressor : IPageCompressor
    {
        public bool UsesStreamingOutput
            => true;

        public int Compress(ReadOnlySpan<byte> source, Span<byte> destination)
            => throw new InvalidOperationException("Gzip compressor is configured for streaming output.");

        public int Compress(ReadOnlySpan<byte> source, GrowableBufferWriter destination)
        {
            using var output = new BufferWriterStream(destination);
            using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
                gzip.Write(source);
            return destination.WrittenCount;
        }

        public void Dispose()
        {
        }
    }

    sealed class SnappyPageCompressor : IPageCompressor
    {
        public bool UsesStreamingOutput
            => false;

        public int Compress(ReadOnlySpan<byte> source, Span<byte> destination)
        {
            var maxLength = Snappy.GetMaxCompressedLength(source.Length);
            if (maxLength > destination.Length)
                throw new InvalidOperationException("Snappy compressed payload exceeds MaxCompressedBytes.");

            var written = Snappy.Compress(source, destination);
            if (written > destination.Length)
                throw new InvalidOperationException("Snappy compressed payload exceeds MaxCompressedBytes.");
            return written;
        }

        public int Compress(ReadOnlySpan<byte> source, GrowableBufferWriter destination)
            => throw new InvalidOperationException("Snappy compressor requires contiguous destination.");

        public void Dispose()
        {
        }
    }

    sealed class Lz4PageCompressor : IPageCompressor
    {
        public bool UsesStreamingOutput
            => true;

        public int Compress(ReadOnlySpan<byte> source, Span<byte> destination)
            => throw new InvalidOperationException("Lz4 compressor is configured for streaming output.");

        public int Compress(ReadOnlySpan<byte> source, GrowableBufferWriter destination)
        {
            using var encoder = LZ4Encoder.Create(chaining: false, LZ4Level.L00_FAST, extraBlocks: 0, blockSize: 64 * 1024);
            var remaining = source;
            while (!remaining.IsEmpty)
            {
                var output = destination.GetSpan(GetLz4OutputHint(remaining.Length));
                var action = LZ4EncoderExtensions.TopupAndEncode(
                    encoder,
                    remaining,
                    output,
                    forceEncode: true,
                    allowCopy: true,
                    loaded: out var loaded,
                    encoded: out var encoded);
                destination.Advance(encoded);
                remaining = remaining[loaded..];
                if (action == EncoderAction.None && loaded == 0 && encoded == 0)
                    throw new InvalidOperationException("Lz4 streaming compression made no progress.");
            }

            while (true)
            {
                var output = destination.GetSpan(256);
                var action = LZ4EncoderExtensions.FlushAndEncode(encoder, output, allowCopy: true, encoded: out var encoded);
                destination.Advance(encoded);
                if (action == EncoderAction.None)
                    break;
            }

            return destination.WrittenCount;
        }

        static int GetLz4OutputHint(int inputLength)
        {
            var bounded = inputLength <= 64 * 1024 ? inputLength : 64 * 1024;
            var hint = checked(bounded + (bounded >> 4) + 128);
            return hint < 512 ? 512 : hint;
        }

        public void Dispose()
        {
        }
    }

    sealed class ZstdPageCompressor : IPageCompressor
    {
        public bool UsesStreamingOutput
            => true;

        public int Compress(ReadOnlySpan<byte> source, Span<byte> destination)
            => throw new InvalidOperationException("Zstd compressor is configured for streaming output.");

        public int Compress(ReadOnlySpan<byte> source, GrowableBufferWriter destination)
        {
            using var output = new BufferWriterStream(destination);
            using (var zstd = new CompressionStream(output, 1, 0, leaveOpen: true))
                zstd.Write(source);
            return destination.WrittenCount;
        }

        public void Dispose()
        {
        }
    }

    sealed class BufferWriterStream : Stream
    {
        readonly GrowableBufferWriter _destination;

        internal BufferWriterStream(GrowableBufferWriter destination)
            => _destination = destination;

        public override bool CanRead
            => false;

        public override bool CanSeek
            => false;

        public override bool CanWrite
            => true;

        public override long Length
            => _destination.WrittenCount;

        public override long Position
        {
            get => _destination.WrittenCount;
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
            => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin)
            => throw new NotSupportedException();

        public override void SetLength(long value)
            => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            var destination = _destination.GetSpan(count);
            buffer.AsSpan(offset, count).CopyTo(destination);
            _destination.Advance(count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            var destination = _destination.GetSpan(buffer.Length);
            buffer.CopyTo(destination);
            _destination.Advance(buffer.Length);
        }
    }
}
