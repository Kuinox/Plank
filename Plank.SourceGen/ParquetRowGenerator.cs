using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Plank.SourceGen;

[Generator]
public sealed class ParquetRowGenerator : IIncrementalGenerator
{
    static readonly DiagnosticDescriptor InvalidTarget = new(
        id: "PLANKGEN001",
        title: "Invalid [ParquetSchema] target",
        messageFormat: "Type '{0}' must be a non-generic class with at least one supported non-static property",
        category: "Plank.SourceGen",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    static readonly DiagnosticDescriptor UnsupportedPropertyType = new(
        id: "PLANKGEN002",
        title: "Unsupported schema column mapping",
        messageFormat: "Column '{0}' on schema '{1}' has unsupported row mapping for repetition '{2}' and physical type '{3}'",
        category: "Plank.SourceGen",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    static readonly DiagnosticDescriptor UnsupportedSchemaDeclaration = new(
        id: "PLANKGEN003",
        title: "Unsupported schema declaration",
        messageFormat: "{0}",
        category: "Plank.SourceGen",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    static readonly DiagnosticDescriptor InvalidTypeHint = new(
        id: "PLANKGEN004",
        title: "Invalid schema column mapping",
        messageFormat: "{0}",
        category: "Plank.SourceGen",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    static readonly DiagnosticDescriptor DuplicateSchemaColumn = new(
        id: "PLANKGEN005",
        title: "Duplicate schema column",
        messageFormat: "{0}",
        category: "Plank.SourceGen",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    static readonly DiagnosticDescriptor InvalidSchemaPhysicalType = new(
        id: "PLANKGEN006",
        title: "Invalid schema physical type",
        messageFormat: "{0}",
        category: "Plank.SourceGen",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    static readonly DiagnosticDescriptor InvalidSchemaRepetition = new(
        id: "PLANKGEN007",
        title: "Invalid schema repetition",
        messageFormat: "{0}",
        category: "Plank.SourceGen",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    static readonly DiagnosticDescriptor MissingDateLogicalType = new(
        id: "PLANKGEN008",
        title: "Missing date logical type",
        messageFormat: "{0}",
        category: "Plank.SourceGen",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    static readonly DiagnosticDescriptor MissingTimeLogicalType = new(
        id: "PLANKGEN009",
        title: "Missing time logical type",
        messageFormat: "{0}",
        category: "Plank.SourceGen",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    static readonly DiagnosticDescriptor MissingTimestampLogicalType = new(
        id: "PLANKGEN010",
        title: "Missing timestamp logical type",
        messageFormat: "{0}",
        category: "Plank.SourceGen",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    static readonly DiagnosticDescriptor LogicalPhysicalMismatch = new(
        id: "PLANKGEN011",
        title: "Logical and physical type mismatch",
        messageFormat: "{0}",
        category: "Plank.SourceGen",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    static readonly DiagnosticDescriptor LogicalClrMismatch = new(
        id: "PLANKGEN012",
        title: "Logical and CLR type mismatch",
        messageFormat: "{0}",
        category: "Plank.SourceGen",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    static readonly DiagnosticDescriptor InvalidLogicalTimeUnit = new(
        id: "PLANKGEN013",
        title: "Invalid logical time unit",
        messageFormat: "{0}",
        category: "Plank.SourceGen",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    static readonly DiagnosticDescriptor InvalidDecimalDefinition = new(
        id: "PLANKGEN014",
        title: "Invalid decimal definition",
        messageFormat: "{0}",
        category: "Plank.SourceGen",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    static readonly DiagnosticDescriptor DecimalPhysicalMismatch = new(
        id: "PLANKGEN015",
        title: "Decimal physical type mismatch",
        messageFormat: "{0}",
        category: "Plank.SourceGen",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var schemaTypes = context.SyntaxProvider.ForAttributeWithMetadataName(
            fullyQualifiedMetadataName: "Plank.Schema.ParquetSchemaAttribute",
            predicate: static (node, _) => node is ClassDeclarationSyntax,
            transform: static (ctx, _) => (INamedTypeSymbol)ctx.TargetSymbol);

        context.RegisterSourceOutput(schemaTypes, static (sourceContext, typeSymbol) => Emit(sourceContext, typeSymbol));
    }

    static void Emit(SourceProductionContext context, INamedTypeSymbol schemaType)
    {
        if (schemaType.TypeKind != TypeKind.Class || schemaType.Arity != 0)
        {
            context.ReportDiagnostic(Diagnostic.Create(InvalidTarget, schemaType.Locations.FirstOrDefault(), schemaType.Name));
            return;
        }

        if (!TryExtractColumns(schemaType, out var columns, out var extractError))
        {
            if (extractError.Length == 0)
            {
                context.ReportDiagnostic(Diagnostic.Create(UnsupportedSchemaDeclaration, schemaType.Locations.FirstOrDefault(),
                    $"Schema type '{schemaType.Name}' does not declare any supported non-static properties."));
                return;
            }

            context.ReportDiagnostic(Diagnostic.Create(InvalidTypeHint, schemaType.Locations.FirstOrDefault(), extractError));
            return;
        }

        var schemaDiagnostics = ValidateSchemaColumns(columns);
        for (var i = 0; i < schemaDiagnostics.Length; i++)
            context.ReportDiagnostic(Diagnostic.Create(schemaDiagnostics[i].Descriptor,
                schemaType.Locations.FirstOrDefault(), schemaDiagnostics[i].Message));
        if (schemaDiagnostics.Length > 0)
            return;

        var mappedColumns = ImmutableArray.CreateBuilder<MappedColumn>(columns.Length);
        foreach (var column in columns)
        {
            if (!TryMapColumn(column, out var mapped))
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    UnsupportedPropertyType,
                    schemaType.Locations.FirstOrDefault(),
                    column.Name,
                    schemaType.Name,
                    column.Repetition,
                    column.PhysicalType));
                return;
            }
            mappedColumns.Add(mapped);
        }

        var source = BuildSource(schemaType, columns, mappedColumns.ToImmutable());
        context.AddSource($"{schemaType.Name}.SchemaApi.g.cs", source);
    }

    static string BuildSource(INamedTypeSymbol schemaType, ImmutableArray<SchemaColumn> schemaColumns,
        ImmutableArray<MappedColumn> columns)
    {
        var namespaceName = schemaType.ContainingNamespace is { IsGlobalNamespace: false }
            ? schemaType.ContainingNamespace.ToDisplayString()
            : null;
        var builder = new StringBuilder();
        builder.AppendLine("// <auto-generated />");
        builder.AppendLine("#nullable enable");
        if (namespaceName is not null)
        {
            builder.Append("namespace ").Append(namespaceName).AppendLine(";");
            builder.AppendLine();
        }

        builder.Append(GetAccessibilityKeyword(schemaType.DeclaredAccessibility)).Append(" partial class ")
            .Append(schemaType.Name).AppendLine();
        builder.AppendLine("{");
        builder.AppendLine("    public static global::Plank.Schema.ParquetSchema Schema { get; } = new([");
        for (var i = 0; i < schemaColumns.Length; i++)
        {
            var schemaColumn = schemaColumns[i];
            builder.Append("        new global::Plank.Schema.Column(\"").Append(Escape(schemaColumn.Name))
                .Append("\", global::Plank.Schema.ParquetPhysicalType.").Append(schemaColumn.PhysicalType)
                .Append(", ").Append(GetColumnOptionsExpression(schemaColumn));
            if (schemaColumn.LogicalType is { } logicalType)
                builder.Append(", ").Append(GetLogicalTypeExpression(logicalType));
            builder.AppendLine("),");
        }
        builder.AppendLine("    ]);");
        builder.AppendLine("    const int DefaultRowBatchSize = 1024;");
        builder.AppendLine();
        builder.AppendLine("    public static Writer CreateRowWriter(global::Plank.Writing.RowGroupWriter rowGroupWriter, global::Plank.Writing.ParquetWriterOptions? options = null)");
        builder.AppendLine("        => new(rowGroupWriter, options ?? global::Plank.Writing.ParquetWriterOptions.Default);");
        builder.AppendLine();
        builder.AppendLine("    public static PipelineWriter CreateRowWriter(global::System.IO.Stream stream, global::Plank.Writing.ParquetWriterOptions? options = null)");
        builder.AppendLine("        => new(stream, options ?? global::Plank.Writing.ParquetWriterOptions.Default);");
        builder.AppendLine();
        builder.AppendLine("    public static PipelineWriter CreateRowWriter(global::System.IO.Stream stream, global::System.Action<int>? onFlush, global::Plank.Writing.ParquetWriterOptions? options = null)");
        builder.AppendLine("        => new(stream, onFlush, options ?? global::Plank.Writing.ParquetWriterOptions.Default);");
        builder.AppendLine();
        builder.AppendLine("    public static PipelineWriter CreateRowWriter(global::System.IO.Stream stream, uint maxParallelism, global::Plank.Writing.ParquetWriterOptions? options = null)");
        builder.AppendLine("        => new(stream, maxParallelism, options ?? global::Plank.Writing.ParquetWriterOptions.Default);");
        builder.AppendLine();
        builder.AppendLine("    public static PipelineWriter CreateRowWriter(global::System.IO.Stream stream, uint maxParallelism, global::System.Action<int>? onFlush, global::Plank.Writing.ParquetWriterOptions? options = null)");
        builder.AppendLine("        => new(stream, maxParallelism, onFlush, options ?? global::Plank.Writing.ParquetWriterOptions.Default);");
        builder.AppendLine();
        builder.AppendLine("    public static RowReader CreateRowReader(global::System.IO.Stream stream, Projection projection = Projection.All, global::Plank.Reading.ParquetReaderOptions? options = null)");
        builder.AppendLine("        => new(stream, projection, options ?? global::Plank.Reading.ParquetReaderOptions.Default);");
        builder.AppendLine();
        builder.AppendLine("    public static RowReader CreateRowReader(global::Plank.Reading.IParquetReadSource source, Projection projection = Projection.All, global::Plank.Reading.ParquetReaderOptions? options = null)");
        builder.AppendLine("        => new(source, projection, options ?? global::Plank.Reading.ParquetReaderOptions.Default);");
        builder.AppendLine();
        builder.AppendLine("    [global::System.Flags]");
        builder.AppendLine("    public enum Projection : ulong");
        builder.AppendLine("    {");
        builder.AppendLine("        None = 0,");
        for (var i = 0; i < columns.Length; i++)
            builder.Append("        ").Append(columns[i].PropertyName).Append(" = 1UL << ").Append(i).AppendLine(",");
        builder.Append("        All = ");
        for (var i = 0; i < columns.Length; i++)
        {
            if (i > 0)
                builder.Append(" | ");
            builder.Append(columns[i].PropertyName);
        }
        builder.AppendLine();
        builder.AppendLine("    }");
        builder.AppendLine();
        builder.AppendLine("    public static SchemaWriter CreateWriter(global::System.IO.Stream stream, global::Plank.Writing.ParquetWriterOptions? options = null)");
        builder.AppendLine("        => new(stream, options ?? global::Plank.Writing.ParquetWriterOptions.Default);");
        builder.AppendLine();
        builder.AppendLine("    public sealed class SchemaWriter");
        builder.AppendLine("    {");
        builder.AppendLine("        readonly global::Plank.Writing.ParquetWriter _writer;");
        builder.AppendLine();
        builder.AppendLine("        internal SchemaWriter(global::System.IO.Stream stream, global::Plank.Writing.ParquetWriterOptions options)");
        builder.AppendLine("        {");
        builder.AppendLine("            _ = stream ?? throw new global::System.ArgumentNullException(nameof(stream));");
        builder.AppendLine("            _ = options ?? throw new global::System.ArgumentNullException(nameof(options));");
        builder.AppendLine("            _writer = Schema.CreateWriter(stream, options);");
        builder.AppendLine("        }");
        builder.AppendLine();
        builder.AppendLine("        public RowGroup StartRowGroup()");
        builder.AppendLine("            => new(_writer.StartRowGroup());");
        builder.AppendLine();
        builder.AppendLine("        public void Reset(global::System.IO.Stream stream)");
        builder.AppendLine("            => _writer.Reset(stream);");
        builder.AppendLine();
        builder.AppendLine("        public void CloseFile()");
        builder.AppendLine("            => _writer.CloseFile();");
        builder.AppendLine("    }");
        builder.AppendLine();
        builder.AppendLine("    public sealed class RowGroup");
        builder.AppendLine("    {");
        builder.AppendLine("        readonly global::Plank.Writing.RowGroupWriter _rowGroupWriter;");
        for (var i = 0; i < columns.Length; i++)
            builder.Append("        readonly global::Plank.Writing.SerializedColumn<")
                .Append(columns[i].ClrTypeName).Append("> _")
                .Append(columns[i].PropertyName).AppendLine(";");
        builder.AppendLine();
        builder.AppendLine("        internal RowGroup(global::Plank.Writing.RowGroupWriter rowGroupWriter)");
        builder.AppendLine("        {");
        builder.AppendLine("            _rowGroupWriter = rowGroupWriter ?? throw new global::System.ArgumentNullException(nameof(rowGroupWriter));");
        for (var i = 0; i < columns.Length; i++)
            builder.Append("            _").Append(columns[i].PropertyName).Append(" = rowGroupWriter.CreateSerializedColumn<")
                .Append(columns[i].ClrTypeName).Append(">(Schema.Columns[").Append(i).Append("]);").AppendLine();
        builder.AppendLine("        }");
        builder.AppendLine();
        for (var i = 0; i < columns.Length; i++)
        {
            builder.Append("        public global::Plank.Writing.SerializedColumn<")
                .Append(columns[i].ClrTypeName).Append("> ")
                .Append(columns[i].PropertyName).Append(" => _").Append(columns[i].PropertyName).AppendLine(";");
            if (i < columns.Length - 1)
                builder.AppendLine();
        }
        builder.AppendLine();
        builder.AppendLine("        public void Write<T>(global::Plank.Writing.SerializedColumn<T> serialized)");
        builder.AppendLine("            => _rowGroupWriter.Write(serialized);");
        builder.AppendLine("    }");
        builder.AppendLine();
        builder.AppendLine("    public struct Writer");
        builder.AppendLine("    {");
        builder.AppendLine("        readonly global::Plank.Writing.RowGroupWriter _rowGroupWriter;");
        builder.AppendLine("        readonly BufferSlot _slot;");
        builder.AppendLine("        bool _written;");

        builder.AppendLine();
        builder.AppendLine("        internal Writer(global::Plank.Writing.RowGroupWriter rowGroupWriter, global::Plank.Writing.ParquetWriterOptions options)");
        builder.AppendLine("        {");
        builder.AppendLine("            _rowGroupWriter = rowGroupWriter ?? throw new global::System.ArgumentNullException(nameof(rowGroupWriter));");
        builder.AppendLine("            options = options ?? throw new global::System.ArgumentNullException(nameof(options));");
        builder.AppendLine("            _slot = new BufferSlot(rowGroupWriter, DefaultRowBatchSize);");
        builder.AppendLine("            _written = false;");
        builder.AppendLine("        }");
        builder.AppendLine();
        builder.AppendLine("        public Row GetRow()");
        builder.AppendLine("        {");
        builder.AppendLine("            if (_written)");
        builder.AppendLine("                throw new global::System.InvalidOperationException(\"Rows are already written for this row group.\");");
        builder.AppendLine("            return _slot.GetRow();");
        builder.AppendLine("        }");
        builder.AppendLine();
        builder.AppendLine("        public void Next()");
        builder.AppendLine("        {");
        builder.AppendLine("            if (_written)");
        builder.AppendLine("                throw new global::System.InvalidOperationException(\"Rows are already written for this row group.\");");
        builder.AppendLine("            _slot.Next();");
        builder.AppendLine("        }");
        builder.AppendLine();
        builder.AppendLine("        public void Write()");
        builder.AppendLine("        {");
        builder.AppendLine("            if (_written)");
        builder.AppendLine("                throw new global::System.InvalidOperationException(\"This row writer was already written.\");");
        builder.AppendLine("            _slot.SerializeColumns();");
        builder.AppendLine("            _slot.WriteSerialized(_rowGroupWriter);");
        builder.AppendLine("            _written = true;");
        builder.AppendLine("        }");
        builder.AppendLine("    }");
        builder.AppendLine();
        builder.AppendLine("    public sealed class PipelineWriter : global::Plank.Writing.RowWriterBase<BufferSlot>");
        builder.AppendLine("    {");
        builder.AppendLine("        readonly int _rowBatchSize;");
        builder.AppendLine("        readonly global::System.Action<int>? _onFlush;");
        builder.AppendLine("        BufferSlot _active;");
        builder.AppendLine("        bool _completed;");
        builder.AppendLine();
        builder.AppendLine("        internal PipelineWriter(global::System.IO.Stream stream, global::Plank.Writing.ParquetWriterOptions options)");
        builder.AppendLine("            : this(stream, options.RowApiMaxParallelism, null, options)");
        builder.AppendLine("        {");
        builder.AppendLine("        }");
        builder.AppendLine();
        builder.AppendLine("        internal PipelineWriter(global::System.IO.Stream stream, global::System.Action<int>? onFlush, global::Plank.Writing.ParquetWriterOptions options)");
        builder.AppendLine("            : this(stream, options.RowApiMaxParallelism, onFlush, options)");
        builder.AppendLine("        {");
        builder.AppendLine("        }");
        builder.AppendLine();
        builder.AppendLine("        internal PipelineWriter(global::System.IO.Stream stream, uint maxParallelism, global::Plank.Writing.ParquetWriterOptions options)");
        builder.AppendLine("            : this(stream, maxParallelism, null, options)");
        builder.AppendLine("        {");
        builder.AppendLine("        }");
        builder.AppendLine();
        builder.AppendLine("        internal PipelineWriter(global::System.IO.Stream stream, uint maxParallelism, global::System.Action<int>? onFlush, global::Plank.Writing.ParquetWriterOptions options)");
        builder.AppendLine("            : base(stream, Schema, maxParallelism, options)");
        builder.AppendLine("        {");
        builder.AppendLine("            _ = stream ?? throw new global::System.ArgumentNullException(nameof(stream));");
        builder.AppendLine("            _ = options ?? throw new global::System.ArgumentNullException(nameof(options));");
        builder.AppendLine("            if (maxParallelism == 0)");
        builder.AppendLine("                throw new global::System.ArgumentOutOfRangeException(nameof(maxParallelism), maxParallelism, \"Max parallelism must be greater than zero.\");");
        builder.AppendLine();
        builder.AppendLine("            _rowBatchSize = DefaultRowBatchSize;");
        builder.AppendLine("            _onFlush = onFlush;");
        builder.AppendLine("            InitializeSlots();");
        builder.AppendLine("            _active = TakeInitialSlot();");
        builder.AppendLine("            _completed = false;");
        builder.AppendLine("        }");
        builder.AppendLine();
        builder.AppendLine("        protected override BufferSlot CreateSlot(global::Plank.Writing.ParquetWriter writer)");
        builder.AppendLine("            => new(writer, _rowBatchSize);");
        builder.AppendLine();
        builder.AppendLine("        protected override void SerializeSlot(BufferSlot slot)");
        builder.AppendLine("            => slot.SerializeColumns();");
        builder.AppendLine();
        builder.AppendLine("        protected override void WriteSerializedSlot(BufferSlot slot, global::Plank.Writing.RowGroupWriter rowGroupWriter)");
        builder.AppendLine("            => slot.WriteSerialized(rowGroupWriter);");
        builder.AppendLine();
        builder.AppendLine("        protected override void OnSlotWritten(BufferSlot slot)");
        builder.AppendLine("            => _onFlush?.Invoke(slot.Count);");
        builder.AppendLine();
        builder.AppendLine("        protected override void ResetSlotForReuse(BufferSlot slot)");
        builder.AppendLine("            => slot.ResetForReuse();");
        builder.AppendLine();
        builder.Append("        protected override string WorkerThreadNamePrefix => \"Plank")
            .Append(Escape(schemaType.Name))
            .AppendLine("RowApiWorker\";");
        builder.AppendLine();
        builder.AppendLine("        public Row GetRow()");
        builder.AppendLine("        {");
        builder.AppendLine("            ThrowIfFaulted();");
        builder.AppendLine("            if (_completed)");
        builder.AppendLine("                throw new global::System.InvalidOperationException(\"Pipeline writer is already completed.\");");
        builder.AppendLine("            return _active.GetRow();");
        builder.AppendLine("        }");
        builder.AppendLine();
        builder.AppendLine("        public void Next()");
        builder.AppendLine("        {");
        builder.AppendLine("            ThrowIfFaulted();");
        builder.AppendLine("            if (_completed)");
        builder.AppendLine("                throw new global::System.InvalidOperationException(\"Pipeline writer is already completed.\");");
        builder.AppendLine();
        builder.AppendLine("            _active.Next();");
        builder.AppendLine("            if (!_active.IsFull)");
        builder.AppendLine("                return;");
        builder.AppendLine();
        builder.AppendLine("            _active = EnqueueAndTakeFree(_active);");
        builder.AppendLine("        }");
        builder.AppendLine();
        builder.AppendLine("        public void Complete()");
        builder.AppendLine("        {");
        builder.AppendLine("            ThrowIfFaulted();");
        builder.AppendLine("            if (_completed)");
        builder.AppendLine("                return;");
        builder.AppendLine();
        builder.AppendLine("            Complete(_active, !_active.IsEmpty);");
        builder.AppendLine("            _completed = true;");
        builder.AppendLine("        }");
        builder.AppendLine("    }");
        builder.AppendLine();
        builder.AppendLine("    public sealed class RowReader : global::System.IDisposable");
        builder.AppendLine("    {");
        builder.AppendLine("        global::Plank.Reading.IParquetReadSource _source;");
        builder.AppendLine("        Projection _projection;");
        builder.AppendLine("        readonly global::Plank.Reading.ParquetReader _reader;");
        builder.AppendLine("        global::Plank.Reading.StreamReadSource? _streamSource;");
        builder.AppendLine("        global::Plank.Reading.RowGroupTokenEnumerable.Enumerator _rowGroups;");
        builder.AppendLine("        readonly global::Plank.Reading.RowGroupReader _rowGroup;");
        builder.AppendLine("        ulong _rowGroupRowsRemaining;");
        builder.AppendLine("        bool _started;");
        builder.AppendLine("        bool _hasCurrent;");
        builder.AppendLine("        bool _disposed;");
        for (var i = 0; i < columns.Length; i++)
        {
            builder.Append("        global::Plank.Reading.ColumnPageEnumerable<").Append(columns[i].ClrTypeName)
                .Append(">.Enumerator _").Append(columns[i].PropertyName).AppendLine("Pages;");
            builder.Append("        global::System.ReadOnlyMemory<").Append(columns[i].ClrTypeName)
                .Append("> _").Append(columns[i].PropertyName).AppendLine("Page;");
            builder.Append("        ").Append(columns[i].ClrTypeName).Append("[] _")
                .Append(columns[i].PropertyName).AppendLine("PageArray;");
            builder.Append("        int _").Append(columns[i].PropertyName).AppendLine("PageIndex;");
            builder.Append("        bool _").Append(columns[i].PropertyName).AppendLine("PagesOpen;");
            builder.Append("        bool _").Append(columns[i].PropertyName).AppendLine("Projected;");
        }
        builder.AppendLine();
        builder.AppendLine("        internal RowReader(global::System.IO.Stream stream, Projection projection, global::Plank.Reading.ParquetReaderOptions options)");
        builder.AppendLine("            : this(new global::Plank.Reading.StreamReadSource(stream), projection, options)");
        builder.AppendLine("        {");
        builder.AppendLine("        }");
        builder.AppendLine();
        builder.AppendLine("        internal RowReader(global::Plank.Reading.IParquetReadSource source, Projection projection, global::Plank.Reading.ParquetReaderOptions options)");
        builder.AppendLine("        {");
        builder.AppendLine("            _source = source ?? throw new global::System.ArgumentNullException(nameof(source));");
        builder.AppendLine("            _ = options ?? throw new global::System.ArgumentNullException(nameof(options));");
        builder.AppendLine("            _projection = projection;");
        builder.AppendLine("            _reader = Schema.CreateReader(source, options);");
        builder.AppendLine("            _streamSource = source as global::Plank.Reading.StreamReadSource;");
        builder.AppendLine("            _rowGroups = default;");
        builder.AppendLine("            _rowGroup = _reader.CreateReusableRowGroupReader();");
        builder.AppendLine("            _rowGroupRowsRemaining = 0;");
        builder.AppendLine("            _started = false;");
        builder.AppendLine("            _hasCurrent = false;");
        builder.AppendLine("            _disposed = false;");
        for (var i = 0; i < columns.Length; i++)
        {
            builder.Append("            _").Append(columns[i].PropertyName).AppendLine("Pages = default;");
            builder.Append("            _").Append(columns[i].PropertyName).AppendLine("Page = default;");
            builder.Append("            _").Append(columns[i].PropertyName).Append("PageArray = global::System.Array.Empty<")
                .Append(columns[i].ClrTypeName).AppendLine(">();");
            builder.Append("            _").Append(columns[i].PropertyName).AppendLine("PageIndex = -1;");
            builder.Append("            _").Append(columns[i].PropertyName).AppendLine("PagesOpen = false;");
            builder.Append("            _").Append(columns[i].PropertyName).Append("Projected = IsProjected(Projection.")
                .Append(columns[i].PropertyName).AppendLine(");");
        }
        builder.AppendLine("        }");
        builder.AppendLine();
        builder.AppendLine("        public Enumerator GetEnumerator()");
        builder.AppendLine("            => new(this);");
        builder.AppendLine();
        builder.AppendLine("        public void Reset(global::System.IO.Stream stream, Projection projection = Projection.All)");
        builder.AppendLine("        {");
        builder.AppendLine("            if (_streamSource is null)");
        builder.AppendLine("                _streamSource = new global::Plank.Reading.StreamReadSource(stream);");
        builder.AppendLine("            else");
        builder.AppendLine("                _streamSource.Reset(stream);");
        builder.AppendLine("            Reset(_streamSource, projection);");
        builder.AppendLine("        }");
        builder.AppendLine();
        builder.AppendLine("        public void Reset(global::Plank.Reading.IParquetReadSource source, Projection projection = Projection.All)");
        builder.AppendLine("        {");
        builder.AppendLine("            ThrowIfDisposed();");
        builder.AppendLine("            _source = source ?? throw new global::System.ArgumentNullException(nameof(source));");
        builder.AppendLine("            _projection = projection;");
        for (var i = 0; i < columns.Length; i++)
            builder.Append("            _").Append(columns[i].PropertyName).Append("Projected = IsProjected(Projection.")
                .Append(columns[i].PropertyName).AppendLine(");");
        builder.AppendLine("            DisposeColumnReaders();");
        builder.AppendLine("            _rowGroup.Dispose();");
        builder.AppendLine("            _reader.Reset(source);");
        builder.AppendLine("            _rowGroups = default;");
        builder.AppendLine("            _rowGroupRowsRemaining = 0;");
        builder.AppendLine("            _started = false;");
        builder.AppendLine("            _hasCurrent = false;");
        for (var i = 0; i < columns.Length; i++)
        {
            builder.Append("            _").Append(columns[i].PropertyName).AppendLine("Pages = default;");
            builder.Append("            _").Append(columns[i].PropertyName).AppendLine("Page = default;");
            builder.Append("            _").Append(columns[i].PropertyName).Append("PageArray = global::System.Array.Empty<")
                .Append(columns[i].ClrTypeName).AppendLine(">();");
            builder.Append("            _").Append(columns[i].PropertyName).AppendLine("PageIndex = -1;");
            builder.Append("            _").Append(columns[i].PropertyName).AppendLine("PagesOpen = false;");
        }
        builder.AppendLine("        }");
        builder.AppendLine();
        builder.AppendLine("        public readonly struct Enumerator : global::System.IDisposable");
        builder.AppendLine("        {");
        builder.AppendLine("            readonly RowReader _reader;");
        builder.AppendLine();
        builder.AppendLine("            internal Enumerator(RowReader reader)");
        builder.AppendLine("                => _reader = reader ?? throw new global::System.ArgumentNullException(nameof(reader));");
        builder.AppendLine();
        builder.AppendLine("            public Row Current => _reader.Current;");
        builder.AppendLine();
        builder.AppendLine("            public bool MoveNext()");
        builder.AppendLine("                => _reader.MoveNext();");
        builder.AppendLine();
        builder.AppendLine("            public void Dispose()");
        builder.AppendLine("            {");
        builder.AppendLine("            }");
        builder.AppendLine("        }");
        builder.AppendLine();
        builder.AppendLine("        struct LocalEnumerator : global::System.IDisposable");
        builder.AppendLine("        {");
        builder.AppendLine("            readonly global::Plank.Reading.IParquetReadSource _source;");
        builder.AppendLine("            readonly Projection _projection;");
        builder.AppendLine("            readonly global::Plank.Reading.ParquetReader _reader;");
        builder.AppendLine("            global::Plank.Reading.RowGroupTokenEnumerable.Enumerator _rowGroups;");
        builder.AppendLine("            global::Plank.Reading.RowGroupReader? _rowGroup;");
        builder.AppendLine("            ulong _rowGroupRowsRemaining;");
        builder.AppendLine("            bool _hasCurrent;");
        builder.AppendLine("            bool _disposed;");
        for (var i = 0; i < columns.Length; i++)
        {
            builder.Append("            global::Plank.Reading.ColumnPageEnumerable<").Append(columns[i].ClrTypeName)
                .Append(">.Enumerator _").Append(columns[i].PropertyName).AppendLine("Pages;");
            builder.Append("            global::System.ReadOnlyMemory<").Append(columns[i].ClrTypeName)
                .Append("> _").Append(columns[i].PropertyName).AppendLine("Page;");
            builder.Append("            ").Append(columns[i].ClrTypeName).Append("[] _")
                .Append(columns[i].PropertyName).AppendLine("PageArray;");
            builder.Append("            int _").Append(columns[i].PropertyName).AppendLine("PageIndex;");
            builder.Append("            bool _").Append(columns[i].PropertyName).AppendLine("PagesOpen;");
            builder.Append("            bool _").Append(columns[i].PropertyName).AppendLine("Projected;");
        }
        builder.AppendLine();
        builder.AppendLine("            internal LocalEnumerator(global::Plank.Reading.IParquetReadSource source, Projection projection, global::Plank.Reading.ParquetReader reader)");
        builder.AppendLine("            {");
        builder.AppendLine("                _source = source ?? throw new global::System.ArgumentNullException(nameof(source));");
        builder.AppendLine("                _reader = reader ?? throw new global::System.ArgumentNullException(nameof(reader));");
        builder.AppendLine("                _projection = projection;");
        builder.AppendLine("                _rowGroups = reader.EnumerateRowGroups().GetEnumerator();");
        builder.AppendLine("                _rowGroup = null;");
        builder.AppendLine("                _rowGroupRowsRemaining = 0;");
        builder.AppendLine("                _hasCurrent = false;");
        builder.AppendLine("                _disposed = false;");
        for (var i = 0; i < columns.Length; i++)
        {
            builder.Append("                _").Append(columns[i].PropertyName).AppendLine("Pages = default;");
            builder.Append("                _").Append(columns[i].PropertyName).AppendLine("Page = default;");
            builder.Append("                _").Append(columns[i].PropertyName).Append("PageArray = global::System.Array.Empty<")
                .Append(columns[i].ClrTypeName).AppendLine(">();");
            builder.Append("                _").Append(columns[i].PropertyName).AppendLine("PageIndex = -1;");
            builder.Append("                _").Append(columns[i].PropertyName).AppendLine("PagesOpen = false;");
            builder.Append("                _").Append(columns[i].PropertyName).Append("Projected = IsProjected(Projection.")
                .Append(columns[i].PropertyName).AppendLine(");");
        }
        builder.AppendLine("            }");
        builder.AppendLine();
        builder.AppendLine("            public readonly Row Current");
        builder.AppendLine("            {");
        builder.AppendLine("                get");
        builder.AppendLine("                {");
        builder.AppendLine("                    ThrowIfDisposed();");
        builder.AppendLine("                    if (!_hasCurrent)");
        builder.AppendLine("                        throw new global::System.InvalidOperationException(\"The row reader is not positioned on a row.\");");
        builder.Append("                    return new Row(");
        for (var i = 0; i < columns.Length; i++)
        {
            if (i > 0)
                builder.Append(", ");
            builder.Append('_').Append(columns[i].PropertyName).Append("PageArray, _")
                .Append(columns[i].PropertyName).Append("PageIndex");
        }
        builder.AppendLine(");");
        builder.AppendLine("                }");
        builder.AppendLine("            }");
        builder.AppendLine();
        builder.AppendLine("            public bool MoveNext()");
        builder.AppendLine("            {");
        builder.AppendLine("                ThrowIfDisposed();");
        builder.AppendLine("                _hasCurrent = ReadNextRow();");
        builder.AppendLine("                return _hasCurrent;");
        builder.AppendLine("            }");
        builder.AppendLine();
        builder.AppendLine("            public void Dispose()");
        builder.AppendLine("            {");
        builder.AppendLine("                if (_disposed)");
        builder.AppendLine("                    return;");
        builder.AppendLine();
        builder.AppendLine("                DisposeColumnReaders();");
        builder.AppendLine("                _rowGroup?.Dispose();");
        builder.AppendLine("                _disposed = true;");
        builder.AppendLine("            }");
        builder.AppendLine();
        builder.AppendLine("            bool ReadNextRow()");
        builder.AppendLine("            {");
        builder.AppendLine("                while (_rowGroupRowsRemaining == 0)");
        builder.AppendLine("                    if (!OpenNextRowGroup())");
        builder.AppendLine("                    {");
        builder.AppendLine("                        _hasCurrent = false;");
        builder.AppendLine("                        return false;");
        builder.AppendLine("                    }");
        builder.AppendLine();
        for (var i = 0; i < columns.Length; i++)
        {
            builder.Append("                if (_").Append(columns[i].PropertyName).AppendLine("Projected)");
            builder.AppendLine("                {");
            AppendAdvanceColumnBody(builder, columns[i], "                    ");
            builder.AppendLine("                }");
        }
        builder.AppendLine("                _rowGroupRowsRemaining--;");
        builder.AppendLine("                return true;");
        builder.AppendLine("            }");
        builder.AppendLine();
        builder.AppendLine("            bool OpenNextRowGroup()");
        builder.AppendLine("            {");
        builder.AppendLine("                while (true)");
        builder.AppendLine("                {");
        builder.AppendLine("                    DisposeColumnReaders();");
        builder.AppendLine("                    _rowGroup?.Dispose();");
        builder.AppendLine("                    if (!_rowGroups.MoveNext())");
        builder.AppendLine("                    {");
        builder.AppendLine("                        _rowGroup = null;");
        builder.AppendLine("                        return false;");
        builder.AppendLine("                    }");
        builder.AppendLine();
        builder.AppendLine("                    _rowGroup = _reader.OpenRowGroup(_source, _rowGroups.Current);");
        builder.AppendLine("                    _rowGroupRowsRemaining = _rowGroup.RowCount;");
        builder.AppendLine("                    if (_rowGroupRowsRemaining == 0)");
        builder.AppendLine("                        continue;");
        for (var i = 0; i < columns.Length; i++)
        {
            builder.Append("                    if (_").Append(columns[i].PropertyName).AppendLine("Projected)");
            builder.AppendLine("                    {");
            builder.Append("                        _").Append(columns[i].PropertyName).Append("Pages = _rowGroup.Column<")
                .Append(columns[i].ClrTypeName).Append(">(Schema.Columns[").Append(i).AppendLine("]).Pages.GetEnumerator();");
            builder.Append("                        _").Append(columns[i].PropertyName).AppendLine("PagesOpen = true;");
            builder.AppendLine("                    }");
            builder.Append("                    _").Append(columns[i].PropertyName).AppendLine("Page = default;");
            builder.Append("                    _").Append(columns[i].PropertyName).Append("PageArray = global::System.Array.Empty<")
                .Append(columns[i].ClrTypeName).AppendLine(">();");
            builder.Append("                    _").Append(columns[i].PropertyName).AppendLine("PageIndex = -1;");
        }
        builder.AppendLine("                    return true;");
        builder.AppendLine("                }");
        builder.AppendLine("            }");
        builder.AppendLine();
        builder.AppendLine("            readonly bool IsProjected(Projection column)");
        builder.AppendLine("                => (_projection & column) != 0;");
        builder.AppendLine();
        builder.AppendLine("            void DisposeColumnReaders()");
        builder.AppendLine("            {");
        for (var i = 0; i < columns.Length; i++)
        {
            builder.Append("                if (_").Append(columns[i].PropertyName).AppendLine("PagesOpen)");
            builder.AppendLine("                {");
            builder.Append("                    _").Append(columns[i].PropertyName).AppendLine("Pages.Dispose();");
            builder.Append("                    _").Append(columns[i].PropertyName).AppendLine("PagesOpen = false;");
            builder.AppendLine("                }");
        }
        builder.AppendLine("            }");
        builder.AppendLine();
        builder.AppendLine("            readonly void ThrowIfDisposed()");
        builder.AppendLine("            {");
        builder.AppendLine("                if (_disposed)");
        builder.AppendLine("                    throw new global::System.ObjectDisposedException(nameof(Enumerator));");
        builder.AppendLine("            }");
        builder.AppendLine("        }");
        builder.AppendLine();
        builder.AppendLine("        public Row Current");
        builder.AppendLine("        {");
        builder.AppendLine("            get");
        builder.AppendLine("            {");
        builder.AppendLine("                ThrowIfDisposed();");
        builder.AppendLine("                if (!_hasCurrent)");
        builder.AppendLine("                    throw new global::System.InvalidOperationException(\"The row reader is not positioned on a row.\");");
        builder.Append("                return new Row(");
        for (var i = 0; i < columns.Length; i++)
        {
            if (i > 0)
                builder.Append(", ");
            builder.Append('_').Append(columns[i].PropertyName).Append("PageArray, _")
                .Append(columns[i].PropertyName).Append("PageIndex");
        }
        builder.AppendLine(");");
        builder.AppendLine("            }");
        builder.AppendLine("        }");
        builder.AppendLine();
        builder.AppendLine("        public bool MoveNext()");
        builder.AppendLine("        {");
        builder.AppendLine("            ThrowIfDisposed();");
        builder.AppendLine("            EnsureStarted();");
        builder.AppendLine("            _hasCurrent = ReadNextRow();");
        builder.AppendLine("            return _hasCurrent;");
        builder.AppendLine("        }");
        builder.AppendLine();
        builder.AppendLine("        public void Dispose()");
        builder.AppendLine("        {");
        builder.AppendLine("            if (_disposed)");
        builder.AppendLine("                return;");
        builder.AppendLine();
        builder.AppendLine("            DisposeColumnReaders();");
        builder.AppendLine("            _rowGroup.Dispose();");
        builder.AppendLine("            _reader.Dispose();");
        builder.AppendLine("            _disposed = true;");
        builder.AppendLine("        }");
        builder.AppendLine();
        builder.AppendLine("        bool ReadNextRow()");
        builder.AppendLine("        {");
        builder.AppendLine("            while (_rowGroupRowsRemaining == 0)");
        builder.AppendLine("                if (!OpenNextRowGroup())");
        builder.AppendLine("                {");
        builder.AppendLine("                    _hasCurrent = false;");
        builder.AppendLine("                    return false;");
        builder.AppendLine("                }");
        builder.AppendLine();
        for (var i = 0; i < columns.Length; i++)
        {
            builder.Append("            if (_").Append(columns[i].PropertyName).AppendLine("Projected)");
            builder.AppendLine("            {");
            AppendAdvanceColumnBody(builder, columns[i], "                ");
            builder.AppendLine("            }");
        }
        builder.AppendLine("            _rowGroupRowsRemaining--;");
        builder.AppendLine("            return true;");
        builder.AppendLine("        }");
        builder.AppendLine();
        builder.AppendLine("        bool OpenNextRowGroup()");
        builder.AppendLine("        {");
        builder.AppendLine("            while (true)");
        builder.AppendLine("            {");
        builder.AppendLine("                DisposeColumnReaders();");
        builder.AppendLine("                _rowGroup.Dispose();");
        builder.AppendLine("                if (!_rowGroups.MoveNext())");
        builder.AppendLine("                {");
        builder.AppendLine("                    return false;");
        builder.AppendLine("                }");
        builder.AppendLine();
        builder.AppendLine("                _reader.OpenRowGroup(_source, _rowGroups.Current, _rowGroup);");
        builder.AppendLine("                _rowGroupRowsRemaining = _rowGroup.RowCount;");
        builder.AppendLine("                if (_rowGroupRowsRemaining == 0)");
        builder.AppendLine("                    continue;");
        for (var i = 0; i < columns.Length; i++)
        {
            builder.Append("                if (_").Append(columns[i].PropertyName).AppendLine("Projected)");
            builder.AppendLine("                {");
            builder.Append("                    _").Append(columns[i].PropertyName).Append("Pages = _rowGroup.Column<")
                .Append(columns[i].ClrTypeName).Append(">(Schema.Columns[").Append(i).AppendLine("]).Pages.GetEnumerator();");
            builder.Append("                    _").Append(columns[i].PropertyName).AppendLine("PagesOpen = true;");
            builder.AppendLine("                }");
            builder.Append("                _").Append(columns[i].PropertyName).AppendLine("Page = default;");
            builder.Append("                _").Append(columns[i].PropertyName).Append("PageArray = global::System.Array.Empty<")
                .Append(columns[i].ClrTypeName).AppendLine(">();");
            builder.Append("                _").Append(columns[i].PropertyName).AppendLine("PageIndex = -1;");
        }
        builder.AppendLine("                return true;");
        builder.AppendLine("            }");
        builder.AppendLine("        }");
        builder.AppendLine();
        builder.AppendLine("        bool IsProjected(Projection column)");
        builder.AppendLine("            => (_projection & column) != 0;");
        builder.AppendLine();
        builder.AppendLine("        void EnsureStarted()");
        builder.AppendLine("        {");
        builder.AppendLine("            if (_started)");
        builder.AppendLine("                return;");
        builder.AppendLine();
        builder.AppendLine("            _rowGroups = _reader.EnumerateRowGroups().GetEnumerator();");
        builder.AppendLine("            _started = true;");
        builder.AppendLine("        }");
        builder.AppendLine();
        builder.AppendLine("        void DisposeColumnReaders()");
        builder.AppendLine("        {");
        for (var i = 0; i < columns.Length; i++)
        {
            builder.Append("            if (_").Append(columns[i].PropertyName).AppendLine("PagesOpen)");
            builder.AppendLine("            {");
            builder.Append("                _").Append(columns[i].PropertyName).AppendLine("Pages.Dispose();");
            builder.Append("                _").Append(columns[i].PropertyName).AppendLine("PagesOpen = false;");
            builder.AppendLine("            }");
        }
        builder.AppendLine("        }");
        builder.AppendLine();
        builder.AppendLine("        void ThrowIfDisposed()");
        builder.AppendLine("        {");
        builder.AppendLine("            if (_disposed)");
        builder.AppendLine("                throw new global::System.ObjectDisposedException(nameof(RowReader));");
        builder.AppendLine("        }");
        builder.AppendLine();
        builder.AppendLine("        static T[] GetArray<T>(global::System.ReadOnlyMemory<T> memory, string columnName)");
        builder.AppendLine("        {");
        builder.AppendLine("            if (global::System.Runtime.InteropServices.MemoryMarshal.TryGetArray(memory, out var segment) && segment.Array is not null)");
        builder.AppendLine("                return segment.Array;");
        builder.AppendLine();
        builder.AppendLine("            throw new global::System.InvalidOperationException($\"Column '{columnName}' page is not array-backed.\");");
        builder.AppendLine("        }");
        builder.AppendLine("    }");
        builder.AppendLine();
        builder.AppendLine("    public sealed class BufferSlot");
        builder.AppendLine("    {");
        builder.AppendLine("        int _index;");
        builder.AppendLine("        readonly int _rowCount;");
        builder.AppendLine("        global::System.Collections.Generic.List<global::System.IDisposable>? _ownedBuffers;");
        for (var i = 0; i < columns.Length; i++)
            builder.Append("        internal readonly ").Append(GetBufferType(columns[i].ClrTypeName)).Append(" _").Append(columns[i].PropertyName).AppendLine(";");
        for (var i = 0; i < columns.Length; i++)
            builder.Append("        readonly global::Plank.Writing.SerializedColumn<").Append(columns[i].ClrTypeName).Append("> _serialized").Append(columns[i].PropertyName).AppendLine(";");
        builder.AppendLine();
        builder.AppendLine("        internal BufferSlot(global::Plank.Writing.RowGroupWriter rowGroupWriter, int rowCount)");
        builder.AppendLine("        {");
        builder.AppendLine("            _ = rowGroupWriter ?? throw new global::System.ArgumentNullException(nameof(rowGroupWriter));");
        builder.AppendLine("            if (rowCount < 0)");
        builder.AppendLine("                throw new global::System.ArgumentOutOfRangeException(nameof(rowCount), rowCount, \"Row count must be non-negative.\");");
        builder.AppendLine();
        builder.AppendLine("            _index = 0;");
        builder.AppendLine("            _rowCount = rowCount;");
        for (var i = 0; i < columns.Length; i++)
            builder.Append("            _").Append(columns[i].PropertyName).Append(" = rowCount == 0 ? global::System.Array.Empty<").Append(columns[i].ClrTypeName).Append(">() : ").Append(GetBufferAllocation(columns[i].ClrTypeName)).AppendLine(";");
        for (var i = 0; i < columns.Length; i++)
            builder.Append("            _serialized").Append(columns[i].PropertyName).Append(" = rowGroupWriter.CreateSerializedColumn<").Append(columns[i].ClrTypeName).Append(">(Schema.Columns[").Append(i).Append("]);").AppendLine();
        builder.AppendLine("        }");
        builder.AppendLine();
        builder.AppendLine("        internal BufferSlot(global::Plank.Writing.ParquetWriter writer, int rowCount)");
        builder.AppendLine("        {");
        builder.AppendLine("            _ = writer ?? throw new global::System.ArgumentNullException(nameof(writer));");
        builder.AppendLine("            if (rowCount < 0)");
        builder.AppendLine("                throw new global::System.ArgumentOutOfRangeException(nameof(rowCount), rowCount, \"Row count must be non-negative.\");");
        builder.AppendLine();
        builder.AppendLine("            _index = 0;");
        builder.AppendLine("            _rowCount = rowCount;");
        for (var i = 0; i < columns.Length; i++)
            builder.Append("            _").Append(columns[i].PropertyName).Append(" = rowCount == 0 ? global::System.Array.Empty<").Append(columns[i].ClrTypeName).Append(">() : ").Append(GetBufferAllocation(columns[i].ClrTypeName)).AppendLine(";");
        for (var i = 0; i < columns.Length; i++)
            builder.Append("            _serialized").Append(columns[i].PropertyName).Append(" = writer.CreateSerializedColumn<").Append(columns[i].ClrTypeName).Append(">(Schema.Columns[").Append(i).Append("]);").AppendLine();
        builder.AppendLine("        }");
        builder.AppendLine();
        builder.AppendLine("        internal bool IsFull => _index == _rowCount;");
        builder.AppendLine();
        builder.AppendLine("        internal bool IsEmpty => _index == 0;");
        builder.AppendLine();
        builder.AppendLine("        internal int Count => _index;");
        builder.AppendLine();
        builder.AppendLine("        internal Row GetRow()");
        builder.AppendLine("        {");
        builder.AppendLine("            if (_index >= _rowCount)");
        builder.AppendLine("                throw new global::System.InvalidOperationException(\"No more row slots are available.\");");
        builder.AppendLine();
        builder.Append("            return new Row(_index, this");
        for (var i = 0; i < columns.Length; i++)
            builder.Append(", _").Append(columns[i].PropertyName);
        builder.AppendLine(");");
        builder.AppendLine("        }");
        builder.AppendLine();
        builder.AppendLine("        internal void Next()");
        builder.AppendLine("        {");
        builder.AppendLine("            if (_index >= _rowCount)");
        builder.AppendLine("                throw new global::System.InvalidOperationException(\"No more row slots are available.\");");
        builder.AppendLine("            _index++;");
        builder.AppendLine("        }");
        builder.AppendLine();
        builder.AppendLine("        internal void SerializeColumns()");
        builder.AppendLine("        {");
        for (var i = 0; i < columns.Length; i++)
            builder.Append("            _serialized").Append(columns[i].PropertyName).Append(".Serialize(new global::System.ReadOnlySpan<").Append(columns[i].ClrTypeName).Append(">(_").Append(columns[i].PropertyName).Append(", 0, _index));").AppendLine();
        builder.AppendLine("        }");
        builder.AppendLine();
        builder.AppendLine("        internal void WriteSerialized(global::Plank.Writing.RowGroupWriter rowGroupWriter)");
        builder.AppendLine("        {");
        for (var i = 0; i < columns.Length; i++)
            builder.Append("            rowGroupWriter.Write(_serialized").Append(columns[i].PropertyName).Append(");").AppendLine();
        builder.AppendLine("        }");
        builder.AppendLine();
        builder.AppendLine("        internal void RegisterOwner(global::System.IDisposable owner)");
        builder.AppendLine("        {");
        builder.AppendLine("            global::System.ArgumentNullException.ThrowIfNull(owner);");
        builder.AppendLine("            (_ownedBuffers ??= new global::System.Collections.Generic.List<global::System.IDisposable>()).Add(owner);");
        builder.AppendLine("        }");
        builder.AppendLine();
        builder.AppendLine("        internal void ResetForReuse()");
        builder.AppendLine("        {");
        builder.AppendLine("            if (_ownedBuffers is not null)");
        builder.AppendLine("            {");
        builder.AppendLine("                for (var i = 0; i < _ownedBuffers.Count; i++)");
        builder.AppendLine("                    _ownedBuffers[i].Dispose();");
        builder.AppendLine("                _ownedBuffers.Clear();");
        builder.AppendLine("            }");
        for (var i = 0; i < columns.Length; i++)
            if (RequiresClearOnReset(columns[i].ClrTypeName))
                builder.Append("            global::System.Array.Clear(_").Append(columns[i].PropertyName).Append(", 0, _index);").AppendLine();
        builder.AppendLine("            _index = 0;");
        builder.AppendLine("        }");
        builder.AppendLine("    }");
        builder.AppendLine();
        builder.AppendLine("    public readonly ref struct Row");
        builder.AppendLine("    {");
        builder.AppendLine("        readonly int _index;");
        builder.AppendLine("        readonly BufferSlot? _ownerSlot;");
        for (var i = 0; i < columns.Length; i++)
            builder.Append("        readonly ").Append(GetBufferType(columns[i].ClrTypeName)).Append(" _").Append(columns[i].PropertyName).AppendLine(";");
        for (var i = 0; i < columns.Length; i++)
            builder.Append("        readonly int _").Append(columns[i].PropertyName).AppendLine("Index;");
        builder.AppendLine();
        builder.Append("        internal Row(int index");
        for (var i = 0; i < columns.Length; i++)
            builder.Append(", ").Append(GetBufferType(columns[i].ClrTypeName)).Append(' ').Append(ToParameterName(columns[i].PropertyName));
        builder.AppendLine(")");
        builder.Append("            : this(index, null");
        for (var i = 0; i < columns.Length; i++)
            builder.Append(", ").Append(ToParameterName(columns[i].PropertyName));
        builder.AppendLine(")");
        builder.AppendLine("        {");
        builder.AppendLine("        }");
        builder.AppendLine();
        builder.Append("        internal Row(");
        for (var i = 0; i < columns.Length; i++)
        {
            if (i > 0)
                builder.Append(", ");
            builder.Append(GetBufferType(columns[i].ClrTypeName)).Append(' ').Append(ToParameterName(columns[i].PropertyName))
                .Append(", int ").Append(ToParameterName(columns[i].PropertyName)).Append("Index");
        }
        builder.AppendLine(")");
        builder.AppendLine("        {");
        builder.AppendLine("            _index = -1;");
        builder.AppendLine("            _ownerSlot = null;");
        for (var i = 0; i < columns.Length; i++)
        {
            builder.Append("            _").Append(columns[i].PropertyName).Append(" = ").Append(ToParameterName(columns[i].PropertyName)).AppendLine(";");
            builder.Append("            _").Append(columns[i].PropertyName).Append("Index = ").Append(ToParameterName(columns[i].PropertyName)).AppendLine("Index;");
        }
        builder.AppendLine("        }");
        builder.AppendLine();
        builder.Append("        internal Row(int index, BufferSlot? ownerSlot");
        for (var i = 0; i < columns.Length; i++)
            builder.Append(", ").Append(GetBufferType(columns[i].ClrTypeName)).Append(' ').Append(ToParameterName(columns[i].PropertyName));
        builder.AppendLine(")");
        builder.AppendLine("        {");
        builder.AppendLine("            _index = index;");
        builder.AppendLine("            _ownerSlot = ownerSlot;");
        for (var i = 0; i < columns.Length; i++)
        {
            builder.Append("            _").Append(columns[i].PropertyName).Append(" = ").Append(ToParameterName(columns[i].PropertyName)).AppendLine(";");
            builder.Append("            _").Append(columns[i].PropertyName).AppendLine("Index = index;");
        }
        builder.AppendLine("        }");
        builder.AppendLine();
        for (var i = 0; i < columns.Length; i++)
        {
            builder.Append("        public ref ").Append(columns[i].ClrTypeName).Append(' ').Append(columns[i].PropertyName).AppendLine();
            builder.AppendLine("        {");
            builder.AppendLine("            get");
            builder.AppendLine("            {");
            builder.Append("                if (_").Append(columns[i].PropertyName).AppendLine(".Length == 0)");
            builder.Append("                    throw new global::System.InvalidOperationException(\"Column '")
                .Append(Escape(columns[i].PropertyName)).AppendLine("' was not selected.\");");
            builder.Append("                return ref _").Append(columns[i].PropertyName).Append("[_").Append(columns[i].PropertyName).AppendLine("Index];");
            builder.AppendLine("            }");
            builder.AppendLine("        }");
            if (SupportsOwnerSetter(columns[i].ClrTypeName))
            {
                builder.AppendLine();
                if (columns[i].ClrTypeName.EndsWith("?", StringComparison.Ordinal))
                {
                    builder.Append("        public void Set").Append(columns[i].PropertyName)
                        .Append("(global::System.Buffers.IMemoryOwner<byte>? owner)").AppendLine();
                    builder.AppendLine("        {");
                    builder.Append("            _").Append(columns[i].PropertyName).Append("[_").Append(columns[i].PropertyName).Append("Index] = owner is null ? default(")
                        .Append(columns[i].ClrTypeName).Append(") : owner.Memory;").AppendLine();
                    builder.AppendLine("            if (owner is not null)");
                    builder.AppendLine("            {");
                    builder.AppendLine("                if (_ownerSlot is null)");
                    builder.AppendLine("                    throw new global::System.InvalidOperationException(\"Owned buffer setters are only available while writing rows.\");");
                    builder.AppendLine("                _ownerSlot.RegisterOwner(owner);");
                    builder.AppendLine("            }");
                    builder.AppendLine("        }");
                }
                else
                {
                    builder.Append("        public void Set").Append(columns[i].PropertyName)
                        .Append("(global::System.Buffers.IMemoryOwner<byte> owner)").AppendLine();
                    builder.AppendLine("        {");
                    builder.AppendLine("            global::System.ArgumentNullException.ThrowIfNull(owner);");
                    builder.Append("            _").Append(columns[i].PropertyName).Append("[_").Append(columns[i].PropertyName).AppendLine("Index] = owner.Memory;");
                    builder.AppendLine("            if (_ownerSlot is null)");
                    builder.AppendLine("                throw new global::System.InvalidOperationException(\"Owned buffer setters are only available while writing rows.\");");
                    builder.AppendLine("            _ownerSlot.RegisterOwner(owner);");
                    builder.AppendLine("        }");
                }
            }
            if (i < columns.Length - 1)
                builder.AppendLine();
        }
        builder.AppendLine("    }");
        builder.AppendLine("}");

        return builder.ToString();
    }

    static bool TryMapColumn(SchemaColumn column, out MappedColumn mapped)
    {
        if (column.Repetition == "Repeated")
        {
            mapped = default;
            return false;
        }

        if (!IsSupportedMapping(column, column.ClrTypeName))
        {
            mapped = default;
            return false;
        }

        mapped = new MappedColumn(column.Name, ToIdentifier(column.RowPropertyName), column.ClrTypeName);
        return true;
    }

    static void AppendAdvanceColumnBody(StringBuilder builder, MappedColumn column, string indent)
    {
        var name = column.PropertyName;
        builder.Append(indent).Append('_').Append(name).AppendLine("PageIndex++;");
        builder.Append(indent).Append("while ((uint)_").Append(name).Append("PageIndex >= (uint)_")
            .Append(name).AppendLine("Page.Length)");
        builder.Append(indent).AppendLine("{");
        builder.Append(indent).Append("    if (!_").Append(name).AppendLine("Pages.MoveNext())");
        builder.Append(indent)
            .Append("        throw new global::System.IO.InvalidDataException(\"Column '")
            .Append(Escape(name)).AppendLine("' ended before the row group was complete.\");");
        builder.AppendLine();
        builder.Append(indent).Append("    _").Append(name).Append("Page = _").Append(name).AppendLine("Pages.Current.Values;");
        builder.Append(indent).Append("    _").Append(name).Append("PageArray = GetArray(_").Append(name).Append("Page, \"")
            .Append(Escape(name)).AppendLine("\");");
        builder.Append(indent).Append("    _").Append(name).AppendLine("PageIndex = 0;");
        builder.Append(indent).Append("    if (_").Append(name).AppendLine("Page.Length == 0)");
        builder.Append(indent).Append("        _").Append(name).AppendLine("PageIndex = -1;");
        builder.Append(indent).AppendLine("}");
    }

    static bool IsSupportedMapping(SchemaColumn column, string clrType)
    {
        if (column.Repetition == "Optional")
            return column.PhysicalType switch
            {
                "Boolean" => clrType is "bool" or "bool?",
                "Int32" => clrType is
                    "byte" or "byte?"
                    or "ushort" or "ushort?"
                    or "int" or "int?"
                    or "uint" or "uint?"
                    or "global::System.DateOnly" or "global::System.DateOnly?",
                "Int64" => clrType is
                    "long" or "long?"
                    or "ulong" or "ulong?"
                    or "global::System.DateTime" or "global::System.DateTime?"
                    or "global::System.DateTimeOffset" or "global::System.DateTimeOffset?"
                    or "global::System.TimeOnly" or "global::System.TimeOnly?",
                "Float" => clrType is "float" or "float?",
                "Double" => clrType is "double" or "double?",
                "ByteArray" => clrType is "string" or "string?" or "byte[]" or "byte[]?" or "global::System.ReadOnlyMemory<byte>" or "global::System.ReadOnlyMemory<byte>?",
                _ => false
            };

        return column.PhysicalType switch
        {
            "Boolean" => clrType == "bool",
            "Int32" => clrType is "byte" or "ushort" or "int" or "uint" or "global::System.DateOnly",
            "Int64" => clrType is
                "long" or "ulong"
                or "global::System.DateTime" or "global::System.DateTimeOffset" or "global::System.TimeOnly",
            "Float" => clrType == "float",
            "Double" => clrType == "double",
            "ByteArray" => clrType is "string" or "byte[]" or "global::System.ReadOnlyMemory<byte>",
            _ => false
        };
    }

    static bool IsSupportedClrType(string clrTypeName)
        => clrTypeName is
            "bool" or "bool?" or
            "byte" or "byte?" or
            "ushort" or "ushort?" or
            "int" or "int?" or
            "uint" or "uint?" or
            "long" or "long?" or
            "ulong" or "ulong?" or
            "float" or "float?" or
            "double" or "double?" or
            "string" or "string?" or
            "byte[]" or "byte[]?" or
            "global::System.ReadOnlyMemory<byte>" or "global::System.ReadOnlyMemory<byte>?" or
            "global::System.DateOnly" or "global::System.DateOnly?" or
            "global::System.DateTime" or "global::System.DateTime?" or
            "global::System.DateTimeOffset" or "global::System.DateTimeOffset?" or
            "global::System.TimeOnly" or "global::System.TimeOnly?";

    static string Escape(string value)
        => value.Replace("\\", "\\\\").Replace("\"", "\\\"");

    static string ToIdentifier(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "_";

        var builder = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            var isValid = i == 0 ? char.IsLetter(c) || c == '_' : char.IsLetterOrDigit(c) || c == '_';
            builder.Append(isValid ? c : '_');
        }
        if (char.IsDigit(builder[0]))
            builder.Insert(0, '_');
        return builder.ToString();
    }

    static string ToParameterName(string propertyName)
        => $"p{propertyName}";

    static string GetAccessibilityKeyword(Accessibility accessibility)
        => accessibility switch
        {
            Accessibility.Public => "public",
            Accessibility.Internal => "internal",
            Accessibility.Private => "private",
            Accessibility.Protected => "protected",
            Accessibility.ProtectedOrInternal => "protected internal",
            Accessibility.ProtectedAndInternal => "private protected",
            _ => "internal"
        };

    static string GetBufferType(string clrTypeName)
        => $"{clrTypeName}[]";

    static string GetBufferAllocation(string clrTypeName)
    {
        if (clrTypeName.EndsWith("[]", StringComparison.Ordinal))
        {
            var elementType = clrTypeName.Substring(0, clrTypeName.Length - 2);
            return $"new {elementType}[rowCount][]";
        }

        return $"new {clrTypeName}[rowCount]";
    }

    static bool SupportsOwnerSetter(string clrTypeName)
        => clrTypeName is "global::System.ReadOnlyMemory<byte>" or "global::System.ReadOnlyMemory<byte>?";

    static bool RequiresClearOnReset(string clrTypeName)
        => clrTypeName is
            "string" or "string?" or
            "byte[]" or "byte[]?" or
            "global::System.ReadOnlyMemory<byte>" or "global::System.ReadOnlyMemory<byte>?";

    static bool TryExtractColumns(INamedTypeSymbol schemaType, out ImmutableArray<SchemaColumn> columns, out string error)
    {
        error = string.Empty;
        var properties = schemaType.GetMembers()
            .OfType<IPropertySymbol>()
            .Where(static p => !p.IsStatic && !p.IsIndexer)
            .OrderBy(static p => p.Locations.FirstOrDefault()?.SourceSpan.Start ?? int.MaxValue)
            .ToImmutableArray();

        if (properties.IsDefaultOrEmpty)
        {
            columns = default;
            return false;
        }

        var builder = ImmutableArray.CreateBuilder<SchemaColumn>(properties.Length);
        foreach (var property in properties)
        {
            if (!TryExtractColumn(property, out var column, out error))
            {
                columns = default;
                return false;
            }

            builder.Add(column);
        }

        columns = builder.ToImmutable();
        return true;
    }

    static bool TryExtractColumn(IPropertySymbol property, out SchemaColumn column, out string error)
    {
        error = string.Empty;
        column = default;

        if (!TryNormalizeClrType(property.Type, property.NullableAnnotation, out var clrTypeName))
        {
            error = $"Unsupported CLR type '{property.Type.ToDisplayString()}' for schema property '{property.Name}'.";
            return false;
        }

        if (!TryInferDefaults(clrTypeName, out var inferredPhysicalType, out var inferredLogicalType))
        {
            error = $"Could not infer parquet mapping for CLR type '{property.Type.ToDisplayString()}' on property '{property.Name}'.";
            return false;
        }

        var columnName = property.Name;
        var physicalType = inferredPhysicalType;
        var logicalType = inferredLogicalType;
        ImmutableArray<string> encodings = [];
        if (!TryReadColumnOverrides(property, ref columnName, ref physicalType, ref logicalType, ref encodings, out error))
            return false;
        if (columnName.Length == 0)
        {
            error = $"Property '{property.Name}' has an empty parquet column name.";
            return false;
        }

        var repetition = IsNullableClrType(clrTypeName) ? "Optional" : "Required";
        column = new SchemaColumn(columnName, physicalType, repetition, clrTypeName, logicalType, property.Name, encodings);
        return true;
    }

    static bool TryReadColumnOverrides(IPropertySymbol property, ref string columnName, ref string physicalType,
        ref LogicalTypeSpec? logicalType, ref ImmutableArray<string> encodings, out string error)
    {
        error = string.Empty;
        var attributes = property.GetAttributes()
            .Where(static a => a.AttributeClass?.ToDisplayString() == "Plank.Schema.ParquetColumnAttribute")
            .ToImmutableArray();

        if (attributes.IsDefaultOrEmpty)
            return true;
        if (attributes.Length > 1)
        {
            error = $"Property '{property.Name}' has multiple [ParquetColumn] attributes.";
            return false;
        }

        var attribute = attributes[0];
        for (var i = 0; i < attribute.ConstructorArguments.Length; i++)
        {
            var parameter = attribute.AttributeConstructor?.Parameters[i];
            var argument = attribute.ConstructorArguments[i];
            if (parameter?.Type.SpecialType == SpecialType.System_String)
            {
                if (argument.Value is string name)
                    columnName = name;
                continue;
            }

            if (parameter?.Type.TypeKind == TypeKind.Enum &&
                parameter.Type.ToDisplayString() == "Plank.Schema.ParquetPhysicalType")
            {
                if (!TryGetPhysicalTypeName(argument, out physicalType))
                {
                    error = $"Property '{property.Name}' declares an invalid ParquetPhysicalType override.";
                    return false;
                }
            }
        }

        foreach (var namedArgument in attribute.NamedArguments)
        {
            if (namedArgument.Key == "Encodings")
            {
                if (!TryGetEncodingNames(namedArgument.Value, out encodings))
                {
                    error = $"Property '{property.Name}' declares an invalid EncodingKind override.";
                    return false;
                }
                continue;
            }

            if (namedArgument.Key == "LogicalType")
            {
                if (!TryGetLogicalTypeSpec(namedArgument.Value, out logicalType))
                {
                    error = $"Property '{property.Name}' declares an invalid LogicalTypeKind override.";
                    return false;
                }
            }
        }

        return true;
    }

    static bool TryGetEncodingNames(TypedConstant constant, out ImmutableArray<string> encodings)
    {
        if (constant.Kind != TypedConstantKind.Array)
        {
            encodings = [];
            return false;
        }

        var builder = ImmutableArray.CreateBuilder<string>(constant.Values.Length);
        for (var i = 0; i < constant.Values.Length; i++)
        {
            if (!TryGetEncodingName(constant.Values[i], out var encoding))
            {
                encodings = [];
                return false;
            }

            builder.Add(encoding);
        }

        encodings = builder.ToImmutable();
        return true;
    }

    static bool TryGetEncodingName(TypedConstant constant, out string encoding)
    {
        encoding = string.Empty;
        if (constant.Value is not int enumValue)
            return false;

        encoding = enumValue switch
        {
            0 => "Plain",
            1 => "PlainDictionary",
            2 => "RleDictionary",
            3 => "Rle",
            4 => "BitPacked",
            5 => "DeltaBinaryPacked",
            6 => "DeltaLengthByteArray",
            7 => "DeltaByteArray",
            8 => "ByteStreamSplit",
            _ => string.Empty
        };
        return encoding.Length > 0;
    }

    static bool TryGetPhysicalTypeName(TypedConstant constant, out string physicalType)
    {
        physicalType = string.Empty;
        if (constant.Value is not int enumValue)
            return false;

        physicalType = enumValue switch
        {
            0 => "Boolean",
            1 => "Int32",
            2 => "Int64",
            3 => "Int96",
            4 => "Float",
            5 => "Double",
            6 => "ByteArray",
            7 => "FixedLenByteArray",
            _ => string.Empty
        };

        return physicalType.Length > 0;
    }

    static bool TryGetLogicalTypeSpec(TypedConstant constant, out LogicalTypeSpec? logicalType)
    {
        logicalType = null;
        if (constant.Value is not int enumValue)
            return false;

        logicalType = enumValue switch
        {
            0 => null,
            1 => new LogicalTypeSpec("String"),
            2 => new LogicalTypeSpec("Json"),
            3 => new LogicalTypeSpec("Uuid"),
            _ => null
        };

        return enumValue is >= 0 and <= 3;
    }

    static bool TryInferDefaults(string clrTypeName, out string physicalType, out LogicalTypeSpec? logicalType)
    {
        logicalType = null;
        var nonNullableType = clrTypeName.TrimEnd('?');
        physicalType = nonNullableType switch
        {
            "bool" => "Boolean",
            "byte" => "Int32",
            "ushort" => "Int32",
            "int" => "Int32",
            "uint" => "Int32",
            "long" => "Int64",
            "ulong" => "Int64",
            "float" => "Float",
            "double" => "Double",
            "string" => "ByteArray",
            "byte[]" => "ByteArray",
            "global::System.ReadOnlyMemory<byte>" => "ByteArray",
            "global::System.DateOnly" => "Int32",
            "global::System.DateTime" => "Int64",
            "global::System.DateTimeOffset" => "Int64",
            "global::System.TimeOnly" => "Int64",
            _ => string.Empty
        };

        if (physicalType.Length == 0)
            return false;

        logicalType = nonNullableType switch
        {
            "byte" => new LogicalTypeSpec("Int", bitWidth: 8, isSigned: false),
            "ushort" => new LogicalTypeSpec("Int", bitWidth: 16, isSigned: false),
            "uint" => new LogicalTypeSpec("Int", bitWidth: 32, isSigned: false),
            "ulong" => new LogicalTypeSpec("Int", bitWidth: 64, isSigned: false),
            "global::System.DateOnly" => new LogicalTypeSpec("Date"),
            "global::System.TimeOnly" => new LogicalTypeSpec("Time", unit: "Micros", isAdjustedToUtc: false),
            "global::System.DateTime" => new LogicalTypeSpec("Timestamp", unit: "Micros", isAdjustedToUtc: true),
            "global::System.DateTimeOffset" => new LogicalTypeSpec("Timestamp", unit: "Micros", isAdjustedToUtc: true),
            _ => null
        };
        return true;
    }

    static string GetLogicalTypeExpression(LogicalTypeSpec logicalType)
        => logicalType.Kind switch
        {
            "Int" => $"new global::Plank.Schema.LogicalType.Int({logicalType.BitWidth.GetValueOrDefault()}, {ToBoolLiteral(logicalType.IsSigned)})",
            "Date" => "new global::Plank.Schema.LogicalType.Date()",
            "Time" => $"new global::Plank.Schema.LogicalType.Time(global::Plank.Schema.TimeUnit.{logicalType.Unit}, {ToBoolLiteral(logicalType.IsAdjustedToUtc)})",
            "Timestamp" => $"new global::Plank.Schema.LogicalType.Timestamp(global::Plank.Schema.TimeUnit.{logicalType.Unit}, {ToBoolLiteral(logicalType.IsAdjustedToUtc)})",
            "String" => "new global::Plank.Schema.LogicalType.String()",
            "Json" => "new global::Plank.Schema.LogicalType.Json()",
            "Uuid" => "new global::Plank.Schema.LogicalType.Uuid()",
            "Decimal" => $"new global::Plank.Schema.LogicalType.Decimal({logicalType.Precision.GetValueOrDefault()}, {logicalType.Scale.GetValueOrDefault()})",
            _ => "null!"
        };

    static string ToBoolLiteral(bool? value)
        => value == true ? "true" : "false";

    static bool IsNullableClrType(string clrTypeName)
        => clrTypeName.EndsWith("?", StringComparison.Ordinal);

    static ImmutableArray<SchemaDiagnostic> ValidateSchemaColumns(ImmutableArray<SchemaColumn> columns)
    {
        var diagnostics = ImmutableArray.CreateBuilder<SchemaDiagnostic>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < columns.Length; i++)
        {
            var column = columns[i];
            if (!seen.Add(column.Name))
                diagnostics.Add(new SchemaDiagnostic(DuplicateSchemaColumn,
                    $"Duplicate column name '{column.Name}' is not allowed."));

            if (!IsSupportedPhysicalType(column.PhysicalType))
                diagnostics.Add(new SchemaDiagnostic(InvalidSchemaPhysicalType,
                    $"Column '{column.Name}' has unsupported physical type '{column.PhysicalType}'."));

            if (!IsSupportedRepetition(column.Repetition))
                diagnostics.Add(new SchemaDiagnostic(InvalidSchemaRepetition,
                    $"Column '{column.Name}' has unsupported repetition '{column.Repetition}'."));

            ValidateLogicalType(column, diagnostics);
        }

        return diagnostics.ToImmutable();
    }

    static bool IsSupportedPhysicalType(string physicalType)
        => physicalType is "Boolean" or "Int32" or "Int64" or "Float" or "Double" or "ByteArray" or "Int96" or "FixedLenByteArray";

    static bool IsSupportedRepetition(string repetition)
        => repetition is "Unspecified" or "Required" or "Optional" or "Repeated";

    static void ValidateLogicalType(SchemaColumn column, ImmutableArray<SchemaDiagnostic>.Builder diagnostics)
    {
        var logicalType = column.LogicalType;
        if (logicalType is null)
        {
            if (IsUnsignedIntClr(column.ClrTypeName, GetUnsignedBitWidth(column.ClrTypeName)))
                diagnostics.Add(new SchemaDiagnostic(LogicalClrMismatch,
                    $"Column '{column.Name}' uses an unsigned CLR integer and must declare logical type 'Int' with IsSigned=false."));
            if (IsDateOnly(column.ClrTypeName))
                diagnostics.Add(new SchemaDiagnostic(MissingDateLogicalType,
                    $"Column '{column.Name}' uses DateOnly and must declare logical type 'Date'."));
            if (IsTimeOnly(column.ClrTypeName))
                diagnostics.Add(new SchemaDiagnostic(MissingTimeLogicalType,
                    $"Column '{column.Name}' uses TimeOnly and must declare logical type 'Time'."));
            if (IsTimestampClr(column.ClrTypeName))
                diagnostics.Add(new SchemaDiagnostic(MissingTimestampLogicalType,
                    $"Column '{column.Name}' uses DateTime/DateTimeOffset and must declare logical type 'Timestamp'."));
            return;
        }

        switch (logicalType.Value.Kind)
        {
            case "Int":
                if (logicalType.Value.BitWidth is not (8 or 16 or 32 or 64))
                    diagnostics.Add(new SchemaDiagnostic(InvalidTypeHint,
                        $"Column '{column.Name}' logical type 'Int' requires bit width 8, 16, 32, or 64."));
                if (logicalType.Value.IsSigned != false)
                    diagnostics.Add(new SchemaDiagnostic(LogicalClrMismatch,
                        $"Column '{column.Name}' unsigned CLR type requires logical type 'Int' with IsSigned=false."));
                if (logicalType.Value.BitWidth is 8 or 16 or 32)
                {
                    if (column.PhysicalType != "Int32")
                        diagnostics.Add(new SchemaDiagnostic(LogicalPhysicalMismatch,
                            $"Column '{column.Name}' logical type 'Int({logicalType.Value.BitWidth},false)' requires physical type 'Int32'."));
                }
                else if (column.PhysicalType != "Int64")
                    diagnostics.Add(new SchemaDiagnostic(LogicalPhysicalMismatch,
                        $"Column '{column.Name}' logical type 'Int(64,false)' requires physical type 'Int64'."));
                if (!IsUnsignedIntClr(column.ClrTypeName, logicalType.Value.BitWidth))
                    diagnostics.Add(new SchemaDiagnostic(LogicalClrMismatch,
                        $"Column '{column.Name}' logical type 'Int({logicalType.Value.BitWidth},false)' requires matching unsigned CLR type."));
                break;
            case "Date":
                if (column.PhysicalType != "Int32")
                    diagnostics.Add(new SchemaDiagnostic(LogicalPhysicalMismatch,
                        $"Column '{column.Name}' logical type 'Date' requires physical type 'Int32'."));
                if (!IsDateOnly(column.ClrTypeName))
                    diagnostics.Add(new SchemaDiagnostic(LogicalClrMismatch,
                        $"Column '{column.Name}' logical type 'Date' requires CLR type DateOnly/DateOnly?."));
                break;
            case "Time":
                if (logicalType.Value.Unit is null || !IsTimeUnit(logicalType.Value.Unit))
                    diagnostics.Add(new SchemaDiagnostic(InvalidLogicalTimeUnit,
                        $"Column '{column.Name}' logical type 'Time' requires a valid unit (Millis/Micros/Nanos)."));
                if (logicalType.Value.Unit == "Millis")
                {
                    if (column.PhysicalType != "Int32")
                        diagnostics.Add(new SchemaDiagnostic(LogicalPhysicalMismatch,
                            $"Column '{column.Name}' logical type 'Time(Millis)' requires physical type 'Int32'."));
                }
                else if (column.PhysicalType != "Int64")
                    diagnostics.Add(new SchemaDiagnostic(LogicalPhysicalMismatch,
                        $"Column '{column.Name}' logical type 'Time({logicalType.Value.Unit})' requires physical type 'Int64'."));
                if (!IsTimeOnly(column.ClrTypeName))
                    diagnostics.Add(new SchemaDiagnostic(LogicalClrMismatch,
                        $"Column '{column.Name}' logical type 'Time' requires CLR type TimeOnly/TimeOnly?."));
                break;
            case "Timestamp":
                if (logicalType.Value.Unit is null || !IsTimeUnit(logicalType.Value.Unit))
                    diagnostics.Add(new SchemaDiagnostic(InvalidLogicalTimeUnit,
                        $"Column '{column.Name}' logical type 'Timestamp' requires a valid unit (Millis/Micros/Nanos)."));
                if (column.PhysicalType != "Int64")
                    diagnostics.Add(new SchemaDiagnostic(LogicalPhysicalMismatch,
                        $"Column '{column.Name}' logical type 'Timestamp' requires physical type 'Int64'."));
                if (!IsTimestampClr(column.ClrTypeName))
                    diagnostics.Add(new SchemaDiagnostic(LogicalClrMismatch,
                        $"Column '{column.Name}' logical type 'Timestamp' requires CLR type DateTime/DateTimeOffset (nullable allowed)."));
                break;
            case "String":
            case "Json":
                if (column.PhysicalType != "ByteArray")
                    diagnostics.Add(new SchemaDiagnostic(LogicalPhysicalMismatch,
                        $"Column '{column.Name}' logical type '{logicalType.Value.Kind}' requires physical type 'ByteArray'."));
                if (!IsUtf8ByteArrayClr(column.ClrTypeName) && !IsStringClr(column.ClrTypeName))
                    diagnostics.Add(new SchemaDiagnostic(LogicalClrMismatch,
                        $"Column '{column.Name}' logical type '{logicalType.Value.Kind}' requires CLR type string/string? or ReadOnlyMemory<byte>/ReadOnlyMemory<byte>?/byte[]/byte[]?."));
                break;
            case "Uuid":
                if (column.PhysicalType != "FixedLenByteArray")
                    diagnostics.Add(new SchemaDiagnostic(LogicalPhysicalMismatch,
                        $"Column '{column.Name}' logical type 'Uuid' requires physical type 'FixedLenByteArray'."));
                if (!IsUtf8ByteArrayClr(column.ClrTypeName))
                    diagnostics.Add(new SchemaDiagnostic(LogicalClrMismatch,
                        $"Column '{column.Name}' logical type 'Uuid' requires CLR type ReadOnlyMemory<byte>/ReadOnlyMemory<byte>?/byte[]/byte[]?."));
                break;
            case "Decimal":
                if (logicalType.Value.Precision is not int precision || precision <= 0)
                    diagnostics.Add(new SchemaDiagnostic(InvalidDecimalDefinition,
                        $"Column '{column.Name}' decimal precision must be positive."));
                if (logicalType.Value.Scale is not int scale || scale < 0)
                    diagnostics.Add(new SchemaDiagnostic(InvalidDecimalDefinition,
                        $"Column '{column.Name}' decimal scale must be non-negative."));
                if (logicalType.Value.Precision is int p && logicalType.Value.Scale is int s && s > p)
                    diagnostics.Add(new SchemaDiagnostic(InvalidDecimalDefinition,
                        $"Column '{column.Name}' decimal scale ({s}) must be <= precision ({p})."));
                if (column.PhysicalType is not ("Int32" or "Int64" or "FixedLenByteArray" or "ByteArray"))
                    diagnostics.Add(new SchemaDiagnostic(DecimalPhysicalMismatch,
                        $"Column '{column.Name}' logical type 'Decimal' is incompatible with physical type '{column.PhysicalType}'."));
                break;
        }
    }

    static bool IsDateOnly(string clrType)
        => clrType is "global::System.DateOnly" or "global::System.DateOnly?";

    static bool IsTimeOnly(string clrType)
        => clrType is "global::System.TimeOnly" or "global::System.TimeOnly?";

    static bool IsUnsignedIntClr(string clrType, int? bitWidth)
        => (clrType, bitWidth) switch
        {
            ("byte", 8) or ("byte?", 8) => true,
            ("ushort", 16) or ("ushort?", 16) => true,
            ("uint", 32) or ("uint?", 32) => true,
            ("ulong", 64) or ("ulong?", 64) => true,
            _ => false
        };

    static int? GetUnsignedBitWidth(string clrType)
        => clrType switch
        {
            "byte" or "byte?" => 8,
            "ushort" or "ushort?" => 16,
            "uint" or "uint?" => 32,
            "ulong" or "ulong?" => 64,
            _ => null
        };

    static bool IsTimestampClr(string clrType)
        => clrType is
            "global::System.DateTime" or "global::System.DateTime?"
            or "global::System.DateTimeOffset" or "global::System.DateTimeOffset?";

    static bool IsStringClr(string clrType)
        => clrType is "string" or "string?";

    static bool IsUtf8ByteArrayClr(string clrType)
        => clrType is
            "byte[]" or "byte[]?" or
            "global::System.ReadOnlyMemory<byte>" or "global::System.ReadOnlyMemory<byte>?";

    static bool IsTimeUnit(string unit)
        => unit is "Millis" or "Micros" or "Nanos";

    static bool TryNormalizeClrType(ITypeSymbol typeSymbol, NullableAnnotation nullableAnnotation, out string clrTypeName)
    {
        var isNullable = false;
        if (typeSymbol is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullableType)
        {
            isNullable = true;
            typeSymbol = nullableType.TypeArguments[0];
        }

        clrTypeName = typeSymbol.SpecialType switch
        {
            SpecialType.System_Boolean => isNullable ? "bool?" : "bool",
            SpecialType.System_Byte => isNullable ? "byte?" : "byte",
            SpecialType.System_UInt16 => isNullable ? "ushort?" : "ushort",
            SpecialType.System_Int32 => isNullable ? "int?" : "int",
            SpecialType.System_UInt32 => isNullable ? "uint?" : "uint",
            SpecialType.System_Int64 => isNullable ? "long?" : "long",
            SpecialType.System_UInt64 => isNullable ? "ulong?" : "ulong",
            SpecialType.System_Single => isNullable ? "float?" : "float",
            SpecialType.System_Double => isNullable ? "double?" : "double",
            SpecialType.System_String => nullableAnnotation == NullableAnnotation.Annotated ? "string?" : "string",
            _ => string.Empty
        };
        if (clrTypeName.Length > 0)
            return IsSupportedClrType(clrTypeName);

        if (typeSymbol is IArrayTypeSymbol { ElementType.SpecialType: SpecialType.System_Byte, Rank: 1 })
        {
            clrTypeName = nullableAnnotation == NullableAnnotation.Annotated ? "byte[]?" : "byte[]";
            return IsSupportedClrType(clrTypeName);
        }

        if (typeSymbol is INamedTypeSymbol namedType &&
            namedType.ContainingNamespace.ToDisplayString() == "System" &&
            namedType.Name == "ReadOnlyMemory" &&
            namedType.TypeArguments.Length == 1 &&
            namedType.TypeArguments[0].SpecialType == SpecialType.System_Byte)
        {
            clrTypeName = isNullable ? "global::System.ReadOnlyMemory<byte>?" : "global::System.ReadOnlyMemory<byte>";
            return IsSupportedClrType(clrTypeName);
        }

        var displayName = typeSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        clrTypeName = displayName switch
        {
            "global::System.DateOnly" => isNullable ? "global::System.DateOnly?" : "global::System.DateOnly",
            "global::System.DateTime" => isNullable ? "global::System.DateTime?" : "global::System.DateTime",
            "global::System.DateTimeOffset" => isNullable ? "global::System.DateTimeOffset?" : "global::System.DateTimeOffset",
            "global::System.TimeOnly" => isNullable ? "global::System.TimeOnly?" : "global::System.TimeOnly",
            _ => string.Empty
        };

        return clrTypeName.Length > 0 && IsSupportedClrType(clrTypeName);
    }

    static string GetColumnOptionsExpression(SchemaColumn column)
    {
        var builder = new StringBuilder();
        builder.Append("new global::Plank.Schema.ColumnOptions(global::Plank.Schema.ParquetRepetition.")
            .Append(column.Repetition);
        if (!column.Encodings.IsDefaultOrEmpty)
        {
            builder.Append(", global::System.Collections.Immutable.ImmutableArray.Create(");
            for (var i = 0; i < column.Encodings.Length; i++)
            {
                if (i > 0)
                    builder.Append(", ");

                builder.Append("global::Plank.Schema.EncodingKind.")
                    .Append(column.Encodings[i]);
            }

            builder.Append(')');
        }

        builder.Append(')');
        return builder.ToString();
    }

    readonly struct SchemaColumn
    {
        public SchemaColumn(string name, string physicalType, string repetition, string clrTypeName,
            LogicalTypeSpec? logicalType, string rowPropertyName, ImmutableArray<string> encodings)
        {
            Name = name;
            PhysicalType = physicalType;
            Repetition = repetition;
            ClrTypeName = clrTypeName;
            LogicalType = logicalType;
            RowPropertyName = rowPropertyName;
            Encodings = encodings;
        }

        public string Name { get; }

        public string PhysicalType { get; }

        public string Repetition { get; }

        public string ClrTypeName { get; }

        public LogicalTypeSpec? LogicalType { get; }

        public string RowPropertyName { get; }

        public ImmutableArray<string> Encodings { get; }
    }

    readonly struct LogicalTypeSpec
    {
        public LogicalTypeSpec(string kind, string? unit = null, bool? isAdjustedToUtc = null, int? precision = null,
            int? scale = null, int? bitWidth = null, bool? isSigned = null)
        {
            Kind = kind;
            Unit = unit;
            IsAdjustedToUtc = isAdjustedToUtc;
            Precision = precision;
            Scale = scale;
            BitWidth = bitWidth;
            IsSigned = isSigned;
        }

        public string Kind { get; }

        public string? Unit { get; }

        public bool? IsAdjustedToUtc { get; }

        public int? Precision { get; }

        public int? Scale { get; }

        public int? BitWidth { get; }

        public bool? IsSigned { get; }
    }

    readonly struct SchemaDiagnostic
    {
        public SchemaDiagnostic(DiagnosticDescriptor descriptor, string message)
        {
            Descriptor = descriptor;
            Message = message;
        }

        public DiagnosticDescriptor Descriptor { get; }

        public string Message { get; }
    }

    readonly struct MappedColumn
    {
        public MappedColumn(string name, string propertyName, string clrTypeName)
        {
            Name = name;
            PropertyName = propertyName;
            ClrTypeName = clrTypeName;
        }

        public string Name { get; }

        public string PropertyName { get; }

        public string ClrTypeName { get; }
    }
}
