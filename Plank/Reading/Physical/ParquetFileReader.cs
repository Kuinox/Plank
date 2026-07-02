using System.Buffers.Binary;
using Plank.Reading.Physical.Internal;
using Plank.Schema;
using Plank.Writing;

namespace Plank.Reading.Physical;

public sealed class ParquetFileReader : IDisposable
{
    static ReadOnlySpan<byte> FileMagic
        => "PAR1"u8;

    readonly ParquetFileReaderOptions _options;
    PhysicalMetadataStore _metadata;
    PhysicalMetadataStore _scratch;
    StreamReadSource? _streamSource;
    StreamReadSource? _streamSourceScratch;
    IParquetReadSource? _source;
    int _version;
    bool _disposed;

    public ParquetFileReader(ParquetFileReaderOptions? options = null)
    {
        _options = options ?? ParquetFileReaderOptions.Default;
        _options.Validate();
        _metadata = new PhysicalMetadataStore(_options.BufferPool);
        _scratch = new PhysicalMetadataStore(_options.BufferPool);
    }

    public int FileVersion
    {
        get
        {
            ThrowIfDisposed();
            return _metadata.FileVersion;
        }
    }

    public ulong FooterOffset
    {
        get
        {
            ThrowIfDisposed();
            return _metadata.FooterOffset;
        }
    }

    public uint FooterLength
    {
        get
        {
            ThrowIfDisposed();
            return _metadata.FooterLength;
        }
    }

    public int SchemaNodeCount
    {
        get
        {
            ThrowIfDisposed();
            return _metadata.SchemaNodeCount;
        }
    }

    public int ColumnCount
    {
        get
        {
            ThrowIfDisposed();
            return _metadata.ColumnCount;
        }
    }

    public int RowGroupCount
    {
        get
        {
            ThrowIfDisposed();
            return _metadata.RowGroupCount;
        }
    }

    public void Reset(Stream stream)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(stream);
        if (_streamSourceScratch is null)
            _streamSourceScratch = new StreamReadSource(stream);
        else
            _streamSourceScratch.Reset(stream);

        ResetCore(_streamSourceScratch);
        (_streamSource, _streamSourceScratch) = (_streamSourceScratch, _streamSource);
        _source = _streamSource;
    }

    public void Reset(IParquetReadSource source)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(source);
        ResetCore(source);
        _source = source;
    }

    public ParquetSchemaNodeInfo SchemaNode(int nodeOrdinal)
    {
        ValidateOrdinal(nodeOrdinal, _metadata.SchemaNodeCount, nameof(nodeOrdinal));
        return new ParquetSchemaNodeInfo(this, _version, nodeOrdinal);
    }

    public ParquetColumnSchemaInfo ColumnSchema(int columnOrdinal)
    {
        ValidateOrdinal(columnOrdinal, _metadata.ColumnCount, nameof(columnOrdinal));
        return new ParquetColumnSchemaInfo(this, _version, columnOrdinal);
    }

    public ParquetRowGroupInfo RowGroup(int rowGroupOrdinal)
    {
        ValidateOrdinal(rowGroupOrdinal, _metadata.RowGroupCount, nameof(rowGroupOrdinal));
        return new ParquetRowGroupInfo(this, _version, rowGroupOrdinal);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _version++;
        _metadata.Dispose();
        _scratch.Dispose();
        _source = null;
    }

    void ResetCore(IParquetReadSource source)
    {
        if (source.Length < 12)
            throw new CorruptParquetException("Stream is too small to contain a Parquet footer.");

        Span<byte> trailer = stackalloc byte[8];
        source.ReadExactly(source.Length - (ulong)trailer.Length, trailer);
        if (!trailer[4..].SequenceEqual(FileMagic))
            throw new CorruptParquetException("Stream does not end with the PAR1 footer marker.");

        var footerLength = BinaryPrimitives.ReadUInt32LittleEndian(trailer);
        if (footerLength > source.Length - (ulong)trailer.Length)
            throw new CorruptParquetException("Footer length exceeds stream size.");

        var footerOffset = source.Length - (ulong)trailer.Length - footerLength;
        if (footerOffset < 4)
            throw new CorruptParquetException("Footer offset is invalid for this stream.");

        _scratch.Clear();
        _scratch.FooterOffset = footerOffset;
        _scratch.FooterLength = footerLength;
        var footerBytes = _scratch.PrepareFooter(footerLength);
        source.ReadExactly(footerOffset, footerBytes);
        PhysicalMetadataThriftReader.Read(_scratch);

        (_metadata, _scratch) = (_scratch, _metadata);
        _version++;
    }

    internal IParquetBufferPool BufferPool
        => _options.BufferPool;

    internal IParquetReadSource Source
    {
        get
        {
            ThrowIfDisposed();
            return _source ?? throw new InvalidOperationException("The reader has not been reset with a source.");
        }
    }

    internal int Version
        => _version;

    internal ReadOnlySpan<byte> FooterBytes
        => _metadata.FooterBytes.AsSpan(0, _metadata.FooterByteCount);

    internal PhysicalSchemaNode GetSchemaNode(int version, int ordinal)
    {
        ValidateHandle(version);
        return _metadata.SchemaNodes[ordinal];
    }

    internal PhysicalColumnSchema GetColumnSchema(int version, int ordinal)
    {
        ValidateHandle(version);
        return _metadata.Columns[ordinal];
    }

    internal PhysicalRowGroup GetRowGroup(int version, int ordinal)
    {
        ValidateHandle(version);
        return _metadata.RowGroups[ordinal];
    }

    internal ParquetColumnChunkInfo GetColumnChunk(int version, int rowGroupOrdinal, int columnOrdinal)
    {
        ValidateHandle(version);
        var rowGroup = _metadata.RowGroups[rowGroupOrdinal];
        ValidateOrdinal(columnOrdinal, rowGroup.ColumnCount, nameof(columnOrdinal));
        return _metadata.ColumnChunks[rowGroup.ColumnStart + columnOrdinal];
    }

    internal ReadOnlySpan<byte> GetName(int version, int nodeOrdinal)
    {
        var node = GetSchemaNode(version, nodeOrdinal);
        return FooterBytes.Slice(node.NameOffset, node.NameLength);
    }

    internal int GetPathNodeOrdinal(int version, int columnOrdinal, int segmentOrdinal)
    {
        var column = GetColumnSchema(version, columnOrdinal);
        ValidateOrdinal(segmentOrdinal, column.PathSegmentCount, nameof(segmentOrdinal));
        var nodeOrdinal = column.NodeOrdinal;
        for (var i = column.PathSegmentCount - 1; i > segmentOrdinal; i--)
            nodeOrdinal = _metadata.SchemaNodes[nodeOrdinal].ParentOrdinal;
        return nodeOrdinal;
    }

    internal void ValidateHandle(int version)
    {
        ThrowIfDisposed();
        if (version != _version)
            throw new InvalidOperationException("The reader handle is stale because the reader was reset.");
    }

    void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(ParquetFileReader));
    }

    static void ValidateOrdinal(int ordinal, int count, string parameterName)
    {
        if ((uint)ordinal >= (uint)count)
            throw new ArgumentOutOfRangeException(parameterName, ordinal,
                $"Ordinal must be between zero and {count - 1}.");
    }
}
