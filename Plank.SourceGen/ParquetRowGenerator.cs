using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
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

    internal static readonly DiagnosticDescriptor UnsupportedSchemaDeclaration = new(
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

    internal static readonly DiagnosticDescriptor AllocatingValueNotAllowed = new(
        id: "PLANKGEN016",
        title: "Allocating schema value requires opt-in",
        messageFormat: "{0}",
        category: "Plank.SourceGen",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    static readonly DiagnosticDescriptor InvalidEncoding = new(
        id: "PLANKGEN017",
        title: "Invalid schema column encoding",
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

        if (NestedParquetRowEmitter.TryEmit(context, schemaType))
            return;

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

        if (!AllowsAllocatingValues(schemaType))
        {
            foreach (var column in columns)
            {
                if (!IsStringClr(column.ClrTypeName))
                    continue;

                context.ReportDiagnostic(Diagnostic.Create(AllocatingValueNotAllowed,
                    schemaType.Locations.FirstOrDefault(),
                    $"Column '{column.Name}' uses string, which allocates during UTF-8 conversion. " +
                    "Set [ParquetSchema(AllowAllocatingValues = true)] to opt in."));
                return;
            }
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
        context.AddSource(GetHintName(schemaType), source);
    }

    static string GetHintName(INamedTypeSymbol schemaType)
    {
        var typeName = schemaType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        using var sha256 = SHA256.Create();
        var hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(typeName));
        const string hex = "0123456789abcdef";
        var builder = new StringBuilder(hash.Length * 2 + ".SchemaApi.g.cs".Length);
        for (var i = 0; i < hash.Length; i++)
            builder.Append(hex[hash[i] >> 4]).Append(hex[hash[i] & 0xf]);
        return builder.Append(".SchemaApi.g.cs").ToString();
    }

    static string BuildSource(INamedTypeSymbol schemaType, ImmutableArray<SchemaColumn> schemaColumns,
        ImmutableArray<MappedColumn> columns)
    {
        var schemaMemberName = GetAvailableGeneratedMemberName(schemaType, "Schema");
        var writerTypeName = GetAvailableGeneratedMemberName(schemaType, "Writer");
        var readerTypeName = GetAvailableGeneratedMemberName(schemaType, "Reader");
        var datasetWriterTypeName = GetAvailableGeneratedMemberName(schemaType, "DatasetWriter");
        var routeTypeName = GetAvailableGeneratedMemberName(schemaType, "Route");
        var rowTypeName = EscapeIdentifier(schemaType.Name);
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
            .Append(EscapeIdentifier(schemaType.Name)).AppendLine();
        builder.AppendLine("{");
        builder.Append("    public static global::Plank.Schema.ParquetSchema ").Append(schemaMemberName)
            .AppendLine(" { get; } = new([");
        for (var i = 0; i < schemaColumns.Length; i++)
        {
            var schemaColumn = schemaColumns[i];
            builder.Append("        global::Plank.Schema.ColumnDefinition.Leaf(\"").Append(Escape(schemaColumn.Name))
                .Append("\", global::Plank.Schema.ParquetPhysicalType.").Append(schemaColumn.PhysicalType)
                .Append(", ").Append(GetColumnOptionsExpression(schemaColumn));
            if (schemaColumn.LogicalType is { } logicalType)
                builder.Append(", ").Append(GetLogicalTypeExpression(logicalType));
            if (schemaColumn.ConverterTypeName is { } converterTypeName)
                builder.Append(", converter: new ").Append(converterTypeName).Append("()");
            if (schemaColumn.FieldId is { } fieldId)
                builder.Append(") with { FieldId = ").Append(fieldId).Append(" },");
            else
                builder.Append("),");
            builder.AppendLine();
        }
        builder.AppendLine("    ]);");
        builder.AppendLine("    const int DefaultRowBatchSize = 1024;");
        builder.AppendLine();
        builder.Append("    public delegate global::System.ReadOnlySpan<byte> ").Append(routeTypeName).Append('(')
            .Append(rowTypeName)
            .AppendLine(" row, global::Plank.IParquetBufferPool bufferPool, out global::Plank.ParquetBuffer? allocation);");
        builder.AppendLine();
        builder.Append("    public static ").Append(datasetWriterTypeName).Append(" CreateDatasetWriter<TFile>(")
            .Append(routeTypeName)
            .AppendLine(" route, TFile[] files, global::Plank.Dataset.DatasetWriterOptions? options = null)");
        builder.AppendLine("        where TFile : class, global::Plank.Reading.IParquetReadSource, global::Plank.Writing.IParquetWriteSource");
        builder.AppendLine("        => new(route ?? throw new global::System.ArgumentNullException(nameof(route)),");
        builder.AppendLine("            files ?? throw new global::System.ArgumentNullException(nameof(files)),");
        builder.AppendLine("            options ?? global::Plank.Dataset.DatasetWriterOptions.Default);");
        builder.AppendLine();
        builder.Append("    public static ").Append(writerTypeName)
            .AppendLine(" CreateRowWriter(global::Plank.Writing.RowGroupWriter rowGroupWriter, global::Plank.Writing.ParquetWriterOptions? options = null)");
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
        builder.AppendLine("    public static RowReader CreateRowReader(global::System.IO.Stream stream, Projection projection = default, global::Plank.RowApi.RowReaderOptions? options = null, global::Plank.Reading.ParquetSchemaEvolutionOptions? schemaEvolution = null)");
        builder.AppendLine("        => new(stream, projection, options ?? global::Plank.RowApi.RowReaderOptions.Default, schemaEvolution);");
        builder.AppendLine();
        builder.AppendLine("    public static RowReader CreateRowReader(global::Plank.Reading.IParquetReadSource source, Projection projection = default, global::Plank.RowApi.RowReaderOptions? options = null, global::Plank.Reading.ParquetSchemaEvolutionOptions? schemaEvolution = null)");
        builder.AppendLine("        => new(source, projection, options ?? global::Plank.RowApi.RowReaderOptions.Default, schemaEvolution);");
        builder.AppendLine();
        builder.Append("    public static ").Append(readerTypeName)
            .AppendLine(" CreateReader(global::System.IO.Stream stream, global::Plank.Reading.Logical.ParquetReaderOptions? options = null, global::Plank.Reading.Logical.ParquetPagePruner? pagePruner = null)");
        builder.Append("        => new(").Append(schemaMemberName).AppendLine(".CreateReader(stream, options, pagePruner));");
        builder.AppendLine();
        builder.Append("    public static ").Append(readerTypeName)
            .AppendLine(" CreateReader(global::Plank.Reading.IParquetReadSource source, global::Plank.Reading.Logical.ParquetReaderOptions? options = null, global::Plank.Reading.Logical.ParquetPagePruner? pagePruner = null)");
        builder.Append("        => new(").Append(schemaMemberName).AppendLine(".CreateReader(source, options, pagePruner));");
        builder.AppendLine();
        AppendColumnReader(builder, columns, schemaMemberName, readerTypeName);
        builder.AppendLine();
        AppendRowApiProjection(builder, columns);
        builder.AppendLine();
        AppendRowApiColumnDescriptors(builder, columns, schemaMemberName);
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
        builder.Append("            _writer = ").Append(schemaMemberName).AppendLine(".CreateWriter(stream, options);");
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
                .Append(columns[i].ClrTypeName).Append(">(").Append(schemaMemberName).Append(".LeafColumns[")
                .Append(i).Append("]);").AppendLine();
        builder.AppendLine("        }");
        builder.AppendLine();
        for (var i = 0; i < columns.Length; i++)
        {
            builder.Append("        public global::Plank.Writing.SerializedColumn<")
                .Append(columns[i].ClrTypeName).Append("> ")
                .Append(EscapeIdentifier(columns[i].PropertyName)).Append(" => _").Append(columns[i].PropertyName).AppendLine(";");
            if (i < columns.Length - 1)
                builder.AppendLine();
        }
        builder.AppendLine();
        builder.AppendLine("        public void Write<T>(global::Plank.Writing.SerializedColumn<T> serialized)");
        builder.AppendLine("            => _rowGroupWriter.Write(serialized);");
        builder.AppendLine("    }");
        builder.AppendLine();
        builder.Append("    public struct ").AppendLine(writerTypeName);
        builder.AppendLine("    {");
        builder.AppendLine("        readonly global::Plank.RowApi.RowGroupWriterCore<BufferSlot> _core;");

        builder.AppendLine();
        builder.Append("        internal ").Append(writerTypeName)
            .AppendLine("(global::Plank.Writing.RowGroupWriter rowGroupWriter, global::Plank.Writing.ParquetWriterOptions options)");
        builder.AppendLine("        {");
        builder.AppendLine("            _ = options ?? throw new global::System.ArgumentNullException(nameof(options));");
        builder.AppendLine("            var slot = new BufferSlot(rowGroupWriter, DefaultRowBatchSize);");
        builder.AppendLine("            _core = new global::Plank.RowApi.RowGroupWriterCore<BufferSlot>(rowGroupWriter, slot);");
        builder.AppendLine("        }");
        builder.AppendLine();
        builder.AppendLine("        public Row GetRow()");
        builder.AppendLine("            => _core.GetSlotForRow().GetRow();");
        builder.AppendLine();
        builder.AppendLine("        public void Next()");
        builder.AppendLine("            => _core.Next();");
        builder.AppendLine();
        builder.AppendLine("        public void Write()");
        builder.AppendLine("            => _core.Write();");
        builder.AppendLine("    }");
        builder.AppendLine();
        builder.AppendLine("    public sealed class PipelineWriter : global::Plank.RowApi.PipelineRowWriterBase<BufferSlot>");
        builder.AppendLine("    {");
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
        builder.Append("            : base(stream, ").Append(schemaMemberName)
            .Append(", maxParallelism, onFlush, options, DefaultRowBatchSize, \"Plank")
            .Append(Escape(schemaType.Name))
            .AppendLine("RowApiWorker\")");
        builder.AppendLine("        {");
        builder.AppendLine("        }");
        builder.AppendLine();
        builder.AppendLine("        protected override BufferSlot CreateSlot(global::Plank.Writing.ParquetWriter writer)");
        builder.AppendLine("            => new(writer, RowBatchSize);");
        builder.AppendLine();
        builder.AppendLine("        public Row GetRow()");
        builder.AppendLine("            => GetSlotForRow().GetRow();");
        builder.AppendLine();
        builder.AppendLine("        public void Next()");
        builder.AppendLine("            => NextRow();");
        builder.AppendLine();
        builder.AppendLine("        public void Complete()");
        builder.AppendLine("            => CompleteWriter();");
        builder.AppendLine("    }");
        builder.AppendLine();
        builder.Append("    public sealed class ").Append(datasetWriterTypeName)
            .Append(" : global::Plank.Dataset.DatasetWriterBase<").Append(rowTypeName)
            .AppendLine(">, global::System.IDisposable");
        builder.AppendLine("    {");
        builder.Append("        readonly ").Append(routeTypeName).AppendLine(" _route;");
        builder.AppendLine();
        builder.Append("        internal ").Append(datasetWriterTypeName).Append('(').Append(routeTypeName)
            .AppendLine(" route, global::Plank.Writing.IParquetWriteSource[] files, global::Plank.Dataset.DatasetWriterOptions options)");
        builder.Append("            : base(").Append(schemaMemberName)
            .AppendLine(", s_rowApiColumns, DefaultRowBatchSize, files, options)");
        builder.AppendLine("        {");
        builder.AppendLine("            _route = route;");
        builder.AppendLine("            InitializeSlots();");
        builder.AppendLine("        }");
        builder.AppendLine();
        builder.Append("        protected override void CopyRow(").Append(rowTypeName)
            .AppendLine(" row, int slotIndex, int rowIndex)");
        builder.AppendLine("        {");
        for (var i = 0; i < columns.Length; i++)
            builder.Append("            SetColumnValue<").Append(columns[i].ClrTypeName).Append(">(slotIndex, ")
                .Append(i).Append(", rowIndex, row.").Append(EscapeIdentifier(columns[i].PropertyName))
                .AppendLine(");");
        builder.AppendLine("        }");
        builder.AppendLine();
        builder.Append("        protected override global::System.ReadOnlySpan<byte> SelectPath(")
            .Append(rowTypeName)
            .AppendLine(" row, global::Plank.IParquetBufferPool bufferPool, out global::Plank.ParquetBuffer? allocation)");
        builder.AppendLine("            => _route(row, bufferPool, out allocation);");
        builder.AppendLine();
        builder.Append("        public void Queue(").Append(rowTypeName).AppendLine(" row)");
        builder.AppendLine("            => QueueRow(row);");
        builder.AppendLine();
        builder.AppendLine("        public void Dispose()");
        builder.AppendLine("            => DisposeDataset();");
        builder.AppendLine("    }");
        builder.AppendLine();
        AppendRowReader(builder, columns, schemaMemberName);
        builder.AppendLine();
        builder.AppendLine("    public sealed class BufferSlot : global::Plank.RowApi.RowBufferSlot");
        builder.AppendLine("    {");
        builder.AppendLine("        internal BufferSlot(global::Plank.Writing.RowGroupWriter rowGroupWriter, int rowCount)");
        builder.AppendLine("            : base(rowGroupWriter, s_rowApiColumns, rowCount)");
        builder.AppendLine("        {");
        builder.AppendLine("        }");
        builder.AppendLine();
        builder.AppendLine("        internal BufferSlot(global::Plank.Writing.ParquetWriter writer, int rowCount)");
        builder.AppendLine("            : base(writer, s_rowApiColumns, rowCount)");
        builder.AppendLine("        {");
        builder.AppendLine("        }");
        builder.AppendLine();
        builder.AppendLine("        internal Row GetRow()");
        builder.AppendLine("        {");
        builder.AppendLine("            EnsureRowAvailable();");
        builder.AppendLine();
        builder.Append("            return new Row(Index, this");
        for (var i = 0; i < columns.Length; i++)
            builder.Append(", GetValues<").Append(columns[i].ClrTypeName).Append(">(").Append(i).Append(')');
        builder.AppendLine(");");
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
            builder.Append("        public ref ").Append(columns[i].ClrTypeName).Append(' ')
                .Append(EscapeIdentifier(columns[i].PropertyName)).AppendLine();
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

    static void AppendRowApiColumnDescriptors(StringBuilder builder, ImmutableArray<MappedColumn> columns,
        string schemaMemberName)
    {
        for (var i = 0; i < columns.Length; i++)
        {
            builder.Append("    static readonly global::Plank.RowApi.RowApiColumnDescriptor<")
                .Append(columns[i].ClrTypeName).Append("> ")
                .Append(GetRowApiColumnFieldName(columns[i].PropertyName))
                .Append(" = new(\"").Append(Escape(columns[i].PropertyName))
                .Append("\", ").Append(schemaMemberName).Append(".LeafColumns[").Append(i).AppendLine("]);");
        }
        builder.AppendLine();
        builder.AppendLine("    static readonly global::Plank.RowApi.RowApiColumnDescriptor[] s_rowApiColumns = [");
        for (var i = 0; i < columns.Length; i++)
            builder.Append("        ").Append(GetRowApiColumnFieldName(columns[i].PropertyName)).AppendLine(",");
        builder.AppendLine("    ];");
    }

    static void AppendColumnReader(StringBuilder builder, ImmutableArray<MappedColumn> columns,
        string schemaMemberName, string readerTypeName)
    {
        builder.Append("    public sealed class ").Append(readerTypeName)
            .AppendLine(" : global::System.IDisposable");
        builder.AppendLine("    {");
        builder.AppendLine("        readonly global::Plank.Reading.Logical.ParquetReader _reader;");
        builder.AppendLine();
        builder.Append("        internal ").Append(readerTypeName)
            .AppendLine("(global::Plank.Reading.Logical.ParquetReader reader)");
        builder.AppendLine("            => _reader = reader ?? throw new global::System.ArgumentNullException(nameof(reader));");
        builder.AppendLine();
        builder.AppendLine("        public global::Plank.Schema.ParquetSchema Schema => _reader.Schema;");
        builder.AppendLine();
        builder.AppendLine("        public global::Plank.Reading.Logical.ParquetFileMetadata Metadata => _reader.Metadata;");
        builder.AppendLine();
        builder.AppendLine("        public ReadRowGroupCollection RowGroups => new(_reader.RowGroups);");
        builder.AppendLine();
        builder.AppendLine("        public void Reset(global::System.IO.Stream stream, global::Plank.Reading.Logical.ParquetPagePruner? pagePruner = null)");
        builder.AppendLine("            => _reader.Reset(stream, pagePruner);");
        builder.AppendLine();
        builder.AppendLine("        public void Reset(global::Plank.Reading.IParquetReadSource source, global::Plank.Reading.Logical.ParquetPagePruner? pagePruner = null)");
        builder.AppendLine("            => _reader.Reset(source, pagePruner);");
        builder.AppendLine();
        builder.AppendLine("        public void Dispose()");
        builder.AppendLine("            => _reader.Dispose();");
        builder.AppendLine("    }");
        builder.AppendLine();
        builder.AppendLine("    public readonly struct ReadRowGroupCollection");
        builder.AppendLine("    {");
        builder.AppendLine("        readonly global::Plank.Reading.Logical.RowGroupCollection _rowGroups;");
        builder.AppendLine();
        builder.AppendLine("        internal ReadRowGroupCollection(global::Plank.Reading.Logical.RowGroupCollection rowGroups)");
        builder.AppendLine("            => _rowGroups = rowGroups;");
        builder.AppendLine();
        builder.AppendLine("        public int Count => _rowGroups.Count;");
        builder.AppendLine();
        builder.AppendLine("        public ReadRowGroup this[int index] => new(_rowGroups[index]);");
        builder.AppendLine();
        builder.AppendLine("        public Enumerator GetEnumerator()");
        builder.AppendLine("            => new(_rowGroups.GetEnumerator());");
        builder.AppendLine();
        builder.AppendLine("        public struct Enumerator");
        builder.AppendLine("        {");
        builder.AppendLine("            global::Plank.Reading.Logical.RowGroupCollection.Enumerator _inner;");
        builder.AppendLine();
        builder.AppendLine("            internal Enumerator(global::Plank.Reading.Logical.RowGroupCollection.Enumerator inner)");
        builder.AppendLine("                => _inner = inner;");
        builder.AppendLine();
        builder.AppendLine("            public ReadRowGroup Current => new(_inner.Current);");
        builder.AppendLine();
        builder.AppendLine("            public bool MoveNext()");
        builder.AppendLine("                => _inner.MoveNext();");
        builder.AppendLine("        }");
        builder.AppendLine("    }");
        builder.AppendLine();
        builder.AppendLine("    public readonly struct ReadRowGroup");
        builder.AppendLine("    {");
        builder.AppendLine("        readonly global::Plank.Reading.Logical.RowGroup _rowGroup;");
        builder.AppendLine();
        builder.AppendLine("        internal ReadRowGroup(global::Plank.Reading.Logical.RowGroup rowGroup)");
        builder.AppendLine("            => _rowGroup = rowGroup;");
        builder.AppendLine();
        builder.AppendLine("        public int Index => _rowGroup.Index;");
        builder.AppendLine();
        builder.AppendLine("        public ulong MetadataOffset => _rowGroup.MetadataOffset;");
        builder.AppendLine();
        builder.AppendLine("        public ulong ColumnChunkOffset => _rowGroup.ColumnChunkOffset;");
        builder.AppendLine();
        builder.AppendLine("        public ulong RowCount => _rowGroup.RowCount;");
        for (var i = 0; i < columns.Length; i++)
        {
            builder.AppendLine();
            if (IsUtf8ByteArrayClr(columns[i].ClrTypeName) ||
                IsStringClr(columns[i].ClrTypeName) ||
                IsGuidClr(columns[i].ClrTypeName))
            {
                builder.Append("        public global::Plank.Reading.Logical.RowGroupColumn<byte> ")
                    .Append(columns[i].PropertyName)
                    .Append("Column => _rowGroup.Column<byte>(").Append(schemaMemberName).Append(".LeafColumns[")
                    .Append(i).AppendLine("]);");
            }
            else
            {
                builder.Append("        public global::Plank.Reading.Logical.RowGroupColumn<")
                    .Append(columns[i].ClrTypeName).Append("> ").Append(columns[i].PropertyName)
                    .Append("Column => _rowGroup.Column<").Append(columns[i].ClrTypeName)
                    .Append(">(").Append(schemaMemberName).Append(".LeafColumns[").Append(i).AppendLine("]);");
            }
        }
        builder.AppendLine("    }");
    }

    static void AppendRowApiProjection(StringBuilder builder, ImmutableArray<MappedColumn> columns)
    {
        builder.AppendLine("    public readonly struct Projection");
        builder.AppendLine("    {");
        builder.AppendLine("        readonly global::Plank.RowApi.RowApiColumnDescriptor[]? _columns;");
        builder.AppendLine();
        builder.AppendLine("        Projection(global::Plank.RowApi.RowApiColumnDescriptor[] columns)");
        builder.AppendLine("            => _columns = columns;");
        builder.AppendLine();
        builder.AppendLine("        internal global::Plank.RowApi.RowApiColumnDescriptor[]? Columns => _columns;");
        builder.AppendLine();
        builder.AppendLine("        public static Projection All => default;");
        builder.AppendLine();
        builder.AppendLine("        public static Projection None { get; } = new([]);");
        for (var i = 0; i < columns.Length; i++)
        {
            builder.AppendLine();
            builder.Append("        public static Projection ").Append(EscapeIdentifier(columns[i].PropertyName))
                .Append(" { get; } = new([")
                .Append(GetRowApiColumnFieldName(columns[i].PropertyName)).AppendLine("]);");
        }
        builder.AppendLine();
        builder.AppendLine("        public static Projection operator |(Projection left, Projection right)");
        builder.AppendLine("        {");
        builder.AppendLine("            if (left._columns is null || right._columns is null)");
        builder.AppendLine("                return All;");
        builder.AppendLine("            if (left._columns.Length == 0)");
        builder.AppendLine("                return right;");
        builder.AppendLine("            if (right._columns.Length == 0)");
        builder.AppendLine("                return left;");
        builder.AppendLine();
        builder.AppendLine("            var combined = new global::Plank.RowApi.RowApiColumnDescriptor[left._columns.Length + right._columns.Length];");
        builder.AppendLine("            left._columns.CopyTo(combined, 0);");
        builder.AppendLine("            right._columns.CopyTo(combined, left._columns.Length);");
        builder.AppendLine("            return new Projection(combined);");
        builder.AppendLine("        }");
        builder.AppendLine("    }");
    }

    static void AppendRowReader(StringBuilder builder, ImmutableArray<MappedColumn> columns, string schemaMemberName)
    {
        builder.AppendLine("    public sealed class RowReader : global::System.IDisposable");
        builder.AppendLine("    {");
        builder.AppendLine("        readonly global::Plank.RowApi.RowReaderCore _core;");
        builder.AppendLine();
        builder.AppendLine("        internal RowReader(global::System.IO.Stream stream, Projection projection, global::Plank.RowApi.RowReaderOptions options, global::Plank.Reading.ParquetSchemaEvolutionOptions? schemaEvolution)");
        builder.AppendLine("        {");
        builder.Append("            _core = new global::Plank.RowApi.RowReaderCore(stream, ").Append(schemaMemberName)
            .AppendLine(", s_rowApiColumns, projection.Columns, options, schemaEvolution);");
        builder.AppendLine("        }");
        builder.AppendLine();
        builder.AppendLine("        internal RowReader(global::Plank.Reading.IParquetReadSource source, Projection projection, global::Plank.RowApi.RowReaderOptions options, global::Plank.Reading.ParquetSchemaEvolutionOptions? schemaEvolution)");
        builder.AppendLine("        {");
        builder.Append("            _core = new global::Plank.RowApi.RowReaderCore(source, ").Append(schemaMemberName)
            .AppendLine(", s_rowApiColumns, projection.Columns, options, schemaEvolution);");
        builder.AppendLine("        }");
        builder.AppendLine();
        builder.AppendLine("        public Enumerator GetEnumerator()");
        builder.AppendLine("            => new(this);");
        builder.AppendLine();
        builder.AppendLine("        public void Reset(global::System.IO.Stream stream, Projection projection = default, global::Plank.Reading.ParquetSchemaEvolutionOptions? schemaEvolution = null)");
        builder.AppendLine("            => _core.Reset(stream, projection.Columns, schemaEvolution);");
        builder.AppendLine();
        builder.AppendLine("        public void Reset(global::Plank.Reading.IParquetReadSource source, Projection projection = default, global::Plank.Reading.ParquetSchemaEvolutionOptions? schemaEvolution = null)");
        builder.AppendLine("            => _core.Reset(source, projection.Columns, schemaEvolution);");
        builder.AppendLine();
        builder.AppendLine("        public readonly struct Enumerator : global::System.IDisposable");
        builder.AppendLine("        {");
        builder.AppendLine("            readonly RowReader _reader;");
        builder.AppendLine();
        builder.AppendLine("            internal Enumerator(RowReader reader)");
        builder.AppendLine("                => _reader = reader ?? throw new global::System.ArgumentNullException(nameof(reader));");
        builder.AppendLine();
        builder.AppendLine("            public ReadRow Current => _reader.Current;");
        builder.AppendLine();
        builder.AppendLine("            public bool MoveNext()");
        builder.AppendLine("                => _reader.MoveNext();");
        builder.AppendLine();
        builder.AppendLine("            public void Dispose()");
        builder.AppendLine("            {");
        builder.AppendLine("            }");
        builder.AppendLine("        }");
        builder.AppendLine();
        builder.AppendLine("        public ReadRow Current");
        builder.AppendLine("        {");
        builder.AppendLine("            get");
        builder.AppendLine("            {");
        builder.AppendLine("                _core.ThrowIfNotPositioned();");
        builder.AppendLine("                return new ReadRow(_core);");
        builder.AppendLine("            }");
        builder.AppendLine("        }");
        builder.AppendLine();
        builder.AppendLine("        public bool MoveNext()");
        builder.AppendLine("            => _core.MoveNext();");
        builder.AppendLine();
        builder.AppendLine("        public void Dispose()");
        builder.AppendLine("            => _core.Dispose();");
        builder.AppendLine("    }");
        builder.AppendLine();
        AppendReadRow(builder, columns);
    }

    static void AppendReadRow(StringBuilder builder, ImmutableArray<MappedColumn> columns)
    {
        builder.AppendLine("    public readonly ref struct ReadRow");
        builder.AppendLine("    {");
        builder.AppendLine("        readonly global::Plank.RowApi.RowReaderCore _core;");
        builder.AppendLine();
        builder.AppendLine("        internal ReadRow(global::Plank.RowApi.RowReaderCore core)");
        builder.AppendLine("            => _core = core;");
        builder.AppendLine();
        for (var i = 0; i < columns.Length; i++)
        {
            var propertyName = columns[i].PropertyName;
            var descriptorName = GetRowApiColumnFieldName(propertyName);
            if (IsStringClr(columns[i].ClrTypeName))
            {
                builder.Append("        public ").Append(columns[i].ClrTypeName).Append(' ')
                    .Append(EscapeIdentifier(propertyName)).AppendLine();
                builder.AppendLine("        {");
                builder.AppendLine("            get");
                builder.AppendLine("            {");
                builder.Append("                var value = _core.GetCurrentBinary(").Append(descriptorName)
                    .AppendLine(");");
                builder.AppendLine("                if (value.IsNull)");
                builder.AppendLine("                    return null!;");
                builder.AppendLine("                return global::System.Text.Encoding.UTF8.GetString(value.Value);");
                builder.AppendLine("            }");
                builder.AppendLine("        }");
            }
            else if (IsGuidClr(columns[i].ClrTypeName))
            {
                builder.Append("        public ").Append(columns[i].ClrTypeName).Append(' ')
                    .Append(EscapeIdentifier(propertyName)).AppendLine();
                builder.AppendLine("        {");
                builder.AppendLine("            get");
                builder.AppendLine("            {");
                builder.Append("                var value = _core.GetCurrentBinary(").Append(descriptorName)
                    .AppendLine(");");
                if (columns[i].ClrTypeName.EndsWith("?", StringComparison.Ordinal))
                    builder.AppendLine("                return value.IsNull ? null : new global::System.Guid(value.Value, bigEndian: true);");
                else
                    builder.AppendLine("                return new global::System.Guid(value.Value, bigEndian: true);");
                builder.AppendLine("            }");
                builder.AppendLine("        }");
            }
            else if (IsUtf8ByteArrayClr(columns[i].ClrTypeName))
            {
                builder.Append("        public global::System.ReadOnlySpan<byte> ")
                    .Append(EscapeIdentifier(propertyName)).AppendLine();
                builder.Append("            => _core.GetCurrentBinary(").Append(descriptorName)
                    .AppendLine(").Value;");
                builder.AppendLine();
                builder.Append("        public bool ").Append(propertyName).AppendLine("IsNull");
                builder.Append("            => _core.GetCurrentBinary(").Append(descriptorName)
                    .AppendLine(").IsNull;");
            }
            else
            {
                builder.Append("        public ref ").Append(columns[i].ClrTypeName).Append(' ')
                    .Append(EscapeIdentifier(propertyName)).AppendLine();
                builder.Append("            => ref _core.GetCurrent(").Append(descriptorName).AppendLine(");");
            }
            if (i < columns.Length - 1)
                builder.AppendLine();
        }
        builder.AppendLine("    }");
    }

    static bool IsSupportedMapping(SchemaColumn column, string clrType)
    {
        if (column.ConverterTypeName is not null)
            return true;

        if (column.Repetition == "Optional")
            return column.PhysicalType switch
            {
                "Boolean" => clrType is "bool" or "bool?",
                "Int32" => clrType is
                    "byte" or "byte?"
                    or "ushort" or "ushort?"
                    or "int" or "int?"
                    or "uint" or "uint?"
                    or "global::System.DateOnly" or "global::System.DateOnly?"
                    or "decimal" or "decimal?",
                "Int64" => clrType is
                    "long" or "long?"
                    or "ulong" or "ulong?"
                    or "global::System.DateTime" or "global::System.DateTime?"
                    or "global::System.DateTimeOffset" or "global::System.DateTimeOffset?"
                    or "global::System.TimeOnly" or "global::System.TimeOnly?"
                    or "decimal" or "decimal?",
                "Float" => clrType is "float" or "float?",
                "Double" => clrType is "double" or "double?",
                "ByteArray" => clrType is
                    "byte[]" or "byte[]?"
                    or "string" or "string?"
                    or "global::System.ReadOnlyMemory<byte>" or "global::System.ReadOnlyMemory<byte>?"
                    or "decimal" or "decimal?",
                "FixedLenByteArray" => (clrType is "global::System.Guid" or "global::System.Guid?" or "decimal" or "decimal?") ||
                    IsAdditionalFixedBinaryMapping(column, clrType),
                _ => false
            };

        return column.PhysicalType switch
        {
            "Boolean" => clrType == "bool",
            "Int32" => clrType is "byte" or "ushort" or "int" or "uint" or "global::System.DateOnly" or "decimal",
            "Int64" => clrType is
                "long" or "ulong"
                or "global::System.DateTime" or "global::System.DateTimeOffset" or "global::System.TimeOnly"
                or "decimal",
            "Float" => clrType == "float",
            "Double" => clrType == "double",
            "ByteArray" => clrType is "byte[]" or "string" or "global::System.ReadOnlyMemory<byte>" or "decimal",
            "FixedLenByteArray" => (clrType is "global::System.Guid" or "decimal") ||
                IsAdditionalFixedBinaryMapping(column, clrType),
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
            "decimal" or "decimal?" or
            "string" or "string?" or
            "byte[]" or "byte[]?" or
            "global::System.ReadOnlyMemory<byte>" or "global::System.ReadOnlyMemory<byte>?" or
            "global::System.DateOnly" or "global::System.DateOnly?" or
            "global::System.DateTime" or "global::System.DateTime?" or
            "global::System.DateTimeOffset" or "global::System.DateTimeOffset?" or
            "global::System.TimeOnly" or "global::System.TimeOnly?" or
            "global::System.Guid" or "global::System.Guid?";

    static bool IsAdditionalFixedBinaryMapping(SchemaColumn column, string clrType)
        => column.LogicalType?.Kind is "Float16" or "Interval" && IsUtf8ByteArrayClr(clrType);

    static string Escape(string value)
        => Microsoft.CodeAnalysis.CSharp.SymbolDisplay.FormatLiteral(value, quote: false);

    static string EscapeIdentifier(string value)
        => Microsoft.CodeAnalysis.CSharp.SyntaxFacts.GetKeywordKind(value) ==
            Microsoft.CodeAnalysis.CSharp.SyntaxKind.None ? value : $"@{value}";

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

    static string GetRowApiColumnFieldName(string propertyName)
        => $"s_{propertyName}RowApiColumn";

    static string GetAvailableGeneratedMemberName(INamedTypeSymbol schemaType, string preferredName)
    {
        if (schemaType.GetMembers(preferredName).IsDefaultOrEmpty)
            return preferredName;

        for (var suffix = 1; ; suffix++)
        {
            var candidate = preferredName + suffix.ToString(CultureInfo.InvariantCulture);
            if (schemaType.GetMembers(candidate).IsDefaultOrEmpty)
                return candidate;
        }
    }

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
        => $"global::System.Span<{clrTypeName}>";

    static bool SupportsOwnerSetter(string clrTypeName)
        => clrTypeName is "global::System.ReadOnlyMemory<byte>" or "global::System.ReadOnlyMemory<byte>?";

    static bool AllowsAllocatingValues(INamedTypeSymbol schemaType)
    {
        var attribute = schemaType.GetAttributes().FirstOrDefault(static attribute =>
            attribute.AttributeClass?.ToDisplayString() == "Plank.Schema.ParquetSchemaAttribute");
        if (attribute is null)
            return false;

        foreach (var argument in attribute.NamedArguments)
            if (argument.Key == "AllowAllocatingValues" && argument.Value.Value is true)
                return true;
        return false;
    }

    static bool TryExtractColumns(INamedTypeSymbol schemaType, out ImmutableArray<SchemaColumn> columns, out string error)
    {
        error = string.Empty;
        var properties = schemaType.GetMembers()
            .OfType<IPropertySymbol>()
            .Where(static p => !p.IsStatic && !p.IsIndexer)
            .OrderBy(static p => p.Locations.FirstOrDefault()?.SourceTree?.FilePath, StringComparer.Ordinal)
            .ThenBy(static p => p.Locations.FirstOrDefault()?.SourceSpan.Start ?? int.MaxValue)
            .ThenBy(static p => p.Name, StringComparer.Ordinal)
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

        if (!TryGetConverterSpec(property, out var converter, out error))
            return false;

        string clrTypeName;
        string inferredPhysicalType;
        LogicalTypeSpec? inferredLogicalType;
        if (converter is null)
        {
            if (!TryNormalizeClrType(property.Type, property.NullableAnnotation, out clrTypeName))
            {
                error = $"Unsupported CLR type '{property.Type.ToDisplayString()}' for schema property '{property.Name}'.";
                return false;
            }

            if (!TryInferDefaults(clrTypeName, out inferredPhysicalType, out inferredLogicalType))
            {
                error = $"Could not infer parquet mapping for CLR type '{property.Type.ToDisplayString()}' on property '{property.Name}'.";
                return false;
            }
        }
        else
        {
            clrTypeName = property.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            if (!TryInferDefaults(converter.Value.PhysicalClrTypeName, out inferredPhysicalType,
                    out inferredLogicalType))
            {
                error = $"Converter '{converter.Value.TypeName}' uses unsupported physical CLR type " +
                    $"'{converter.Value.PhysicalClrTypeName}'.";
                return false;
            }
        }

        var columnName = property.Name;
        var physicalType = inferredPhysicalType;
        var logicalType = inferredLogicalType;
        int? fieldId = null;
        ImmutableArray<string> encodings = [];
        var bloomFilter = false;
        var bloomFilterFalsePositiveProbability = 0.01;
        var bloomFilterExpectedDistinctValueCount = 0U;
        var bloomFilterMaximumBytes = 128U * 1024 * 1024;
        if (!TryReadColumnOverrides(property, ref columnName, ref physicalType, ref logicalType, ref fieldId, ref encodings,
                ref bloomFilter, ref bloomFilterFalsePositiveProbability, ref bloomFilterExpectedDistinctValueCount,
                ref bloomFilterMaximumBytes, out error))
            return false;
        if (converter is not null && physicalType != inferredPhysicalType)
        {
            error = $"Converter '{converter.Value.TypeName}' uses physical CLR type " +
                $"'{converter.Value.PhysicalClrTypeName}', which maps to '{inferredPhysicalType}', not " +
                $"declared physical type '{physicalType}'.";
            return false;
        }
        if (columnName.Length == 0)
        {
            error = $"Property '{property.Name}' has an empty parquet column name.";
            return false;
        }

        var repetition = IsNullableClrType(clrTypeName) ? "Optional" : "Required";
        column = new SchemaColumn(columnName, physicalType, repetition, clrTypeName, logicalType, property.Name, encodings,
            GetTypeLength(physicalType, converter?.PhysicalClrTypeName ?? clrTypeName, logicalType), converter?.TypeName,
            fieldId, bloomFilter, bloomFilterFalsePositiveProbability,
            bloomFilterExpectedDistinctValueCount, bloomFilterMaximumBytes);
        return true;
    }

    static bool TryGetConverterSpec(IPropertySymbol property, out ConverterSpec? converter, out string error)
    {
        converter = null;
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

        ITypeSymbol? converterTypeSymbol = null;
        foreach (var argument in attributes[0].NamedArguments)
            if (argument.Key == "Converter")
                converterTypeSymbol = argument.Value.Value as ITypeSymbol;
        if (converterTypeSymbol is null)
            return true;
        if (converterTypeSymbol is not INamedTypeSymbol converterType || converterType.IsAbstract ||
            converterType.IsUnboundGenericType)
        {
            error = $"Property '{property.Name}' converter must be a non-abstract, closed class type.";
            return false;
        }

        var converterBase = FindConverterBase(converterType);
        if (converterBase is null)
        {
            error = $"Converter '{converterType.ToDisplayString()}' must derive from " +
                "ParquetValueConverter<TValue, TPhysical>.";
            return false;
        }

        var propertyValueType = property.Type is INamedTypeSymbol
        { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable
            ? nullable.TypeArguments[0]
            : property.Type;
        if (!SymbolEqualityComparer.Default.Equals(propertyValueType, converterBase.TypeArguments[0]))
        {
            error = $"Converter '{converterType.ToDisplayString()}' maps " +
                $"'{converterBase.TypeArguments[0].ToDisplayString()}', not property type " +
                $"'{propertyValueType.ToDisplayString()}'.";
            return false;
        }

        var physicalType = converterBase.TypeArguments[1];
        if (physicalType is INamedTypeSymbol
            { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T })
        {
            error = $"Converter '{converterType.ToDisplayString()}' physical CLR type must be non-nullable.";
            return false;
        }
        if (!physicalType.IsUnmanagedType ||
            !TryNormalizeClrType(physicalType, NullableAnnotation.NotAnnotated, out var physicalClrTypeName))
        {
            error = $"Converter '{converterType.ToDisplayString()}' uses unsupported physical CLR type " +
                $"'{physicalType.ToDisplayString()}'.";
            return false;
        }

        var sameAssembly = SymbolEqualityComparer.Default.Equals(
            converterType.ContainingAssembly, property.ContainingAssembly);
        var hasParameterlessConstructor = converterType.InstanceConstructors.Any(static constructor =>
            constructor.Parameters.Length == 0 && constructor.DeclaredAccessibility == Accessibility.Public) ||
            sameAssembly && converterType.InstanceConstructors.Any(static constructor =>
                constructor.Parameters.Length == 0 &&
                constructor.DeclaredAccessibility is Accessibility.Internal or Accessibility.ProtectedOrInternal);
        if (!hasParameterlessConstructor)
        {
            error = $"Converter '{converterType.ToDisplayString()}' must have an accessible parameterless constructor.";
            return false;
        }

        converter = new ConverterSpec(
            converterType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat), physicalClrTypeName);
        return true;
    }

    static INamedTypeSymbol? FindConverterBase(INamedTypeSymbol converterType)
    {
        for (var candidate = converterType.BaseType; candidate is not null; candidate = candidate.BaseType)
            if (candidate.OriginalDefinition.Name == "ParquetValueConverter" &&
                candidate.OriginalDefinition.Arity == 2 &&
                candidate.OriginalDefinition.ContainingNamespace.ToDisplayString() == "Plank.Schema")
                return candidate;
        return null;
    }

    static uint GetTypeLength(string physicalType, string clrTypeName, LogicalTypeSpec? logicalType)
    {
        if (physicalType != "FixedLenByteArray")
            return 0;
        if (IsGuidClr(clrTypeName))
            return 16;
        return logicalType?.Kind switch
        {
            "Decimal" when logicalType.Value.Precision > 0 => GetDecimalTypeLength(logicalType.Value.Precision.Value),
            "Float16" => 2,
            "Interval" => 12,
            _ => 0
        };
    }

    static bool TryReadColumnOverrides(IPropertySymbol property, ref string columnName, ref string physicalType,
        ref LogicalTypeSpec? logicalType, ref int? fieldId, ref ImmutableArray<string> encodings, ref bool bloomFilter,
        ref double bloomFilterFalsePositiveProbability, ref uint bloomFilterExpectedDistinctValueCount,
        ref uint bloomFilterMaximumBytes, out string error)
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

        int? precision = logicalType?.Precision;
        int? scale = logicalType?.Scale;
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

            if (namedArgument.Key == "Precision")
            {
                if (namedArgument.Value.Value is not int value)
                {
                    error = $"Property '{property.Name}' declares an invalid decimal precision.";
                    return false;
                }
                precision = value;
                continue;
            }

            if (namedArgument.Key == "Scale")
            {
                if (namedArgument.Value.Value is not int value)
                {
                    error = $"Property '{property.Name}' declares an invalid decimal scale.";
                    return false;
                }
                scale = value;
                continue;
            }

            if (namedArgument.Key == "LogicalType")
            {
                if (!TryGetLogicalTypeSpec(namedArgument.Value, logicalType, out logicalType))
                {
                    error = $"Property '{property.Name}' declares an invalid LogicalTypeKind override.";
                    return false;
                }
                continue;
            }

            if (namedArgument.Key == "FieldId")
            {
                if (namedArgument.Value.Value is not int value)
                {
                    error = $"Property '{property.Name}' declares an invalid field ID.";
                    return false;
                }
                fieldId = value;
            }

            if (namedArgument.Key == "BloomFilter" && namedArgument.Value.Value is bool enabled)
                bloomFilter = enabled;
            else if (namedArgument.Key == "BloomFilterFalsePositiveProbability" &&
                     namedArgument.Value.Value is double probability)
                bloomFilterFalsePositiveProbability = probability;
            else if (namedArgument.Key == "BloomFilterExpectedDistinctValueCount" &&
                     namedArgument.Value.Value is uint expectedDistinctValueCount)
                bloomFilterExpectedDistinctValueCount = expectedDistinctValueCount;
            else if (namedArgument.Key == "BloomFilterMaximumBytes" && namedArgument.Value.Value is uint maximumBytes)
                bloomFilterMaximumBytes = maximumBytes;
        }

        if (logicalType is { Kind: "Decimal" })
            logicalType = new LogicalTypeSpec("Decimal", precision: precision, scale: scale ?? 0);

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
        if (!TryGetEnumValue(constant, out var enumValue))
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
        if (!TryGetEnumValue(constant, out var enumValue))
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

    static bool TryGetLogicalTypeSpec(TypedConstant constant, LogicalTypeSpec? inferredLogicalType,
        out LogicalTypeSpec? logicalType)
    {
        logicalType = null;
        if (!TryGetEnumValue(constant, out var enumValue))
            return false;

        logicalType = enumValue switch
        {
            0 => null,
            1 => new LogicalTypeSpec("String"),
            2 => new LogicalTypeSpec("Json"),
            3 => new LogicalTypeSpec("Uuid"),
            4 => ReuseInferredLogicalType(inferredLogicalType, "Date"),
            5 => ReuseInferredLogicalType(inferredLogicalType, "Time"),
            6 => ReuseInferredLogicalType(inferredLogicalType, "Timestamp"),
            7 => ReuseInferredLogicalType(inferredLogicalType, "Int"),
            8 => ReuseInferredLogicalType(inferredLogicalType, "Decimal"),
            9 => new LogicalTypeSpec("Enum"),
            10 => new LogicalTypeSpec("Bson"),
            11 => new LogicalTypeSpec("Float16"),
            12 => new LogicalTypeSpec("Interval"),
            13 => new LogicalTypeSpec("Geography"),
            14 => new LogicalTypeSpec("Geometry"),
            15 => new LogicalTypeSpec("Variant"),
            16 => new LogicalTypeSpec("Unknown"),
            _ => null
        };

        return enumValue is >= 0 and <= 16;
    }

    static LogicalTypeSpec ReuseInferredLogicalType(LogicalTypeSpec? inferredLogicalType, string kind)
        => inferredLogicalType is { } inferred && inferred.Kind == kind
            ? inferred
            : new LogicalTypeSpec(kind);

    static bool TryGetEnumValue(TypedConstant constant, out int value)
    {
        switch (constant.Value)
        {
            case byte byteValue:
                value = byteValue;
                return true;
            case sbyte sbyteValue:
                value = sbyteValue;
                return true;
            case short shortValue:
                value = shortValue;
                return true;
            case ushort ushortValue:
                value = ushortValue;
                return true;
            case int intValue:
                value = intValue;
                return true;
            default:
                value = 0;
                return false;
        }
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
            "decimal" => "FixedLenByteArray",
            "string" => "ByteArray",
            "byte[]" => "ByteArray",
            "global::System.ReadOnlyMemory<byte>" => "ByteArray",
            "global::System.DateOnly" => "Int32",
            "global::System.DateTime" => "Int64",
            "global::System.DateTimeOffset" => "Int64",
            "global::System.TimeOnly" => "Int64",
            "global::System.Guid" => "FixedLenByteArray",
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
            "string" => new LogicalTypeSpec("String"),
            "global::System.Guid" => new LogicalTypeSpec("Uuid"),
            "decimal" => new LogicalTypeSpec("Decimal", scale: 0),
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
            "Bson" => "new global::Plank.Schema.LogicalType.Bson()",
            "Enum" => "new global::Plank.Schema.LogicalType.Enum()",
            "Uuid" => "new global::Plank.Schema.LogicalType.Uuid()",
            "Float16" => "new global::Plank.Schema.LogicalType.Float16()",
            "Interval" => "new global::Plank.Schema.LogicalType.Interval()",
            "Unknown" => "new global::Plank.Schema.LogicalType.Unknown()",
            "Variant" => "new global::Plank.Schema.LogicalType.Variant()",
            "Geometry" => "new global::Plank.Schema.LogicalType.Geometry()",
            "Geography" => "new global::Plank.Schema.LogicalType.Geography()",
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

            for (var encodingIndex = 0; encodingIndex < column.Encodings.Length; encodingIndex++)
            {
                var encoding = column.Encodings[encodingIndex];
                if (!IsEncodingSupported(column.PhysicalType, encoding))
                    diagnostics.Add(new SchemaDiagnostic(InvalidEncoding,
                        $"Encoding '{encoding}' does not support physical type '{column.PhysicalType}' for column '{column.Name}'."));
            }

            ValidateLogicalType(column, diagnostics);
        }

        return diagnostics.ToImmutable();
    }

    static bool IsSupportedPhysicalType(string physicalType)
        => physicalType is "Boolean" or "Int32" or "Int64" or "Float" or "Double" or "ByteArray" or "Int96" or "FixedLenByteArray";

    static bool IsSupportedRepetition(string repetition)
        => repetition is "Unspecified" or "Required" or "Optional" or "Repeated";

    static bool IsEncodingSupported(string physicalType, string encoding)
        => encoding switch
        {
            "Plain" or "PlainDictionary" or "RleDictionary" => true,
            "Rle" => physicalType == "Boolean",
            "BitPacked" => false,
            "DeltaBinaryPacked" => physicalType is "Int32" or "Int64",
            "DeltaLengthByteArray" or "DeltaByteArray" => physicalType == "ByteArray",
            "ByteStreamSplit" => physicalType is "Int32" or "Int64" or "Float" or "Double" or "FixedLenByteArray",
            _ => false
        };

    static void ValidateLogicalType(SchemaColumn column, ImmutableArray<SchemaDiagnostic>.Builder diagnostics)
    {
        var logicalType = column.LogicalType;
        if (logicalType is null)
        {
            if (column.ConverterTypeName is null &&
                IsUnsignedIntClr(column.ClrTypeName, GetUnsignedBitWidth(column.ClrTypeName)))
                diagnostics.Add(new SchemaDiagnostic(LogicalClrMismatch,
                    $"Column '{column.Name}' uses an unsigned CLR integer and must declare logical type 'Int' with IsSigned=false."));
            if (column.ConverterTypeName is null && IsDateOnly(column.ClrTypeName))
                diagnostics.Add(new SchemaDiagnostic(MissingDateLogicalType,
                    $"Column '{column.Name}' uses DateOnly and must declare logical type 'Date'."));
            if (column.ConverterTypeName is null && IsTimeOnly(column.ClrTypeName))
                diagnostics.Add(new SchemaDiagnostic(MissingTimeLogicalType,
                    $"Column '{column.Name}' uses TimeOnly and must declare logical type 'Time'."));
            if (column.ConverterTypeName is null && IsTimestampClr(column.ClrTypeName))
                diagnostics.Add(new SchemaDiagnostic(MissingTimestampLogicalType,
                    $"Column '{column.Name}' uses DateTime/DateTimeOffset and must declare logical type 'Timestamp'."));
            if (IsDecimalClr(column.ClrTypeName))
                diagnostics.Add(new SchemaDiagnostic(InvalidDecimalDefinition,
                    $"Column '{column.Name}' uses decimal and must declare a decimal precision."));
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
                if (column.ConverterTypeName is null &&
                    !IsUnsignedIntClr(column.ClrTypeName, logicalType.Value.BitWidth))
                    diagnostics.Add(new SchemaDiagnostic(LogicalClrMismatch,
                        $"Column '{column.Name}' logical type 'Int({logicalType.Value.BitWidth},false)' requires matching unsigned CLR type."));
                break;
            case "Date":
                if (column.PhysicalType != "Int32")
                    diagnostics.Add(new SchemaDiagnostic(LogicalPhysicalMismatch,
                        $"Column '{column.Name}' logical type 'Date' requires physical type 'Int32'."));
                if (column.ConverterTypeName is null && !IsDateOnly(column.ClrTypeName))
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
                if (column.ConverterTypeName is null && !IsTimeOnly(column.ClrTypeName))
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
                if (column.ConverterTypeName is null && !IsTimestampClr(column.ClrTypeName))
                    diagnostics.Add(new SchemaDiagnostic(LogicalClrMismatch,
                        $"Column '{column.Name}' logical type 'Timestamp' requires CLR type DateTime/DateTimeOffset (nullable allowed)."));
                break;
            case "String":
            case "Json":
            case "Bson":
            case "Enum":
            case "Geometry":
            case "Geography":
                if (column.PhysicalType != "ByteArray")
                    diagnostics.Add(new SchemaDiagnostic(LogicalPhysicalMismatch,
                        $"Column '{column.Name}' logical type '{logicalType.Value.Kind}' requires physical type 'ByteArray'."));
                if (column.ConverterTypeName is null &&
                    !IsUtf8ByteArrayClr(column.ClrTypeName) && !IsStringClr(column.ClrTypeName))
                    diagnostics.Add(new SchemaDiagnostic(LogicalClrMismatch,
                        $"Column '{column.Name}' logical type '{logicalType.Value.Kind}' requires CLR type string/string?/ReadOnlyMemory<byte>/ReadOnlyMemory<byte>?/byte[]/byte[]?."));
                break;
            case "Float16":
            case "Interval":
                if (column.PhysicalType != "FixedLenByteArray")
                    diagnostics.Add(new SchemaDiagnostic(LogicalPhysicalMismatch,
                        $"Column '{column.Name}' logical type '{logicalType.Value.Kind}' requires physical type 'FixedLenByteArray'."));
                if (!IsUtf8ByteArrayClr(column.ClrTypeName))
                    diagnostics.Add(new SchemaDiagnostic(LogicalClrMismatch,
                        $"Column '{column.Name}' logical type '{logicalType.Value.Kind}' requires CLR type ReadOnlyMemory<byte>/ReadOnlyMemory<byte>?/byte[]/byte[]?."));
                break;
            case "Unknown":
                if (column.Repetition != "Optional")
                    diagnostics.Add(new SchemaDiagnostic(LogicalClrMismatch,
                        $"Column '{column.Name}' logical type 'Unknown' requires a nullable CLR type."));
                break;
            case "Variant":
                diagnostics.Add(new SchemaDiagnostic(LogicalPhysicalMismatch,
                    $"Column '{column.Name}' logical type 'Variant' requires a runtime group schema."));
                break;
            case "Uuid":
                if (column.PhysicalType != "FixedLenByteArray")
                    diagnostics.Add(new SchemaDiagnostic(LogicalPhysicalMismatch,
                        $"Column '{column.Name}' logical type 'Uuid' requires physical type 'FixedLenByteArray'."));
                if (column.ConverterTypeName is null &&
                    !IsUtf8ByteArrayClr(column.ClrTypeName) && !IsGuidClr(column.ClrTypeName))
                    diagnostics.Add(new SchemaDiagnostic(LogicalClrMismatch,
                        $"Column '{column.Name}' logical type 'Uuid' requires CLR type Guid/Guid?/ReadOnlyMemory<byte>/ReadOnlyMemory<byte>?/byte[]/byte[]?."));
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
                if (logicalType.Value.Precision is int decimalPrecision)
                {
                    if (column.PhysicalType == "Int32" && decimalPrecision > 9)
                        diagnostics.Add(new SchemaDiagnostic(DecimalPhysicalMismatch,
                            $"Column '{column.Name}' decimal precision {decimalPrecision} exceeds the maximum 9 for physical type 'Int32'."));
                    if (column.PhysicalType == "Int64" && decimalPrecision > 18)
                        diagnostics.Add(new SchemaDiagnostic(DecimalPhysicalMismatch,
                            $"Column '{column.Name}' decimal precision {decimalPrecision} exceeds the maximum 18 for physical type 'Int64'."));
                    if (column.PhysicalType == "FixedLenByteArray" && column.TypeLength > 0 &&
                        decimalPrecision > GetMaximumDecimalPrecision(column.TypeLength))
                        diagnostics.Add(new SchemaDiagnostic(DecimalPhysicalMismatch,
                            $"Column '{column.Name}' decimal precision {decimalPrecision} exceeds the capacity of its {column.TypeLength}-byte physical type."));
                    if (IsDecimalClr(column.ClrTypeName) && decimalPrecision > 29)
                        diagnostics.Add(new SchemaDiagnostic(InvalidDecimalDefinition,
                            $"Column '{column.Name}' decimal precision {decimalPrecision} exceeds the System.Decimal maximum of 29."));
                }
                if (IsDecimalClr(column.ClrTypeName) && logicalType.Value.Scale is int decimalScale && decimalScale > 28)
                    diagnostics.Add(new SchemaDiagnostic(InvalidDecimalDefinition,
                        $"Column '{column.Name}' decimal scale {decimalScale} exceeds the System.Decimal maximum of 28."));
                if (!IsDecimalClr(column.ClrTypeName) && !IsUtf8ByteArrayClr(column.ClrTypeName))
                    diagnostics.Add(new SchemaDiagnostic(LogicalClrMismatch,
                        $"Column '{column.Name}' logical type 'Decimal' requires CLR type decimal/decimal? or a binary CLR carrier."));
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

    static bool IsUtf8ByteArrayClr(string clrType)
        => clrType is
            "byte[]" or "byte[]?" or
            "global::System.ReadOnlyMemory<byte>" or "global::System.ReadOnlyMemory<byte>?";

    static bool IsStringClr(string clrType)
        => clrType is "string" or "string?";

    static bool IsGuidClr(string clrType)
        => clrType is "global::System.Guid" or "global::System.Guid?";

    static bool IsDecimalClr(string clrType)
        => clrType is "decimal" or "decimal?";

    static uint GetDecimalTypeLength(int precision)
    {
        var bytes = Math.Ceiling(((precision / Math.Log10(2)) + 1) / 8);
        return checked((uint)bytes);
    }

    static int GetMaximumDecimalPrecision(uint typeLength)
    {
        var precision = Math.Floor(((double)typeLength * 8 - 1) * Math.Log10(2));
        return precision >= int.MaxValue ? int.MaxValue : checked((int)precision);
    }

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
            SpecialType.System_Decimal => isNullable ? "decimal?" : "decimal",
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
            "global::System.Guid" => isNullable ? "global::System.Guid?" : "global::System.Guid",
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

        if (column.TypeLength > 0)
        {
            if (column.Encodings.IsDefaultOrEmpty)
                builder.Append(", default");
            builder.Append(", ").Append(column.TypeLength);
        }

        if (column.BloomFilter)
        {
            if (column.TypeLength == 0)
            {
                if (column.Encodings.IsDefaultOrEmpty)
                    builder.Append(", default");
                builder.Append(", 0");
            }
            builder.Append(", bloomFilter: new global::Plank.Schema.ParquetBloomFilterOptions { FalsePositiveProbability = ")
                .Append(column.BloomFilterFalsePositiveProbability.ToString("R", CultureInfo.InvariantCulture));
            if (column.BloomFilterExpectedDistinctValueCount > 0)
                builder.Append(", ExpectedDistinctValueCount = ")
                    .Append(column.BloomFilterExpectedDistinctValueCount);
            if (column.BloomFilterMaximumBytes != 128U * 1024 * 1024)
                builder.Append(", MaximumBytes = ").Append(column.BloomFilterMaximumBytes);
            builder.Append(" }");
        }

        builder.Append(')');
        return builder.ToString();
    }

    readonly struct SchemaColumn
    {
        public SchemaColumn(string name, string physicalType, string repetition, string clrTypeName,
            LogicalTypeSpec? logicalType, string rowPropertyName, ImmutableArray<string> encodings, uint typeLength,
            string? converterTypeName, int? fieldId,
            bool bloomFilter, double bloomFilterFalsePositiveProbability, uint bloomFilterExpectedDistinctValueCount,
            uint bloomFilterMaximumBytes)
        {
            Name = name;
            PhysicalType = physicalType;
            Repetition = repetition;
            ClrTypeName = clrTypeName;
            LogicalType = logicalType;
            RowPropertyName = rowPropertyName;
            Encodings = encodings;
            TypeLength = typeLength;
            ConverterTypeName = converterTypeName;
            FieldId = fieldId;
            BloomFilter = bloomFilter;
            BloomFilterFalsePositiveProbability = bloomFilterFalsePositiveProbability;
            BloomFilterExpectedDistinctValueCount = bloomFilterExpectedDistinctValueCount;
            BloomFilterMaximumBytes = bloomFilterMaximumBytes;
        }

        public string Name { get; }

        public string PhysicalType { get; }

        public string Repetition { get; }

        public string ClrTypeName { get; }

        public LogicalTypeSpec? LogicalType { get; }

        public string RowPropertyName { get; }

        public ImmutableArray<string> Encodings { get; }

        public uint TypeLength { get; }

        public string? ConverterTypeName { get; }

        public int? FieldId { get; }

        public bool BloomFilter { get; }

        public double BloomFilterFalsePositiveProbability { get; }

        public uint BloomFilterExpectedDistinctValueCount { get; }

        public uint BloomFilterMaximumBytes { get; }
    }

    readonly struct ConverterSpec
    {
        public ConverterSpec(string typeName, string physicalClrTypeName)
        {
            TypeName = typeName;
            PhysicalClrTypeName = physicalClrTypeName;
        }

        public string TypeName { get; }

        public string PhysicalClrTypeName { get; }
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
