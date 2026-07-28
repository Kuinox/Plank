using Plank.Reading.Logical.Internal;
using Plank.Schema;

namespace Plank.Reading.Logical;

/// <summary>Exposes footer-resident and lazily opened page metadata for a logical column chunk.</summary>
public readonly struct ParquetColumnChunkMetadata
{
    readonly RowGroup _rowGroup;
    readonly int _columnOrdinal;

    internal ParquetColumnChunkMetadata(RowGroup rowGroup, int columnOrdinal)
    {
        _rowGroup = rowGroup;
        _columnOrdinal = columnOrdinal;
    }

    public LeafColumn Definition
        => _rowGroup.GetColumnDefinition(_columnOrdinal);

    public ulong ValueCount
        => GetMetadata().ValueCount;

    public CompressionKind Compression
        => GetMetadata().Compression;

    public ReadOnlySpan<EncodingKind> Encodings
        => GetMetadata().Encodings;

    public bool HasColumnIndex
        => GetMetadata().ColumnIndexLength != 0;

    public bool HasOffsetIndex
        => GetMetadata().OffsetIndexLength != 0;

    public ParquetStatistics Statistics
    {
        get
        {
            var metadata = GetMetadata();
            return _rowGroup.CreateStatistics(Definition, metadata.Statistics);
        }
    }

    /// <summary>
    /// Opens a disposable data-page metadata collection without reading or decompressing page payloads.
    /// </summary>
    /// <remarks>
    /// This is an observational inspection API and does not optimize later enumeration. Pass a
    /// <see cref="ParquetPagePruner"/> when opening or resetting the reader for efficient scans.
    /// </remarks>
    public ParquetDataPageMetadataCollection OpenPages()
    {
        var metadata = GetMetadata();
        var physicalColumnOrdinal = metadata.PhysicalColumnOrdinal >= 0
            ? metadata.PhysicalColumnOrdinal
            : _columnOrdinal;
        return PageMetadataReader.Open(_rowGroup.GetReader().PhysicalReader, _rowGroup.Index,
            physicalColumnOrdinal, Definition, _rowGroup.RowCount);
    }

    InternalColumnChunkMetadata GetMetadata()
        => _rowGroup.GetColumnChunkMetadata(_columnOrdinal);
}
