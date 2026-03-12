using System.Buffers.Binary;
using Plank.Schema;
using Plank.Writing.PageStrategy;
using Plank.Writing.Thrift;

namespace Plank.Writing;

public sealed class ParquetWriter
{
    static readonly byte[] _fileMagic = "PAR1"u8.ToArray();

    Stream _stream = null!;
    readonly ParquetSchema _schema;
    readonly ParquetWriterOptions _options;
    internal readonly Column[] ColumnsByOrdinal;
    readonly IPageStrategy[] _pageStrategiesByOrdinal;
    internal readonly string[][] ColumnPathsByOrdinal;
    internal readonly LeafProjectionInfo[] ColumnProjectionInfosByOrdinal;
    internal readonly int ColumnCount;
    internal readonly BufferWriterFactory BufferWriters;
    internal readonly CompressionKind Compression;
    internal readonly CompressionContext CompressionContext;
    internal readonly ColumnChunkMetadata[] OpenRowGroupColumnMetadata;
    readonly RowGroupWriter _rowGroupWriter;
    internal BufferWriter SerializedRowGroupsMetadata;
    internal BufferWriter SerializedFileMetadata;
    internal long FileOffset;
    int _rowGroupCount;
    long _totalRowCount;
    bool _rowGroupOpen;
    bool _streamDisposed;

    internal ParquetWriter(Stream stream, ParquetSchema schema, ParquetWriterOptions options)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(options);

        _schema = schema;
        _options = options;
        _options.Validate();
        ColumnsByOrdinal = _schema.Columns.IsDefault ? [] : _schema.Columns.ToArray();
        ColumnPathsByOrdinal = _schema.LeafPaths.IsDefault || _schema.LeafPaths.Length == 0
            ? ColumnsByOrdinal.Select(static c => new[] { c.Name }).ToArray()
            : _schema.LeafPaths.Select(static p => p.ToArray()).ToArray();
        ColumnProjectionInfosByOrdinal = _schema.LeafProjectionInfos.IsDefault || _schema.LeafProjectionInfos.Length == 0
            ? ColumnsByOrdinal.Select(static c => new LeafProjectionInfo(IsList: false, ListOptional: false,
                ElementOptional: false, MaxRepetitionLevel: 0,
                MaxDefinitionLevel: c.Options.Repetition == ParquetRepetition.Optional ? 1 : 0)).ToArray()
            : _schema.LeafProjectionInfos.ToArray();
        if (ColumnPathsByOrdinal.Length != ColumnsByOrdinal.Length)
            throw new InvalidOperationException("Leaf path projection did not match projected column count.");
        if (ColumnProjectionInfosByOrdinal.Length != ColumnsByOrdinal.Length)
            throw new InvalidOperationException("Leaf projection metadata did not match projected column count.");
        ColumnCount = ColumnsByOrdinal.Length;
        _pageStrategiesByOrdinal = CreateColumnPageStrategies(ColumnsByOrdinal, _schema.PageStrategiesByColumnName);
        BufferWriters = new BufferWriterFactory(_options.BufferPool, _options.BufferChunkSizeBytes,
            _options.InitialPageBufferBytes, _options.InitialColumnBufferBytes, _options.BufferChunkSizeBytes);
        Compression = _options.Compression;
        CompressionContext = new CompressionContext(BufferWriters);
        OpenRowGroupColumnMetadata = ColumnCount == 0 ? [] : new ColumnChunkMetadata[ColumnCount];
        _rowGroupWriter = new RowGroupWriter(this);
        SerializedRowGroupsMetadata = BufferWriters.CreateMetadataBufferWriter();
        SerializedFileMetadata = BufferWriters.CreateMetadataBufferWriter();
        FileOffset = 0;
        OpenFile(stream);
    }

    public uint RowApiMaxParallelism
        => _options.RowApiMaxParallelism;

    public SerializedColumn<T> CreateSerializedColumn<T>(Column column)
        => new(this, column, _options.InitialPageCapacity);

    public void Reset(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (_rowGroupOpen)
            throw new InvalidOperationException("Cannot reset while a row group is open.");

        if (ReferenceEquals(stream, _stream))
            PrepareCurrentStreamForReset();
        else
            DisposeCurrentStream();
        OpenFile(stream);
    }

    public RowGroupWriter StartRowGroup()
    {
        ThrowIfStreamClosed();
        if (_rowGroupOpen)
            throw new InvalidOperationException("A row group is already open for this writer.");

        _rowGroupOpen = true;
        if (ColumnCount == 0)
        {
            ParquetMetadataThriftWriter.WriteRowGroup(ref SerializedRowGroupsMetadata, ColumnsByOrdinal,
                ColumnPathsByOrdinal, OpenRowGroupColumnMetadata, 0);
            CompleteOpenRowGroup(0);
        }

        _rowGroupWriter.ResetForNewRowGroup();
        return _rowGroupWriter;
    }

    public void CloseFile()
    {
        ThrowIfStreamClosed();
        if (_rowGroupOpen)
            throw new InvalidOperationException("Cannot close the file while a row group is still open.");

        WriteFileFooter();
        DisposeCurrentStream();
    }

    void ThrowIfStreamClosed()
    {
        if (_streamDisposed)
            throw new InvalidOperationException("The current file stream is closed. Call Reset(stream) to start a new file.");
    }

    void DisposeCurrentStream()
    {
        if (_streamDisposed)
            return;
        _stream.Dispose();
        _streamDisposed = true;
    }

    void PrepareCurrentStreamForReset()
    {
        if (!_stream.CanSeek)
            throw new InvalidOperationException("Cannot reset to the same stream when it is not seekable.");
        if (!_stream.CanWrite)
            throw new InvalidOperationException("Cannot reset to the same stream when it is not writable.");
        _stream.Position = 0;
        _stream.SetLength(0);
    }

    internal uint GetColumnOrdinal(Column column)
    {
        var ordinal = Array.IndexOf(ColumnsByOrdinal, column);
        if (ordinal >= 0)
            return (uint)ordinal;

        throw new ArgumentException("SerializedColumn column does not belong to this schema.", nameof(column));
    }

    internal IPageStrategy GetPageStrategy(uint columnOrdinal)
        => _pageStrategiesByOrdinal[columnOrdinal];

    static IPageStrategy[] CreateColumnPageStrategies(Column[] columns,
        IReadOnlyDictionary<string, IPageStrategy> pageStrategiesByColumnName)
    {
        if (columns.Length == 0)
            return [];

        var result = new IPageStrategy[columns.Length];
        for (var i = 0; i < result.Length; i++)
        {
            if (pageStrategiesByColumnName.TryGetValue(columns[i].Name, out var overrideStrategy))
                result[i] = overrideStrategy;
            else
                result[i] = new DefaultStrategy(columns[i]);
        }
        return result;
    }

    internal void WriteBuffer(ref BufferWriter buffer)
    {
        buffer.WriteTo(_stream);
        FileOffset = checked(FileOffset + buffer.WrittenLength);
    }

    void OpenFile(Stream stream)
    {
        _stream = stream;
        _streamDisposed = false;
        _rowGroupCount = 0;
        _totalRowCount = 0;
        _rowGroupOpen = false;
        FileOffset = 0;
        SerializedRowGroupsMetadata.Reset();
        SerializedFileMetadata.Reset();
        WriteFileHeader();
    }

    void WriteFileHeader()
    {
        _stream.Write(_fileMagic);
        FileOffset = checked(FileOffset + _fileMagic.Length);
    }

    void WriteFileFooter()
    {
        SerializedFileMetadata.Reset();
        ParquetMetadataThriftWriter.WriteFileMetaData(ref SerializedFileMetadata, _schema, _rowGroupCount, _totalRowCount,
            ref SerializedRowGroupsMetadata);
        var metadataLength = SerializedFileMetadata.WrittenLength;
        WriteBuffer(ref SerializedFileMetadata);
        Span<byte> suffix = stackalloc byte[sizeof(int) + 4];
        BinaryPrimitives.WriteInt32LittleEndian(suffix, metadataLength);
        _fileMagic.CopyTo(suffix[sizeof(int)..]);
        _stream.Write(suffix);
        FileOffset = checked(FileOffset + suffix.Length);
    }

    internal void CompleteOpenRowGroup(int rowCount)
    {
        _rowGroupCount++;
        _totalRowCount = checked(_totalRowCount + rowCount);
        _rowGroupOpen = false;
    }
}
