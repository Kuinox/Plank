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
        title: "Invalid [GenerateRowApi] target",
        messageFormat: "Property '{0}' must be static and of type Plank.Schema.RowSchema",
        category: "Plank.SourceGen",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    static readonly DiagnosticDescriptor UnsupportedPropertyType = new(
        id: "PLANKGEN002",
        title: "Unsupported schema column mapping",
        messageFormat: "Column '{0}' on schema property '{1}' has unsupported row mapping for repetition '{2}' and physical type '{3}'",
        category: "Plank.SourceGen",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    static readonly DiagnosticDescriptor UnsupportedSchemaInitializer = new(
        id: "PLANKGEN003",
        title: "Unsupported schema initializer",
        messageFormat: "Schema property '{0}' must use an inline analyzable RowSchema.Create(RowSchema.Column<T>(...)) initializer",
        category: "Plank.SourceGen",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    static readonly DiagnosticDescriptor InvalidTypeHint = new(
        id: "PLANKGEN004",
        title: "Invalid RowSchema column mapping",
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
        var schemaProperties = context.SyntaxProvider.ForAttributeWithMetadataName(
            fullyQualifiedMetadataName: "Plank.Schema.GenerateRowApiAttribute",
            predicate: static (node, _) => node is PropertyDeclarationSyntax,
            transform: static (ctx, _) => (IPropertySymbol)ctx.TargetSymbol);

        context.RegisterSourceOutput(schemaProperties, static (sourceContext, propertySymbol) => Emit(sourceContext, propertySymbol));
    }

    static void Emit(SourceProductionContext context, IPropertySymbol schemaProperty)
    {
        if (!schemaProperty.IsStatic || schemaProperty.Type.ToDisplayString() != "Plank.Schema.RowSchema")
        {
            context.ReportDiagnostic(Diagnostic.Create(InvalidTarget, schemaProperty.Locations.FirstOrDefault(), schemaProperty.Name));
            return;
        }

        if (!TryExtractColumns(schemaProperty, out var columns, out var extractError))
        {
            var descriptor = extractError.Length == 0 ? UnsupportedSchemaInitializer : InvalidTypeHint;
            var arg = extractError.Length == 0 ? schemaProperty.Name : extractError;
            context.ReportDiagnostic(Diagnostic.Create(descriptor, schemaProperty.Locations.FirstOrDefault(), arg));
            return;
        }

        var schemaDiagnostics = ValidateSchemaColumns(columns);
        for (var i = 0; i < schemaDiagnostics.Length; i++)
            context.ReportDiagnostic(Diagnostic.Create(schemaDiagnostics[i].Descriptor,
                schemaProperty.Locations.FirstOrDefault(), schemaDiagnostics[i].Message));
        if (schemaDiagnostics.Length > 0)
            return;

        var mappedColumns = ImmutableArray.CreateBuilder<MappedColumn>(columns.Length);
        foreach (var column in columns)
        {
            if (!TryMapColumn(column, out var mapped))
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    UnsupportedPropertyType,
                    schemaProperty.Locations.FirstOrDefault(),
                    column.Name,
                    schemaProperty.Name,
                    column.Repetition,
                    column.PhysicalType));
                return;
            }
            mappedColumns.Add(mapped);
        }

        var source = BuildSource(schemaProperty, mappedColumns.ToImmutable());
        var containingName = schemaProperty.ContainingType.Name;
        context.AddSource($"{containingName}.{schemaProperty.Name}.PlankRow.g.cs", source);
    }

    static string BuildSource(IPropertySymbol schemaProperty, ImmutableArray<MappedColumn> columns)
    {
        var namespaceName = schemaProperty.ContainingNamespace is { IsGlobalNamespace: false }
            ? schemaProperty.ContainingNamespace.ToDisplayString()
            : null;
        var generatedTypeName = $"{schemaProperty.ContainingType.Name}_{schemaProperty.Name}PlankRow";
        var schemaAccess = $"{schemaProperty.ContainingType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)}.{schemaProperty.Name}";

        var builder = new StringBuilder();
        builder.AppendLine("// <auto-generated />");
        if (namespaceName is not null)
        {
            builder.Append("namespace ").Append(namespaceName).AppendLine(";");
            builder.AppendLine();
        }

        builder.Append("public static class ").Append(generatedTypeName).AppendLine();
        builder.AppendLine("{");
        builder.Append("    public static global::Plank.Schema.ParquetSchema Schema => ").Append(schemaAccess).AppendLine(".ParquetSchema;");
        builder.AppendLine();
        builder.AppendLine("    public static Writer CreateWriter(global::Plank.Writing.RowGroupWriter rowGroupWriter, global::Plank.Writing.ParquetWriterOptions? options = null)");
        builder.AppendLine("        => new(rowGroupWriter, options ?? global::Plank.Writing.ParquetWriterOptions.Default);");
        builder.AppendLine();
        builder.AppendLine("    public static PipelineWriter CreatePipelineWriter(global::System.IO.Stream stream, global::Plank.Writing.ParquetWriterOptions? options = null)");
        builder.AppendLine("        => new(stream, options ?? global::Plank.Writing.ParquetWriterOptions.Default);");
        builder.AppendLine();
        builder.AppendLine("    public static PipelineWriter CreatePipelineWriter(global::System.IO.Stream stream, uint maxParallelism, global::Plank.Writing.ParquetWriterOptions? options = null)");
        builder.AppendLine("        => new(stream, maxParallelism, options ?? global::Plank.Writing.ParquetWriterOptions.Default);");
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
        builder.AppendLine("            _slot = new BufferSlot(rowGroupWriter, checked((int)options.RowApiBatchSize));");
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
        builder.AppendLine("        BufferSlot _active;");
        builder.AppendLine("        bool _completed;");
        builder.AppendLine();
        builder.AppendLine("        internal PipelineWriter(global::System.IO.Stream stream, global::Plank.Writing.ParquetWriterOptions options)");
        builder.AppendLine("            : this(stream, options.RowApiMaxParallelism, options)");
        builder.AppendLine("        {");
        builder.AppendLine("        }");
        builder.AppendLine();
        builder.AppendLine("        internal PipelineWriter(global::System.IO.Stream stream, uint maxParallelism, global::Plank.Writing.ParquetWriterOptions options)");
        builder.AppendLine("            : base(stream, Schema, maxParallelism, options)");
        builder.AppendLine("        {");
        builder.AppendLine("            _ = stream ?? throw new global::System.ArgumentNullException(nameof(stream));");
        builder.AppendLine("            _ = options ?? throw new global::System.ArgumentNullException(nameof(options));");
        builder.AppendLine("            if (maxParallelism == 0)");
        builder.AppendLine("                throw new global::System.ArgumentOutOfRangeException(nameof(maxParallelism), maxParallelism, \"Max parallelism must be greater than zero.\");");
        builder.AppendLine();
        builder.AppendLine("            _rowBatchSize = checked((int)options.RowApiBatchSize);");
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
        builder.AppendLine("        protected override void ResetSlotForReuse(BufferSlot slot)");
        builder.AppendLine("            => slot.ResetForReuse();");
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
        builder.AppendLine("    sealed class BufferSlot");
        builder.AppendLine("    {");
        builder.AppendLine("        int _index;");
        builder.AppendLine("        readonly int _rowCount;");
        for (var i = 0; i < columns.Length; i++)
            builder.Append("        readonly ").Append(GetBufferType(columns[i].ClrTypeName)).Append(" _").Append(columns[i].PropertyName).AppendLine(";");
        for (var i = 0; i < columns.Length; i++)
            builder.Append("        readonly global::Plank.Writing.SerializedColumn _serialized").Append(columns[i].PropertyName).AppendLine(";");
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
            builder.Append("            _serialized").Append(columns[i].PropertyName).Append(" = rowGroupWriter.CreateSerializedColumn();").AppendLine();
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
            builder.Append("            _serialized").Append(columns[i].PropertyName).Append(" = writer.CreateSerializedColumn();").AppendLine();
        builder.AppendLine("        }");
        builder.AppendLine();
        builder.AppendLine("        internal bool IsFull => _index == _rowCount;");
        builder.AppendLine();
        builder.AppendLine("        internal bool IsEmpty => _index == 0;");
        builder.AppendLine();
        builder.AppendLine("        internal Row GetRow()");
        builder.AppendLine("        {");
        builder.AppendLine("            if (_index >= _rowCount)");
        builder.AppendLine("                throw new global::System.InvalidOperationException(\"No more row slots are available.\");");
        builder.AppendLine();
        builder.Append("            return new Row(_index");
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
            builder.Append("            _serialized").Append(columns[i].PropertyName).Append(".Serialize(Schema.Columns[").Append(i).Append("], new global::System.ReadOnlySpan<").Append(columns[i].ClrTypeName).Append(">(_").Append(columns[i].PropertyName).Append(", 0, _index));").AppendLine();
        builder.AppendLine("        }");
        builder.AppendLine();
        builder.AppendLine("        internal void WriteSerialized(global::Plank.Writing.RowGroupWriter rowGroupWriter)");
        builder.AppendLine("        {");
        for (var i = 0; i < columns.Length; i++)
            builder.Append("            rowGroupWriter.Write(_serialized").Append(columns[i].PropertyName).Append(");").AppendLine();
        builder.AppendLine("        }");
        builder.AppendLine();
        builder.AppendLine("        internal void ResetForReuse()");
        builder.AppendLine("        {");
        builder.AppendLine("            _index = 0;");
        builder.AppendLine("        }");
        builder.AppendLine("        }");
        builder.AppendLine("    }");
        builder.AppendLine();
        builder.AppendLine("    public readonly ref struct Row");
        builder.AppendLine("    {");
        builder.AppendLine("        readonly int _index;");
        for (var i = 0; i < columns.Length; i++)
            builder.Append("        readonly ").Append(GetBufferType(columns[i].ClrTypeName)).Append(" _").Append(columns[i].PropertyName).AppendLine(";");
        builder.AppendLine();
        builder.Append("        internal Row(int index");
        for (var i = 0; i < columns.Length; i++)
            builder.Append(", ").Append(GetBufferType(columns[i].ClrTypeName)).Append(' ').Append(ToParameterName(columns[i].PropertyName));
        builder.AppendLine(")");
        builder.AppendLine("        {");
        builder.AppendLine("            _index = index;");
        for (var i = 0; i < columns.Length; i++)
            builder.Append("            _").Append(columns[i].PropertyName).Append(" = ").Append(ToParameterName(columns[i].PropertyName)).AppendLine(";");
        builder.AppendLine("        }");
        builder.AppendLine();
        for (var i = 0; i < columns.Length; i++)
        {
            builder.Append("        public ref ").Append(columns[i].ClrTypeName).Append(' ').Append(columns[i].PropertyName).Append(" => ref _").Append(columns[i].PropertyName).AppendLine("[_index];");
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

        mapped = new MappedColumn(column.Name, ToIdentifier(column.Name), column.ClrTypeName);
        return true;
    }

    static bool IsSupportedMapping(SchemaColumn column, string clrType)
    {
        if (column.Repetition == "Optional")
            return column.PhysicalType switch
            {
                "Int32" => clrType is "int" or "int?" or "global::System.DateOnly" or "global::System.DateOnly?",
                "Int64" => clrType is
                    "long" or "long?"
                    or "global::System.DateTime" or "global::System.DateTime?"
                    or "global::System.DateTimeOffset" or "global::System.DateTimeOffset?"
                    or "global::System.TimeOnly" or "global::System.TimeOnly?",
                "Double" => clrType is "double" or "double?",
                "ByteArray" => clrType is "string" or "string?" or "byte[]" or "byte[]?",
                _ => false
            };

        return column.PhysicalType switch
        {
            "Boolean" => clrType == "bool",
            "Int32" => clrType is "int" or "global::System.DateOnly",
            "Int64" => clrType is "long" or "global::System.DateTime" or "global::System.DateTimeOffset" or "global::System.TimeOnly",
            "Float" => clrType == "float",
            "Double" => clrType == "double",
            "ByteArray" => clrType is "string" or "byte[]",
            _ => false
        };
    }

    static bool IsSupportedClrType(string clrTypeName)
        => clrTypeName is
            "bool" or "bool?" or
            "int" or "int?" or
            "long" or "long?" or
            "float" or "float?" or
            "double" or "double?" or
            "string" or "string?" or
            "byte[]" or "byte[]?" or
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

    static bool TryExtractColumns(IPropertySymbol schemaProperty, out ImmutableArray<SchemaColumn> columns, out string error)
    {
        columns = default;
        error = string.Empty;

        var declaration = schemaProperty.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax() as PropertyDeclarationSyntax;
        if (declaration?.Initializer?.Value is null)
            return false;

        if (!TryExtractColumnExpressions(declaration.Initializer.Value, out var columnExpressions))
            return false;

        var builder = ImmutableArray.CreateBuilder<SchemaColumn>(columnExpressions.Count);
        foreach (var expression in columnExpressions)
        {
            if (!TryParseColumnExpression(expression, out var column, out error))
                return false;
            builder.Add(column);
        }

        columns = builder.ToImmutable();
        return true;
    }

    static bool TryExtractColumnExpressions(ExpressionSyntax expression, out SeparatedSyntaxList<ExpressionSyntax> expressions)
    {
        expressions = default;

        switch (expression)
        {
            case InvocationExpressionSyntax { ArgumentList.Arguments.Count: > 0 } invocation when IsCreateInvocation(invocation.Expression):
                expressions = invocation.ArgumentList.Arguments.Select(static a => a.Expression).ToSeparatedSyntaxList();
                return true;
            case ObjectCreationExpressionSyntax { ArgumentList.Arguments.Count: > 0 } created:
                return TryExtractColumnExpressionsFromArgument(created.ArgumentList.Arguments[0].Expression, out expressions);
            case ImplicitObjectCreationExpressionSyntax { ArgumentList.Arguments.Count: > 0 } created:
                return TryExtractColumnExpressionsFromArgument(created.ArgumentList.Arguments[0].Expression, out expressions);
            default:
                return false;
        }
    }

    static bool TryExtractColumnExpressionsFromArgument(ExpressionSyntax expression, out SeparatedSyntaxList<ExpressionSyntax> expressions)
    {
        expressions = default;
        switch (expression)
        {
            case CollectionExpressionSyntax collection:
                expressions = collection.Elements
                    .OfType<ExpressionElementSyntax>()
                    .Select(static e => e.Expression)
                    .ToSeparatedSyntaxList();
                return expressions.Count > 0;
            case InvocationExpressionSyntax invocation when invocation.Expression.ToString().Contains("ImmutableArray.Create", StringComparison.Ordinal):
                expressions = invocation.ArgumentList.Arguments.Select(static a => a.Expression).ToSeparatedSyntaxList();
                return true;
            default:
                return false;
        }
    }

    static bool IsCreateInvocation(ExpressionSyntax expression)
        => expression.ToString().EndsWith(".Create", StringComparison.Ordinal) ||
           expression.ToString() == "Create";

    static bool TryParseColumnExpression(ExpressionSyntax expression, out SchemaColumn column, out string error)
    {
        column = default;
        error = string.Empty;

        if (!TryExtractColumnInvocation(expression, out var genericName, out var arguments))
            return false;
        if (genericName.TypeArgumentList.Arguments.Count != 1 || arguments.Count < 2)
            return false;

        if (arguments[0].Expression is not LiteralExpressionSyntax { Token.ValueText: { } columnName })
            return false;

        if (!TryParseEnumMember(arguments[1].Expression, out var physicalType))
            return false;

        if (!TryNormalizeClrType(genericName.TypeArgumentList.Arguments[0], out var clrTypeName))
        {
            error = $"Unsupported CLR type '{genericName.TypeArgumentList.Arguments[0]}' for schema property column '{columnName}'.";
            return false;
        }

        var repetition = "Unspecified";
        ExpressionSyntax? optionsExpression = null;
        ExpressionSyntax? logicalTypeExpression = null;
        for (var i = 2; i < arguments.Count; i++)
        {
            var argument = arguments[i];
            var name = argument.NameColon?.Name.Identifier.ValueText;
            if (string.Equals(name, "options", StringComparison.Ordinal))
            {
                optionsExpression = argument.Expression;
                continue;
            }
            if (string.Equals(name, "logicalType", StringComparison.Ordinal))
            {
                logicalTypeExpression = argument.Expression;
                continue;
            }

            if (optionsExpression is null && TryExtractColumnOptionsArguments(argument.Expression, out _))
                optionsExpression = argument.Expression;
            else if (logicalTypeExpression is null)
                logicalTypeExpression = argument.Expression;
        }

        if (optionsExpression is not null &&
            TryExtractColumnOptionsArguments(optionsExpression, out var optionArguments) &&
            optionArguments.Count > 0 &&
            TryParseEnumMember(optionArguments[0].Expression, out var repetitionValue))
            repetition = repetitionValue;

        LogicalTypeSpec? logicalType = null;
        if (logicalTypeExpression is not null && !TryParseLogicalType(logicalTypeExpression, out logicalType, out error))
            return false;

        column = new SchemaColumn(columnName, physicalType, repetition, clrTypeName, logicalType);
        return true;
    }

    static bool TryParseLogicalType(ExpressionSyntax expression, out LogicalTypeSpec? logicalType, out string error)
    {
        logicalType = null;
        error = string.Empty;

        if (expression is LiteralExpressionSyntax { RawKind: (int)Microsoft.CodeAnalysis.CSharp.SyntaxKind.NullLiteralExpression })
            return true;

        if (expression is not ObjectCreationExpressionSyntax { Type: { } typeSyntax, ArgumentList: { } arguments })
        {
            error = $"Unsupported logical type expression '{expression}'.";
            return false;
        }

        var typeName = typeSyntax.ToString();
        var lastDot = typeName.LastIndexOf('.');
        var shortName = lastDot >= 0 ? typeName.Substring(lastDot + 1) : typeName;
        if (shortName.Length == 0)
            shortName = typeName;

        switch (shortName)
        {
            case "Date":
            case "String":
            case "Json":
            case "Uuid":
                logicalType = new LogicalTypeSpec(shortName);
                return true;
            case "Time":
            case "Timestamp":
            {
                if (arguments.Arguments.Count != 2)
                {
                    error = $"Logical type '{shortName}' requires (TimeUnit unit, bool isAdjustedToUtc).";
                    return false;
                }
                if (!TryParseEnumMember(arguments.Arguments[0].Expression, out var unit))
                {
                    error = $"Logical type '{shortName}' has invalid time unit expression '{arguments.Arguments[0].Expression}'.";
                    return false;
                }
                if (arguments.Arguments[1].Expression is not LiteralExpressionSyntax { Token.Value: bool isAdjustedToUtc })
                {
                    error = $"Logical type '{shortName}' requires a bool isAdjustedToUtc argument.";
                    return false;
                }

                logicalType = new LogicalTypeSpec(shortName, unit, isAdjustedToUtc);
                return true;
            }
            case "Decimal":
            {
                if (arguments.Arguments.Count != 2)
                {
                    error = "Logical type 'Decimal' requires (int precision, int scale).";
                    return false;
                }

                if (arguments.Arguments[0].Expression is not LiteralExpressionSyntax { Token.Value: int precision })
                {
                    error = "Logical type 'Decimal' precision must be an int literal.";
                    return false;
                }
                if (arguments.Arguments[1].Expression is not LiteralExpressionSyntax { Token.Value: int scale })
                {
                    error = "Logical type 'Decimal' scale must be an int literal.";
                    return false;
                }

                logicalType = new LogicalTypeSpec(shortName, precision: precision, scale: scale);
                return true;
            }
            default:
                error = $"Unsupported logical type '{typeName}'.";
                return false;
        }
    }

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
                break;
            case "Uuid":
                if (column.PhysicalType != "FixedLenByteArray")
                    diagnostics.Add(new SchemaDiagnostic(LogicalPhysicalMismatch,
                        $"Column '{column.Name}' logical type 'Uuid' requires physical type 'FixedLenByteArray'."));
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

    static bool IsTimestampClr(string clrType)
        => clrType is
            "global::System.DateTime" or "global::System.DateTime?"
            or "global::System.DateTimeOffset" or "global::System.DateTimeOffset?";

    static bool IsTimeUnit(string unit)
        => unit is "Millis" or "Micros" or "Nanos";

    static bool TryExtractColumnInvocation(ExpressionSyntax expression, out GenericNameSyntax genericName, out SeparatedSyntaxList<ArgumentSyntax> arguments)
    {
        genericName = null!;
        arguments = default;

        if (expression is not InvocationExpressionSyntax { Expression: { } invoked, ArgumentList: { } argList })
            return false;

        genericName = invoked switch
        {
            GenericNameSyntax direct => direct,
            MemberAccessExpressionSyntax { Name: GenericNameSyntax memberGeneric } => memberGeneric,
            _ => null!
        };
        if (genericName is null || genericName.Identifier.ValueText != "Column")
            return false;

        arguments = argList.Arguments;
        return true;
    }

    static bool TryNormalizeClrType(TypeSyntax clrTypeSyntax, out string clrTypeName)
    {
        var text = clrTypeSyntax.ToString().Replace(" ", string.Empty);
        clrTypeName = text switch
        {
            "bool" => "bool",
            "bool?" => "bool?",
            "int" => "int",
            "int?" => "int?",
            "long" => "long",
            "long?" => "long?",
            "float" => "float",
            "float?" => "float?",
            "double" => "double",
            "double?" => "double?",
            "string" => "string",
            "string?" => "string?",
            "byte[]" => "byte[]",
            "byte[]?" => "byte[]?",
            "DateOnly" => "global::System.DateOnly",
            "DateOnly?" => "global::System.DateOnly?",
            "DateTime" => "global::System.DateTime",
            "DateTime?" => "global::System.DateTime?",
            "DateTimeOffset" => "global::System.DateTimeOffset",
            "DateTimeOffset?" => "global::System.DateTimeOffset?",
            "TimeOnly" => "global::System.TimeOnly",
            "TimeOnly?" => "global::System.TimeOnly?",
            "System.DateOnly" => "global::System.DateOnly",
            "System.DateOnly?" => "global::System.DateOnly?",
            "System.DateTime" => "global::System.DateTime",
            "System.DateTime?" => "global::System.DateTime?",
            "System.DateTimeOffset" => "global::System.DateTimeOffset",
            "System.DateTimeOffset?" => "global::System.DateTimeOffset?",
            "System.TimeOnly" => "global::System.TimeOnly",
            "System.TimeOnly?" => "global::System.TimeOnly?",
            "global::System.DateOnly" => "global::System.DateOnly",
            "global::System.DateOnly?" => "global::System.DateOnly?",
            "global::System.DateTime" => "global::System.DateTime",
            "global::System.DateTime?" => "global::System.DateTime?",
            "global::System.DateTimeOffset" => "global::System.DateTimeOffset",
            "global::System.DateTimeOffset?" => "global::System.DateTimeOffset?",
            "global::System.TimeOnly" => "global::System.TimeOnly",
            "global::System.TimeOnly?" => "global::System.TimeOnly?",
            _ => string.Empty
        };

        return clrTypeName.Length > 0 && IsSupportedClrType(clrTypeName);
    }

    static bool TryExtractColumnOptionsArguments(ExpressionSyntax expression, out SeparatedSyntaxList<ArgumentSyntax> arguments)
    {
        arguments = default;
        switch (expression)
        {
            case ObjectCreationExpressionSyntax { ArgumentList: { } argList }:
                arguments = argList.Arguments;
                return true;
            case ImplicitObjectCreationExpressionSyntax { ArgumentList: { } argList }:
                arguments = argList.Arguments;
                return true;
            default:
                return false;
        }
    }

    static bool TryParseEnumMember(ExpressionSyntax expression, out string memberName)
    {
        memberName = string.Empty;
        if (expression is MemberAccessExpressionSyntax memberAccess)
        {
            memberName = memberAccess.Name.Identifier.ValueText;
            return memberName.Length > 0;
        }

        var text = expression.ToString();
        var lastDot = text.LastIndexOf('.');
        if (lastDot < 0 || lastDot == text.Length - 1)
            return false;
        memberName = text.Substring(lastDot + 1);
        return memberName.Length > 0;
    }

    readonly struct SchemaColumn
    {
        public SchemaColumn(string name, string physicalType, string repetition, string clrTypeName,
            LogicalTypeSpec? logicalType)
        {
            Name = name;
            PhysicalType = physicalType;
            Repetition = repetition;
            ClrTypeName = clrTypeName;
            LogicalType = logicalType;
        }

        public string Name { get; }

        public string PhysicalType { get; }

        public string Repetition { get; }

        public string ClrTypeName { get; }

        public LogicalTypeSpec? LogicalType { get; }
    }

    readonly struct LogicalTypeSpec
    {
        public LogicalTypeSpec(string kind, string? unit = null, bool? isAdjustedToUtc = null, int? precision = null,
            int? scale = null)
        {
            Kind = kind;
            Unit = unit;
            IsAdjustedToUtc = isAdjustedToUtc;
            Precision = precision;
            Scale = scale;
        }

        public string Kind { get; }

        public string? Unit { get; }

        public bool? IsAdjustedToUtc { get; }

        public int? Precision { get; }

        public int? Scale { get; }
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
