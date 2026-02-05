using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Plank.Schema;

namespace Plank.Writing;

static class ColumnCodec
{
    internal static void Encode<T>(Column column, ReadOnlySpan<T> values, ParquetPhysicalType physicalType, RowGroupOptions options, ref ParquetWriter.RowGroupState.ColumnState state)
    {
        var encoding = ResolveDefaultEncoding(column.Options.Encodings);
        if (encoding != EncodingKind.Plain)
            throw new NotSupportedException($"Encoding '{encoding}' is not supported for column '{column.Name}'.");

        state.Encoding = encoding;
        switch (physicalType)
        {
            case ParquetPhysicalType.Int32:
                if (typeof(T) != typeof(int))
                    throw new InvalidOperationException($"Column '{column.Name}' expects Int32 values.");

                EncodePlainInt32(Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<int>>(ref values), ref state, column.Name, options.MaxEncodedBytes);
                break;
            default:
                throw new NotSupportedException($"Physical type '{physicalType}' is not supported.");
        }
    }

    internal static void Compress(ref ParquetWriter.RowGroupState.ColumnState state)
        => state.Compression = CompressionKind.None;

    internal static bool TryGetFixedWidthBytes(ParquetPhysicalType physicalType, out int width)
    {
        switch (physicalType)
        {
            case ParquetPhysicalType.Boolean:
                width = 1;
                return true;
            case ParquetPhysicalType.Int32:
                width = 4;
                return true;
            case ParquetPhysicalType.Int64:
                width = 8;
                return true;
            case ParquetPhysicalType.Float:
                width = 4;
                return true;
            case ParquetPhysicalType.Double:
                width = 8;
                return true;
            default:
                width = 0;
                return false;
        }
    }

    static EncodingKind ResolveDefaultEncoding(ImmutableArray<EncodingKind> encodings)
        => encodings.IsDefaultOrEmpty ? EncodingKind.Plain : encodings[0];

    static void EncodePlainInt32(ReadOnlySpan<int> values, ref ParquetWriter.RowGroupState.ColumnState state, string columnName, int maxEncodedBytes)
    {
        var byteCount = checked(values.Length * sizeof(int));
        var buffer = state.EncodedBuffer;
        if (buffer is null || buffer.Length < byteCount)
            throw new InvalidOperationException($"Column '{columnName}' requires {byteCount} bytes but MaxEncodedBytes is {maxEncodedBytes}.");

        var destination = buffer.AsSpan(0, byteCount);
        if (BitConverter.IsLittleEndian)
            MemoryMarshal.AsBytes(values).CopyTo(destination);
        else
        {
            for (var i = 0; i < values.Length; i++)
                BinaryPrimitives.WriteInt32LittleEndian(destination.Slice(i * 4, 4), values[i]);
        }

        state.EncodedLength = byteCount;
        state.UncompressedLength = byteCount;
    }
}
