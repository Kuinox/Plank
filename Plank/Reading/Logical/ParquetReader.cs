using Plank.Reading.Logical.Internal;
using Plank.Reading.Physical;
using Plank.Schema;

namespace Plank.Reading.Logical;

public sealed class ParquetReader : IDisposable
{
    static readonly ParquetSchema EmptySchema = new(System.Collections.Immutable.ImmutableArray<ColumnDefinition>.Empty);
    readonly ParquetSchema? _requestedSchema;
    internal readonly ParquetFileReader PhysicalReader;
    InternalParquetFooter _footer;
    int _footerVersion;
    ParquetPagePruner? _pagePruner;
    bool _disposed;

    public ParquetReader(ParquetReaderOptions? options = null)
        : this(options, requestedSchema: null)
    {
    }

    internal ParquetReader(ParquetSchema requestedSchema, ParquetReaderOptions? options = null)
        : this(options, RequireRequestedSchema(requestedSchema))
    {
    }

    ParquetReader(ParquetReaderOptions? options, ParquetSchema? requestedSchema)
    {
        var readerOptions = options ?? ParquetReaderOptions.Default;
        readerOptions.Validate();

        Options = readerOptions;
        _requestedSchema = requestedSchema;
        PhysicalReader = new ParquetFileReader(new ParquetFileReaderOptions
        {
            BufferPool = readerOptions.BufferPool,
            VerifyPageCrc = readerOptions.VerifyPageCrc
        });
        Schema = EmptySchema;
        _footer = InternalParquetFooter.Empty;
        Metadata = default;
        _footerVersion = 0;
        _pagePruner = null;
        _disposed = false;
    }

    public ParquetSchema Schema { get; private set; }

    public ParquetFileMetadata Metadata { get; private set; }

    public RowGroupCollection RowGroups
    {
        get
        {
            ThrowIfDisposed();
            return new RowGroupCollection(this, _footerVersion);
        }
    }

    internal ParquetReaderOptions Options { get; }

    internal ParquetPagePruner? PagePruner
        => _pagePruner;

    /// <summary>Opens a source and loads its logical schema and row groups.</summary>
    /// <remarks>
    /// Earlier row group and column handles are invalidated before opening the source. If opening or schema
    /// validation fails, logical metadata is cleared; the reader can be reset again with another source.
    /// </remarks>
    public void Reset(Stream stream, ParquetPagePruner? pagePruner = null)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(stream);
        var previousFooter = InvalidateLogicalState();
        PhysicalReader.Reset(stream);
        ResetLogicalState(previousFooter, pagePruner);
    }

    /// <summary>Opens a source and loads its logical schema and row groups.</summary>
    /// <remarks>
    /// Earlier row group and column handles are invalidated before opening the source. If opening or schema
    /// validation fails, logical metadata is cleared; the reader can be reset again with another source.
    /// </remarks>
    public void Reset(IParquetReadSource source, ParquetPagePruner? pagePruner = null)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(source);
        var previousFooter = InvalidateLogicalState();
        PhysicalReader.Reset(source);
        ResetLogicalState(previousFooter, pagePruner);
    }

    InternalParquetFooter InvalidateLogicalState()
    {
        // Physical reset can replace the source or return its metadata buffers before it throws.
        // Retire logical handles first, while retaining reusable arrays only for a successful bind.
        var previousFooter = _footer;
        _footerVersion++;
        _footer = InternalParquetFooter.Empty;
        Schema = EmptySchema;
        Metadata = default;
        _pagePruner = null;
        return previousFooter;
    }

    void ResetLogicalState(InternalParquetFooter previousFooter, ParquetPagePruner? pagePruner)
    {
        var physicalMetadata = PhysicalReader.Metadata;
        var fileSchema = PhysicalSchemaBinder.BuildSchema(physicalMetadata);
        if (Options.Strict && _requestedSchema is not null)
            ValidateRequestedSchema(_requestedSchema, fileSchema);

        var schema = _requestedSchema ?? fileSchema;
        var footer = PhysicalSchemaBinder.Bind(PhysicalReader, fileSchema, schema, previousFooter, Options.Strict,
            Options.BufferPool, _footerVersion);

        Schema = schema;
        _footer = footer;
        Metadata = new ParquetFileMetadata(fileSchema, physicalMetadata.FooterOffset,
            physicalMetadata.FooterLength, physicalMetadata.FileVersion);
        _pagePruner = pagePruner;
    }

    internal void ValidateRowGroup(RowGroup rowGroup)
    {
        ThrowIfDisposed();
        if (!ReferenceEquals(rowGroup.Reader, this))
            throw new ArgumentException("Row group does not belong to this reader.", nameof(rowGroup));
        if (rowGroup.Index < 0)
            throw new ArgumentOutOfRangeException(nameof(rowGroup), rowGroup.Index,
                "Row group index must be non-negative.");
        if ((uint)rowGroup.Index >= _footer.RowGroupCount)
            throw new ArgumentOutOfRangeException(nameof(rowGroup), rowGroup.Index,
                "Row group index is outside the parsed footer.");
        if (rowGroup.Metadata.FooterVersion != _footerVersion)
            throw new ArgumentException("Row group does not belong to the current reader state.", nameof(rowGroup));
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        PhysicalReader.Dispose();
    }

    void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(ParquetReader));
    }

    internal int GetRowGroupCount(int footerVersion)
    {
        ThrowIfDisposed();
        ValidateFooterVersion(footerVersion);
        return checked((int)_footer.RowGroupCount);
    }

    internal RowGroup GetRowGroup(int index, int footerVersion)
    {
        ThrowIfDisposed();
        ValidateFooterVersion(footerVersion);
        if ((uint)index >= _footer.RowGroupCount)
            throw new ArgumentOutOfRangeException(nameof(index), index,
                "Row group index is outside the parsed footer.");
        return new RowGroup(this, _footer.RowGroups[index]);
    }

    internal void ValidateFooterVersion(int footerVersion)
    {
        ThrowIfDisposed();
        if (footerVersion != _footerVersion)
            throw new InvalidOperationException("The reader handle is stale because the reader was reset.");
    }

    internal int GetColumnOrdinal(LeafColumn column)
    {
        var ordinal = column.Ordinal;
        if ((uint)ordinal < (uint)Schema.LeafColumns.Length &&
            ReferenceEquals(Schema.LeafColumns[ordinal], column))
            return ordinal;

        throw new ArgumentException("Column does not belong to this schema.", nameof(column));
    }

    static void ValidateRequestedSchema(ParquetSchema requestedSchema, ParquetSchema fileSchema)
    {
        var fileColumns = fileSchema.Columns;
        var requestedColumns = requestedSchema.Columns;
        for (var i = 0; i < requestedColumns.Length; i++)
        {
            var requestedColumn = requestedColumns[i];
            var requestedPath = requestedSchema.LeafPaths[i];
            var match = -1;
            for (var fileOrdinal = 0; fileOrdinal < fileColumns.Length; fileOrdinal++)
            {
                if (!PathEquals(fileSchema.LeafPaths[fileOrdinal], requestedPath))
                    continue;
                if (match >= 0)
                    throw new CorruptParquetException(
                        $"File schema contains duplicate leaf path '{requestedColumn.Name}'.");
                match = fileOrdinal;
            }

            if (match < 0)
                throw new InvalidOperationException(
                    $"Requested schema column '{requestedColumn.Name}' is not present in the file schema.");

            ValidateRequestedColumn(requestedColumn, fileColumns[match]);
        }
    }

    static bool PathEquals(System.Collections.Immutable.ImmutableArray<string> left,
        System.Collections.Immutable.ImmutableArray<string> right)
    {
        if (left.Length != right.Length)
            return false;
        for (var i = 0; i < left.Length; i++)
            if (!string.Equals(left[i], right[i], StringComparison.Ordinal))
                return false;
        return true;
    }

    static void ValidateRequestedColumn(Column requestedColumn, Column fileColumn)
    {
        if (requestedColumn.PhysicalType != fileColumn.PhysicalType)
            throw new InvalidOperationException(
                $"Requested schema column '{requestedColumn.Name}' has physical type {requestedColumn.PhysicalType}, but file schema has {fileColumn.PhysicalType}.");

        var requestedRepetition = NormalizeRepetition(requestedColumn.Options.Repetition);
        var fileRepetition = NormalizeRepetition(fileColumn.Options.Repetition);
        if (fileRepetition == ParquetRepetition.Optional && requestedRepetition == ParquetRepetition.Required)
            throw new InvalidOperationException(
                $"Requested schema column '{requestedColumn.Name}' is required, but the file schema column is optional.");
        if ((fileRepetition == ParquetRepetition.Repeated || requestedRepetition == ParquetRepetition.Repeated) &&
            fileRepetition != requestedRepetition)
            throw new InvalidOperationException(
                $"Requested schema column '{requestedColumn.Name}' has repetition {requestedRepetition}, but file schema has {fileRepetition}.");

        if (requestedColumn.PhysicalType == ParquetPhysicalType.FixedLenByteArray &&
            requestedColumn.Options.TypeLength != fileColumn.Options.TypeLength)
            throw new InvalidOperationException(
                $"Requested schema column '{requestedColumn.Name}' has fixed length {requestedColumn.Options.TypeLength}, but file schema has {fileColumn.Options.TypeLength}.");

        if (!AreLogicalTypesCompatible(requestedColumn, fileColumn.LogicalType))
            throw new InvalidOperationException(
                $"Requested schema column '{requestedColumn.Name}' has logical type {DescribeLogicalType(requestedColumn.LogicalType)}, but file schema has {DescribeLogicalType(fileColumn.LogicalType)}.");
    }

    static ParquetRepetition NormalizeRepetition(ParquetRepetition repetition)
        => repetition == ParquetRepetition.Unspecified ? ParquetRepetition.Required : repetition;

    static bool AreLogicalTypesCompatible(Column requestedColumn, LogicalType? fileLogicalType)
    {
        if (Equals(requestedColumn.LogicalType, fileLogicalType))
            return true;

        if (requestedColumn.LogicalType is null && fileLogicalType is LogicalType.Int integer && integer.IsSigned)
            return (requestedColumn.PhysicalType, integer.BitWidth) is
                (ParquetPhysicalType.Int32, 32) or
                (ParquetPhysicalType.Int64, 64);

        return false;
    }

    static string DescribeLogicalType(LogicalType? logicalType)
        => logicalType?.ToString() ?? "none";

    static ParquetSchema RequireRequestedSchema(ParquetSchema schema)
        => schema ?? throw new ArgumentNullException(nameof(schema));
}
