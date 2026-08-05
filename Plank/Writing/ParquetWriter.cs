using System.Buffers.Binary;
using Plank.Reading.Logical;
using Plank.Schema;
using Plank.Writing.PageStrategy;
using Plank.Writing.Thrift;
using TextEncoding = System.Text.Encoding;

namespace Plank.Writing;

public sealed class ParquetWriter
{
    static readonly byte[] _fileMagic = "PAR1"u8.ToArray();

    IParquetFile _file = null!;
    readonly ParquetSchema _schema;
    readonly ParquetWriterOptions _options;
    string? _createdBy;
    ParquetKeyValueMetadata[] _keyValueMetadata;
    internal readonly Column[] ColumnsByOrdinal;
    readonly PageStrategyContext[] _pageStrategyContextsByOrdinal;
    internal readonly string[][] ColumnPathsByOrdinal;
    internal readonly LeafProjectionInfo[] ColumnProjectionInfosByOrdinal;
    internal readonly int ColumnCount;
    internal readonly BufferWriterFactory BufferWriters;
    internal readonly ParquetFileVersion FileVersion;
    internal readonly ParquetDataPageVersion DataPageVersion;
    internal readonly ResolvedCompression[] ColumnCompressionsByOrdinal;
    internal readonly bool WritePageIndexes;
    internal readonly ParquetSortingColumn[] SortingColumns;
    internal readonly bool WritePageCrc;
    internal readonly CompressionContext CompressionContext;
    internal readonly ColumnChunkMetadata[] OpenRowGroupColumnMetadata;
    readonly RowGroupWriter _rowGroupWriter;
    readonly List<ISerializedColumn> _serializedColumns;
    internal BufferWriter SerializedRowGroupsMetadata;
    internal BufferWriter SerializedFileMetadata;
    internal long FileOffset;
    int _rowGroupCount;
    long _totalRowCount;
    LatestRowGroupValues? _latestRowGroupValues;
    byte[]? _latestRowGroupMetadata;
    long _latestRowGroupOffset;
    long _originalFooterOffset;
    uint _latestRowCount;
    bool _replacingLatestRowGroup;
    bool _rowGroupOpen;
    bool _fileClosed;

    internal ParquetWriter(Stream stream, ParquetSchema schema, ParquetWriterOptions options)
        : this(new StreamParquetFile(stream), schema, options, appendOptions: null)
    {
    }

    internal ParquetWriter(Stream stream, ParquetSchema schema, ParquetAppendOptions options)
        : this(new StreamParquetFile(stream), schema, options.WriterOptions, options)
    {
    }

    internal ParquetWriter(IParquetFile file, ParquetSchema schema, ParquetWriterOptions options)
        : this(file, schema, options, appendOptions: null)
    {
    }

    internal ParquetWriter(IParquetFile file, ParquetSchema schema, ParquetAppendOptions options)
        : this(file, schema, options.WriterOptions, options)
    {
    }

    ParquetWriter(IParquetFile file, ParquetSchema schema, ParquetWriterOptions options,
        ParquetAppendOptions? appendOptions)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(options);

        _schema = schema;
        _options = options;
        if (appendOptions is null)
            _options.Validate();
        else
            appendOptions.Validate();
        _createdBy = null;
        _keyValueMetadata = [];
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
        _pageStrategyContextsByOrdinal = CreateColumnPageStrategyContexts(ColumnsByOrdinal,
            _options.TargetDataPageSizeBytes);
        BufferWriters = new BufferWriterFactory(_options.BufferPool, _options.BufferChunkSizeBytes,
            _options.InitialPageBufferBytes, _options.InitialColumnBufferBytes, _options.BufferChunkSizeBytes);
        FileVersion = _options.FileVersion;
        DataPageVersion = _options.DataPageVersion;
        ColumnCompressionsByOrdinal = ResolveColumnCompressions(ColumnsByOrdinal, _options);
        WritePageIndexes = _options.WritePageIndexes;
        SortingColumns = ValidateSortingColumns(_options.SortingColumns, ColumnCount);
        WritePageCrc = _options.WritePageCrc;
        CompressionContext = new CompressionContext(BufferWriters);
        OpenRowGroupColumnMetadata = ColumnCount == 0 ? [] : new ColumnChunkMetadata[ColumnCount];
        _rowGroupWriter = new RowGroupWriter(this);
        _serializedColumns = [];
        SerializedRowGroupsMetadata = BufferWriters.CreateMetadataBufferWriter();
        SerializedFileMetadata = BufferWriters.CreateMetadataBufferWriter();
        FileOffset = 0;
        if (appendOptions is null)
        {
            _createdBy = options.CreatedBy;
            _keyValueMetadata = SnapshotMetadata(options.KeyValueMetadata);
            OpenFile(file);
        }
        else
        {
            try
            {
                OpenAppendFile(file, appendOptions);
            }
            catch
            {
                ReleaseBuffers();
                throw;
            }
        }
    }

    public uint RowApiMaxParallelism
        => _options.RowApiMaxParallelism;

    public SerializedColumn<T> CreateSerializedColumn<T>(LeafColumn column)
    {
        var ordinal = GetColumnOrdinal(column);
        var retainedValues = _latestRowGroupValues?.GetValues<T>(checked((int)ordinal));
        var serialized = new SerializedColumn<T>(this, column, _options.InitialPageCapacity, retainedValues);
        _serializedColumns.Add(serialized);
        return serialized;
    }

    public void Reset(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (_file is StreamParquetFile streamFile && ReferenceEquals(stream, streamFile.Stream))
        {
            Reset(_file);
            return;
        }

        Reset(new StreamParquetFile(stream));
    }

    public void Reset(IParquetFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (_rowGroupOpen)
            throw new InvalidOperationException("Cannot reset while a row group is open.");

        if (ReferenceEquals(file, _file))
            PrepareCurrentFileForReset();
        else
            CloseCurrentFile();
        OpenFile(file);
    }

    internal (int RowGroupCount, long RowCount) ImportFile(Stream source, ParquetReader reader,
        bool preserveMetadata)
    {
        ThrowIfFileClosed();
        ArgumentNullException.ThrowIfNull(source);
        if (_rowGroupOpen)
            throw new InvalidOperationException("Cannot merge a file while a row group is open.");
        if (!source.CanRead || !source.CanSeek)
            throw new ArgumentException("Merging requires a readable, seekable source stream.", nameof(source));
        if (_file is StreamParquetFile streamFile && ReferenceEquals(source, streamFile.Stream))
            throw new ArgumentException("The merge source and destination streams must be different.", nameof(source));

        var originalSourcePosition = source.Position;
        try
        {
            reader.Reset(source);
            var metadata = reader.PhysicalReader.Metadata;
            if (metadata.RowGroupCount > int.MaxValue - _rowGroupCount)
                throw new InvalidOperationException($"Cannot write more than {int.MaxValue} row groups to one file.");

            var importedRowCount = ValidateImport(metadata);
            var importedCreatedBy = preserveMetadata ? _options.CreatedBy ?? DecodeCreatedBy(metadata) : _createdBy;
            var importedKeyValueMetadata = preserveMetadata
                ? MergeMetadata(metadata, _options.KeyValueMetadata)
                : _keyValueMetadata;
            var fileOffsetBeforeImport = FileOffset;
            var metadataLengthBeforeImport = SerializedRowGroupsMetadata.WrittenLength;
            var rowGroupCountBeforeImport = _rowGroupCount;
            var totalRowCountBeforeImport = _totalRowCount;

            try
            {
                using var copyBuffer = _options.BufferPool.Rent(64 * 1024);
                for (var rowGroupOrdinal = 0; rowGroupOrdinal < metadata.RowGroupCount; rowGroupOrdinal++)
                    ImportRowGroup(source, metadata, rowGroupOrdinal, copyBuffer.Span);
            }
            catch
            {
                FileOffset = fileOffsetBeforeImport;
                _rowGroupCount = rowGroupCountBeforeImport;
                _totalRowCount = totalRowCountBeforeImport;
                SerializedRowGroupsMetadata.Truncate(metadataLengthBeforeImport);
                SerializedFileMetadata.Reset();
                _file.SetLength(checked((ulong)fileOffsetBeforeImport));
                throw;
            }

            if (preserveMetadata)
            {
                _createdBy = importedCreatedBy;
                _keyValueMetadata = importedKeyValueMetadata;
            }
            return (metadata.RowGroupCount, importedRowCount);
        }
        finally
        {
            source.Position = originalSourcePosition;
        }
    }

    public RowGroupWriter StartRowGroup()
    {
        ThrowIfFileClosed();
        if (_rowGroupOpen)
            throw new InvalidOperationException("A row group is already open for this writer.");
        if (_rowGroupCount == int.MaxValue)
            throw new InvalidOperationException($"Cannot write more than {int.MaxValue} row groups to one file.");

        PrepareLatestRowGroupReplacement();

        _rowGroupOpen = true;
        if (ColumnCount == 0)
        {
            ParquetMetadataThriftWriter.WriteRowGroup(ref SerializedRowGroupsMetadata, ColumnsByOrdinal,
                ColumnPathsByOrdinal, OpenRowGroupColumnMetadata, SortingColumns, 0);
            CompleteOpenRowGroup(0);
        }

        _rowGroupWriter.ResetForNewRowGroup();
        return _rowGroupWriter;
    }

    public void CloseFile()
    {
        ThrowIfFileClosed();
        if (_rowGroupOpen)
            throw new InvalidOperationException("Cannot close the file while a row group is still open.");

        RestoreUnchangedLatestRowGroup();
        WriteFileFooter();
        _file.Flush();
        CloseCurrentFile();
        ReleaseBuffers();
    }

    void ThrowIfFileClosed()
    {
        if (_fileClosed)
            throw new InvalidOperationException("The current file is closed. Call Reset(file) to start a new file.");
    }

    void CloseCurrentFile()
    {
        if (_fileClosed)
            return;
        _file.Close();
        _fileClosed = true;
    }

    void PrepareCurrentFileForReset()
    {
        _file.SetLength(0);
    }

    internal uint GetColumnOrdinal(LeafColumn column)
    {
        var ordinal = column.Ordinal;
        if ((uint)ordinal < (uint)ColumnsByOrdinal.Length &&
            ReferenceEquals(ColumnsByOrdinal[ordinal], column.Column))
            return (uint)ordinal;

        throw new ArgumentException("Leaf column does not belong to this writer's schema.", nameof(column));
    }

    internal PageStrategyContext GetPageStrategyContext(uint columnOrdinal)
        => _pageStrategyContextsByOrdinal[columnOrdinal];

    static PageStrategyContext[] CreateColumnPageStrategyContexts(Column[] columns, uint targetDataPageSizeBytes)
    {
        if (columns.Length == 0)
            return [];

        var result = new PageStrategyContext[columns.Length];
        for (var i = 0; i < result.Length; i++)
            result[i] = new PageStrategyContext(columns[i].PageStrategy
                ?? new DefaultStrategy(columns[i], targetDataPageSizeBytes));
        return result;
    }

    static ParquetSortingColumn[] ValidateSortingColumns(
        System.Collections.Immutable.ImmutableArray<ParquetSortingColumn> sortingColumns, int columnCount)
    {
        if (sortingColumns.IsDefaultOrEmpty)
            return [];

        var result = sortingColumns.ToArray();
        for (var i = 0; i < result.Length; i++)
        {
            var columnOrdinal = result[i].ColumnOrdinal;
            if ((uint)columnOrdinal >= (uint)columnCount)
                throw new ArgumentOutOfRangeException(nameof(ParquetWriterOptions.SortingColumns), columnOrdinal,
                    $"Sorting column ordinal must be between zero and {columnCount - 1}.");
            for (var previous = 0; previous < i; previous++)
                if (result[previous].ColumnOrdinal == columnOrdinal)
                    throw new ArgumentException($"Sorting column ordinal {columnOrdinal} is declared more than once.",
                        nameof(ParquetWriterOptions.SortingColumns));
        }
        return result;
    }

    static ResolvedCompression[] ResolveColumnCompressions(Column[] columns, ParquetWriterOptions options)
    {
        if (columns.Length == 0)
            return [];

        var result = new ResolvedCompression[columns.Length];
        for (var i = 0; i < result.Length; i++)
        {
            var columnOptions = columns[i].Options;
            var compression = columnOptions.Compression ?? options.Compression;
            var configuredLevel = columnOptions.CompressionLevel;
            if (!configuredLevel.HasValue && !columnOptions.Compression.HasValue)
                configuredLevel = options.CompressionLevel;
            var level = CompressionConfiguration.ResolveLevel(compression, configuredLevel,
                nameof(ColumnOptions.Compression), nameof(ColumnOptions.CompressionLevel));
            result[i] = new ResolvedCompression(compression, level);
        }
        return result;
    }

    internal void WriteBuffer(ref BufferWriter buffer)
    {
        buffer.WriteTo(_file, checked((ulong)FileOffset));
        FileOffset = checked(FileOffset + buffer.WrittenLength);
    }

    internal void WriteBytes(ReadOnlySpan<byte> bytes)
    {
        _file.Write(checked((ulong)FileOffset), bytes);
        FileOffset = checked(FileOffset + bytes.Length);
    }

    void OpenFile(IParquetFile file)
    {
        _file = file;
        _fileClosed = false;
        _rowGroupCount = 0;
        _totalRowCount = 0;
        _rowGroupOpen = false;
        _latestRowGroupValues = null;
        _latestRowGroupMetadata = null;
        _replacingLatestRowGroup = false;
        FileOffset = 0;
        if (!SerializedRowGroupsMetadata.IsInitialized)
            SerializedRowGroupsMetadata = BufferWriters.CreateMetadataBufferWriter();
        else
            SerializedRowGroupsMetadata.Reset();
        if (!SerializedFileMetadata.IsInitialized)
            SerializedFileMetadata = BufferWriters.CreateMetadataBufferWriter();
        else
            SerializedFileMetadata.Reset();
        WriteFileHeader();
    }

    void OpenAppendFile(IParquetFile file, ParquetAppendOptions appendOptions)
    {
        using var reader = new ParquetReader(_schema, new ParquetReaderOptions
        {
            BufferPool = _options.BufferPool,
            Strict = true
        });
        reader.Reset(file);
        var metadata = reader.PhysicalReader.Metadata;

        var appendLatest = appendOptions.AppendToLatestRowGroup && metadata.RowGroupCount > 0;
        var retainedRowGroupCount = metadata.RowGroupCount - (appendLatest ? 1 : 0);
        if (appendLatest)
        {
            var latestOrdinal = metadata.RowGroupCount - 1;
            var latestPhysical = metadata.RowGroups[latestOrdinal];
            _latestRowGroupValues = LatestRowGroupValues.Read(reader.RowGroups[latestOrdinal], ColumnsByOrdinal);
            var latestRelativeOffset = checked((int)(latestPhysical.MetadataOffset - metadata.FooterOffset));
            _latestRowGroupMetadata = metadata.FooterBytes
                .Slice(latestRelativeOffset, latestPhysical.MetadataLength).ToArray();
            _latestRowGroupOffset = checked((long)latestPhysical.ColumnChunkOffset);
            _originalFooterOffset = checked((long)metadata.FooterOffset);
            _latestRowCount = checked((uint)latestPhysical.RowCount);
        }

        SerializedRowGroupsMetadata.Reset();
        long totalRowCount = 0;
        for (var i = 0; i < retainedRowGroupCount; i++)
        {
            var rowGroup = metadata.RowGroups[i];
            var relativeOffset = checked((int)(rowGroup.MetadataOffset - metadata.FooterOffset));
            SerializedRowGroupsMetadata.Write(metadata.FooterBytes.Slice(relativeOffset, rowGroup.MetadataLength));
            totalRowCount = checked(totalRowCount + checked((long)rowGroup.RowCount));
        }

        if (appendOptions.PreserveExistingMetadata)
        {
            _createdBy = _options.CreatedBy ?? DecodeCreatedBy(metadata);
            _keyValueMetadata = MergeMetadata(metadata, _options.KeyValueMetadata);
        }
        else
        {
            _createdBy = _options.CreatedBy;
            _keyValueMetadata = SnapshotMetadata(_options.KeyValueMetadata);
        }

        _file = file;
        _fileClosed = false;
        _rowGroupCount = retainedRowGroupCount;
        _totalRowCount = totalRowCount;
        _rowGroupOpen = false;
        FileOffset = checked((long)metadata.FooterOffset);
        file.SetLength(checked((ulong)FileOffset));
        SerializedFileMetadata.Reset();
    }

    void PrepareLatestRowGroupReplacement()
    {
        if (_latestRowGroupMetadata is null)
            return;

        _file.SetLength(checked((ulong)_latestRowGroupOffset));
        FileOffset = _latestRowGroupOffset;
        _latestRowGroupMetadata = null;
        _replacingLatestRowGroup = true;
    }

    void RestoreUnchangedLatestRowGroup()
    {
        if (_latestRowGroupMetadata is not { } metadata)
            return;

        SerializedRowGroupsMetadata.Write(metadata);
        _rowGroupCount++;
        _totalRowCount = checked(_totalRowCount + _latestRowCount);
        _file.SetLength(checked((ulong)_originalFooterOffset));
        FileOffset = _originalFooterOffset;
        _latestRowGroupMetadata = null;
        _latestRowGroupValues = null;
    }

    static string? DecodeCreatedBy(Reading.Physical.ParquetFileMetadata metadata)
        => metadata.HasCreatedBy ? TextEncoding.UTF8.GetString(metadata.CreatedByUtf8) : null;

    static ParquetKeyValueMetadata[] MergeMetadata(Reading.Physical.ParquetFileMetadata metadata,
        IReadOnlyList<ParquetKeyValueMetadata> additions)
    {
        if (metadata.KeyValueMetadataCount == 0)
            return SnapshotMetadata(additions);

        var result = new ParquetKeyValueMetadata[checked(metadata.KeyValueMetadataCount + additions.Count)];
        for (var i = 0; i < metadata.KeyValueMetadataCount; i++)
        {
            var entry = metadata.KeyValueMetadata[i];
            var key = TextEncoding.UTF8.GetString(metadata.KeyValueMetadataKeyUtf8(i));
            var value = entry.HasValue ? TextEncoding.UTF8.GetString(metadata.KeyValueMetadataValueUtf8(i)) : null;
            result[i] = new ParquetKeyValueMetadata(key, value);
        }
        for (var i = 0; i < additions.Count; i++)
            result[metadata.KeyValueMetadataCount + i] = additions[i];
        return result;
    }

    static ParquetKeyValueMetadata[] SnapshotMetadata(IReadOnlyList<ParquetKeyValueMetadata> metadata)
        => metadata.Count == 0 ? [] : metadata.ToArray();

    long ValidateImport(Reading.Physical.ParquetFileMetadata metadata)
    {
        long rowCount = 0;
        for (var rowGroupOrdinal = 0; rowGroupOrdinal < metadata.RowGroupCount; rowGroupOrdinal++)
        {
            var rowGroup = metadata.RowGroups[rowGroupOrdinal];
            if (rowGroup.ColumnCount != ColumnCount)
                throw new CorruptParquetException(
                    $"Row group {rowGroupOrdinal} has {rowGroup.ColumnCount} columns; expected {ColumnCount}.");
            rowCount = checked(rowCount + checked((long)rowGroup.RowCount));
            for (var columnOrdinal = 0; columnOrdinal < rowGroup.ColumnCount; columnOrdinal++)
                ValidateImportChunk(metadata.ColumnChunk(rowGroupOrdinal, columnOrdinal), metadata.FooterOffset);
        }

        _ = checked(_totalRowCount + rowCount);
        return rowCount;
    }

    static void ValidateImportChunk(Reading.Physical.ParquetColumnChunkInfo chunk, ulong footerOffset)
    {
        ValidateImportRange(chunk.ChunkOffset, chunk.TotalCompressedSize, footerOffset, "column chunk");
        var chunkEnd = checked(chunk.ChunkOffset + chunk.TotalCompressedSize);
        if (chunk.DataPageOffset < chunk.ChunkOffset || chunk.DataPageOffset >= chunkEnd)
            throw new CorruptParquetException("Column data page offset is outside its column chunk.");
        if (chunk.DictionaryPageOffset != 0 &&
            (chunk.DictionaryPageOffset < chunk.ChunkOffset || chunk.DictionaryPageOffset >= chunkEnd))
            throw new CorruptParquetException("Column dictionary page offset is outside its column chunk.");
        if (chunk.ColumnIndexLength != 0)
            ValidateImportRange(chunk.ColumnIndexOffset, chunk.ColumnIndexLength, footerOffset, "column index");
        if (chunk.OffsetIndexLength != 0)
        {
            if (chunk.OffsetIndexLength > int.MaxValue)
                throw new NotSupportedException("Offset indexes larger than Int32.MaxValue are not supported.");
            ValidateImportRange(chunk.OffsetIndexOffset, chunk.OffsetIndexLength, footerOffset, "offset index");
        }
    }

    static void ValidateImportRange(ulong offset, ulong length, ulong footerOffset, string name)
    {
        if (length == 0 || offset < (ulong)_fileMagic.Length || offset > footerOffset ||
            length > footerOffset - offset)
            throw new CorruptParquetException(
                $"The {name} at offset {offset} with length {length} is outside the source data section.");
        if (offset > long.MaxValue || length > long.MaxValue)
            throw new NotSupportedException($"The {name} exceeds the supported stream offset range.");
    }

    void ImportRowGroup(Stream source, Reading.Physical.ParquetFileMetadata sourceMetadata, int rowGroupOrdinal,
        Span<byte> copyBuffer)
    {
        var rowGroup = sourceMetadata.RowGroups[rowGroupOrdinal];
        for (var columnOrdinal = 0; columnOrdinal < rowGroup.ColumnCount; columnOrdinal++)
        {
            var sourceChunk = sourceMetadata.ColumnChunk(rowGroupOrdinal, columnOrdinal);
            ref var importedChunk = ref OpenRowGroupColumnMetadata[columnOrdinal];
            importedChunk = default;
            var destinationChunkOffset = FileOffset;
            var relocation = checked(destinationChunkOffset - checked((long)sourceChunk.ChunkOffset));
            CopyRange(source, sourceChunk.ChunkOffset, sourceChunk.TotalCompressedSize, copyBuffer);
            importedChunk.DataPageOffset = RelocateOffset(sourceChunk.DataPageOffset, relocation);
            importedChunk.DictionaryPageOffset = RelocateOffset(sourceChunk.DictionaryPageOffset, relocation);
            importedChunk.ValueCount = checked((long)sourceChunk.ValueCount);
            importedChunk.TotalUncompressedSize = checked((long)sourceChunk.TotalUncompressedSize);
            importedChunk.TotalCompressedSize = checked((long)sourceChunk.TotalCompressedSize);
            importedChunk.Compression = sourceChunk.Compression;
            importedChunk.HasDictionaryPage = sourceChunk.DictionaryPageOffset != 0;
        }

        for (var columnOrdinal = 0; columnOrdinal < rowGroup.ColumnCount; columnOrdinal++)
        {
            var sourceChunk = sourceMetadata.ColumnChunk(rowGroupOrdinal, columnOrdinal);
            ref var importedChunk = ref OpenRowGroupColumnMetadata[columnOrdinal];
            if (sourceChunk.ColumnIndexLength != 0)
            {
                importedChunk.ColumnIndexOffset = FileOffset;
                importedChunk.ColumnIndexLength = sourceChunk.ColumnIndexLength;
                CopyRange(source, sourceChunk.ColumnIndexOffset, sourceChunk.ColumnIndexLength, copyBuffer);
            }
            if (sourceChunk.OffsetIndexLength == 0)
                continue;

            using var offsetIndex = _options.BufferPool.Rent(sourceChunk.OffsetIndexLength);
            source.Position = checked((long)sourceChunk.OffsetIndexOffset);
            source.ReadExactly(offsetIndex.Span[..checked((int)sourceChunk.OffsetIndexLength)]);
            SerializedFileMetadata.Reset();
            var relocation = checked(importedChunk.DataPageOffset - checked((long)sourceChunk.DataPageOffset));
            ParquetMetadataThriftWriter.RelocateOffsetIndex(ref SerializedFileMetadata,
                offsetIndex.Span[..checked((int)sourceChunk.OffsetIndexLength)], relocation);
            importedChunk.OffsetIndexOffset = FileOffset;
            importedChunk.OffsetIndexLength = checked((uint)SerializedFileMetadata.WrittenLength);
            WriteBuffer(ref SerializedFileMetadata);
        }

        ParquetMetadataThriftWriter.WriteImportedRowGroup(ref SerializedRowGroupsMetadata, ColumnsByOrdinal,
            ColumnPathsByOrdinal, OpenRowGroupColumnMetadata, sourceMetadata, rowGroupOrdinal, rowGroup.RowCount);
        _rowGroupCount++;
        _totalRowCount = checked(_totalRowCount + checked((long)rowGroup.RowCount));
    }

    void CopyRange(Stream source, ulong offset, ulong length, Span<byte> buffer)
    {
        source.Position = checked((long)offset);
        var remaining = length;
        while (remaining > 0)
        {
            var count = checked((int)Math.Min(remaining, (ulong)buffer.Length));
            source.ReadExactly(buffer[..count]);
            _file.Write(checked((ulong)FileOffset), buffer[..count]);
            FileOffset = checked(FileOffset + count);
            remaining -= (uint)count;
        }
    }

    static long RelocateOffset(ulong offset, long relocation)
        => offset == 0 ? 0 : checked(checked((long)offset) + relocation);

    void ReleaseBuffers()
    {
        SerializedRowGroupsMetadata.Dispose();
        SerializedFileMetadata.Dispose();
        _rowGroupWriter.ReleaseBuffers();
        CompressionContext.Dispose();
        for (var i = 0; i < _serializedColumns.Count; i++)
            _serializedColumns[i].ReleaseBuffers();
    }

    void WriteFileHeader()
    {
        _file.Write(checked((ulong)FileOffset), _fileMagic);
        FileOffset = checked(FileOffset + _fileMagic.Length);
    }

    void WriteFileFooter()
    {
        SerializedFileMetadata.Reset();
        ParquetMetadataThriftWriter.WriteFileMetaData(ref SerializedFileMetadata, _schema, FileVersion, _rowGroupCount,
            _totalRowCount, ref SerializedRowGroupsMetadata, _createdBy, _keyValueMetadata);
        var metadataLength = SerializedFileMetadata.WrittenLength;
        WriteBuffer(ref SerializedFileMetadata);
        Span<byte> suffix = stackalloc byte[sizeof(int) + 4];
        BinaryPrimitives.WriteInt32LittleEndian(suffix, metadataLength);
        _fileMagic.CopyTo(suffix[sizeof(int)..]);
        _file.Write(checked((ulong)FileOffset), suffix);
        FileOffset = checked(FileOffset + suffix.Length);
    }

    internal void CompleteOpenRowGroup(uint rowCount)
    {
        _rowGroupCount++;
        _totalRowCount = checked(_totalRowCount + rowCount);
        _rowGroupOpen = false;
        if (_replacingLatestRowGroup)
        {
            _latestRowGroupValues = null;
            _replacingLatestRowGroup = false;
        }
    }
}
