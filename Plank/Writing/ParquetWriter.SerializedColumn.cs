using System.Buffers;
using Plank.Schema;

namespace Plank.Writing;

public sealed partial class ParquetWriter
{    public readonly struct SerializedColumn : IEquatable<SerializedColumn>
    {
        internal SerializedColumn(Column column, ReadOnlyMemory<byte> payload, int rowCount, int valueCount, int uncompressedLength, int nullCount, int definitionLevelsByteLength, int repetitionLevelsByteLength, EncodingKind encoding, CompressionKind compression, ColumnLogicalType logicalType, bool repeatedElementOptional, IMemoryOwner<byte>? payloadOwner = null)
        {
            Column = column;
            Payload = payload;
            RowCount = rowCount;
            ValueCount = valueCount;
            UncompressedLength = uncompressedLength;
            NullCount = nullCount;
            DefinitionLevelsByteLength = definitionLevelsByteLength;
            RepetitionLevelsByteLength = repetitionLevelsByteLength;
            Encoding = encoding;
            Compression = compression;
            LogicalType = logicalType;
            RepeatedElementOptional = repeatedElementOptional;
            PayloadOwner = payloadOwner;
        }

        public Column Column { get; }

        public ReadOnlyMemory<byte> Payload { get; }

        public int RowCount { get; }

        public int ValueCount { get; }

        public int UncompressedLength { get; }

        internal int NullCount { get; }

        internal int DefinitionLevelsByteLength { get; }

        internal int RepetitionLevelsByteLength { get; }

        public EncodingKind Encoding { get; }

        public CompressionKind Compression { get; }

        internal ColumnLogicalType LogicalType { get; }

        internal bool RepeatedElementOptional { get; }

        internal IMemoryOwner<byte>? PayloadOwner { get; }

        public bool Equals(SerializedColumn other)
            => ReferenceEquals(Column, other.Column)
               && Payload.Equals(other.Payload)
               && RowCount == other.RowCount
               && ValueCount == other.ValueCount
               && UncompressedLength == other.UncompressedLength
               && NullCount == other.NullCount
               && DefinitionLevelsByteLength == other.DefinitionLevelsByteLength
               && RepetitionLevelsByteLength == other.RepetitionLevelsByteLength
               && Encoding == other.Encoding
               && Compression == other.Compression
               && LogicalType == other.LogicalType
               && RepeatedElementOptional == other.RepeatedElementOptional
               && ReferenceEquals(PayloadOwner, other.PayloadOwner);

        public override bool Equals(object? obj)
            => obj is SerializedColumn other && Equals(other);

        public override int GetHashCode()
        {
            var hash = new HashCode();
            hash.Add(Column);
            hash.Add(Payload);
            hash.Add(RowCount);
            hash.Add(ValueCount);
            hash.Add(UncompressedLength);
            hash.Add(NullCount);
            hash.Add(DefinitionLevelsByteLength);
            hash.Add(RepetitionLevelsByteLength);
            hash.Add(Encoding);
            hash.Add(Compression);
            hash.Add(LogicalType);
            hash.Add(RepeatedElementOptional);
            hash.Add(PayloadOwner);
            return hash.ToHashCode();
        }

        public static bool operator ==(SerializedColumn left, SerializedColumn right)
            => left.Equals(right);

        public static bool operator !=(SerializedColumn left, SerializedColumn right)
            => !left.Equals(right);
    }
}
