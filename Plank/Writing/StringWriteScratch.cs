using System.Buffers;
using System.Runtime.CompilerServices;
using System.Text;
using Plank.Schema;
using Plank.Writing.Encoding;

namespace Plank.Writing;

// UTF-8 payloads live in pooled chunks until the encoder has copied pages and statistics.
internal sealed class StringWriteScratch
{
    static readonly UTF8Encoding StrictUtf8 = new(false, true);
    readonly List<PooledByteMemory> _chunks = [];
    int _activeChunks;
    ReadOnlyMemory<byte>[] _required = [];
    ReadOnlyMemory<byte>?[] _optional = [];

    internal ReadOnlySpan<ReadOnlyMemory<byte>> EncodeRequired(ReadOnlySpan<string> values, Column column,
        IParquetBufferPool pool)
    {
        var rows = GetRows(ref _required, values.Length);
        var encoder = new ChunkEncoder(this, pool, values.Length);
        for (var i = 0; i < values.Length; i++)
        {
            var value = values[i];
            if (value is null)
                ByteArrayRows.ThrowNullValue(column);
            rows[i] = encoder.Encode(value!);
        }
        return rows;
    }

    internal ReadOnlySpan<ReadOnlyMemory<byte>?> EncodeOptional(ReadOnlySpan<string> values, IParquetBufferPool pool)
    {
        var rows = GetRows(ref _optional, values.Length);
        var encoder = new ChunkEncoder(this, pool, values.Length);
        for (var i = 0; i < values.Length; i++)
            rows[i] = values[i] is { } value ? encoder.Encode(value) : (ReadOnlyMemory<byte>?)null;
        return rows;
    }

    static Span<T> GetRows<T>(ref T[] rows, int length)
    {
        if (length > rows.Length)
            rows = new T[Math.Max(length, (int)Math.Min(2L * rows.Length, Array.MaxLength))];
        return rows.AsSpan(0, length);
    }

    ref struct ChunkEncoder(StringWriteScratch scratch, IParquetBufferPool pool, int rowCount)
    {
        Memory<byte> _chunk;
        Span<byte> _remaining;
        int _offset;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal ReadOnlyMemory<byte> Encode(string value)
        {
            // Up to three UTF-8 bytes per UTF-16 code unit. Most rows fit without a byte-count pass.
            if ((long)value.Length * 3 > _remaining.Length)
            {
                var needed = StrictUtf8.GetByteCount(value);
                if (needed > _remaining.Length)
                {
                    var chunkSize = (int)Math.Clamp((long)rowCount * 16, 256, 64 * 1024);
                    _chunk = scratch.RentChunk(pool, Math.Max(chunkSize, needed));
                    _remaining = _chunk.Span;
                    _offset = 0;
                }
            }
            var status = Ascii.FromUtf16(value, _remaining, out var written);
            if (status != OperationStatus.Done)
                written += StrictUtf8.GetBytes(value.AsSpan(written), _remaining[written..]);
            var payload = _chunk.Slice(_offset, written);
            _remaining = _remaining[written..];
            _offset += written;
            return payload;
        }
    }

    Memory<byte> RentChunk(IParquetBufferPool pool, int size)
    {
        if (_activeChunks == _chunks.Count)
            _chunks.Add(new PooledByteMemory());
        return _chunks[_activeChunks++].SetBuffer(pool.Rent((uint)size));
    }

    internal void Reset()
    {
        for (var i = 0; i < _activeChunks; i++)
            _chunks[i].Reset();
        _activeChunks = 0;
    }
}
