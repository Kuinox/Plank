using System.Buffers;
using K4os.Compression.LZ4;
using Plank.Schema;
using Snappier;

namespace Plank.Writing;

public sealed partial class ParquetWriter
{
    internal sealed partial class RowGroupState
    {
        sealed class RowGroupBufferCatalog
        {
            const string EncodedBucketName = "column";
            const string CompressedBucketName = "column-compressed";
            readonly IBufferPool _pool;
            readonly int _encodedBucketLength;
            readonly int _compressedBucketLength;
            long _requestIdBase;

            internal RowGroupBufferCatalog(Column[] columns, RowGroupOptions options, uint? rowGroupRowCountHint, CompressionKind compressionKind, IBufferPool pool)
            {
                _pool = pool;
                _encodedBucketLength = GetMaxEncodedLength(columns, rowGroupRowCountHint);
                _compressedBucketLength = GetCompressedBufferLength(_encodedBucketLength, options.MaxCompressedBytes, compressionKind);
                _requestIdBase = 0;
                RegisterBuckets(columns.Length);
            }

            internal void ConfigureRequestIds(long startId)
            {
                if (startId < 0)
                    throw new ArgumentOutOfRangeException(nameof(startId), startId, "Buffer request id base must be non-negative.");
                _requestIdBase = startId;
            }

            internal IMemoryOwner<byte> RentEncoded(int ordinal)
                => _pool.Rent(EncodedBucketName, _encodedBucketLength, checked(_requestIdBase + ordinal));

            internal IMemoryOwner<byte> RentCompressed(int ordinal)
                => _pool.Rent(CompressedBucketName, _compressedBucketLength, checked(_requestIdBase + ordinal));

            void RegisterBuckets(int columnCount)
            {
                if (columnCount == 0)
                    return;

                _pool.Register(EncodedBucketName, _encodedBucketLength, columnCount);
                _pool.Register(CompressedBucketName, _compressedBucketLength, columnCount);
            }

            static int GetMaxEncodedLength(Column[] columns, uint? rowGroupRowCountHint)
            {
                if (columns.Length == 0)
                    return DefaultEncodedBufferBytes;

                var maxLength = DefaultEncodedBufferBytes;
                for (var i = 0; i < columns.Length; i++)
                {
                    var column = columns[i];
                    var length = DefaultEncodedBufferBytes;
                    if (rowGroupRowCountHint.HasValue)
                    {
                        var rowCount = checked((int)rowGroupRowCountHint.Value);
                        if (ColumnCodec.TryGetFixedWidthBytes(column.PhysicalType, out var width))
                        {
                            var hintLength = checked(rowCount * width);
                            if (column.Options.Repetition is ParquetRepetition.Optional)
                                hintLength = checked(hintLength + ColumnCodec.GetDefinitionLevelsByteCount(rowCount));
                            if (hintLength > length)
                                length = hintLength;
                        }
                        else if (column.PhysicalType is ParquetPhysicalType.ByteArray)
                        {
                            var variableWidthHint = checked(rowCount * 8);
                            if (column.Options.Repetition is ParquetRepetition.Optional)
                                variableWidthHint = checked(variableWidthHint + ColumnCodec.GetDefinitionLevelsByteCount(rowCount));
                            if (variableWidthHint > length)
                                length = variableWidthHint;
                        }
                    }
                    if (length > maxLength)
                        maxLength = length;
                }
                return maxLength;
            }

            static int GetCompressedBufferLength(int uncompressedLength, int maxCompressedBytes, CompressionKind compression)
            {
                if (maxCompressedBytes > 0)
                    return maxCompressedBytes;
                if (compression == CompressionKind.None)
                    return uncompressedLength;

                var required = compression switch
                {
                    CompressionKind.Snappy => Snappy.GetMaxCompressedLength(uncompressedLength),
                    CompressionKind.Lz4 => LZ4Codec.MaximumOutputSize(uncompressedLength),
                    CompressionKind.Gzip => checked(uncompressedLength + Math.Max(256, uncompressedLength >> 3)),
                    CompressionKind.Brotli => checked(uncompressedLength + Math.Max(256, uncompressedLength >> 3)),
                    CompressionKind.Zstd => checked(uncompressedLength + Math.Max(256, uncompressedLength >> 3)),
                    _ => uncompressedLength
                };

                return required < uncompressedLength ? uncompressedLength : required;
            }
        }
    }
}
