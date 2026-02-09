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
        messageFormat: "Property '{0}' must be static and of type Plank.Schema.ParquetSchema",
        category: "Plank.SourceGen",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    static readonly DiagnosticDescriptor UnsupportedPropertyType = new(
        id: "PLANKGEN002",
        title: "Unsupported schema column mapping",
        messageFormat: "Column '{0}' on schema property '{1}' has unsupported row mapping for repetition '{2}' and physical type '{3}'. Use [RowApiTypeHint] for ambiguous columns.",
        category: "Plank.SourceGen",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    static readonly DiagnosticDescriptor UnsupportedSchemaInitializer = new(
        id: "PLANKGEN003",
        title: "Unsupported schema initializer",
        messageFormat: "Schema property '{0}' must use an inline analyzable initializer with explicit Column(...) entries",
        category: "Plank.SourceGen",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    static readonly DiagnosticDescriptor InvalidTypeHint = new(
        id: "PLANKGEN004",
        title: "Invalid [RowApiTypeHint] mapping",
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
        if (!schemaProperty.IsStatic || schemaProperty.Type.ToDisplayString() != "Plank.Schema.ParquetSchema")
        {
            context.ReportDiagnostic(Diagnostic.Create(InvalidTarget, schemaProperty.Locations.FirstOrDefault(), schemaProperty.Name));
            return;
        }

        if (!TryExtractTypeHints(schemaProperty, out var typeHints, out var typeHintError))
        {
            context.ReportDiagnostic(Diagnostic.Create(InvalidTypeHint, schemaProperty.Locations.FirstOrDefault(), typeHintError));
            return;
        }

        var columns = TryExtractColumns(schemaProperty);
        if (columns is null)
        {
            context.ReportDiagnostic(Diagnostic.Create(UnsupportedSchemaInitializer, schemaProperty.Locations.FirstOrDefault(), schemaProperty.Name));
            return;
        }

        var mappedColumns = ImmutableArray.CreateBuilder<MappedColumn>(columns.Value.Length);
        foreach (var column in columns.Value)
        {
            if (!TryMapColumn(column, typeHints, out var mapped))
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

        var columnNames = new HashSet<string>(columns.Value.Select(static c => c.Name), StringComparer.Ordinal);
        foreach (var hintColumnName in typeHints.Keys)
        {
            if (columnNames.Contains(hintColumnName))
                continue;

            context.ReportDiagnostic(Diagnostic.Create(
                InvalidTypeHint,
                schemaProperty.Locations.FirstOrDefault(),
                $"[RowApiTypeHint] references unknown column '{hintColumnName}' on schema property '{schemaProperty.Name}'."));
            return;
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
        builder.Append("    public static global::Plank.Schema.ParquetSchema Schema => ").Append(schemaAccess).AppendLine(";");
        builder.AppendLine();
        builder.AppendLine("    public static Writer CreateWriter(global::Plank.Writing.RowGroupWriter rowGroupWriter, int rowCount)");
        builder.AppendLine("        => new(rowGroupWriter, rowCount);");
        builder.AppendLine();
        builder.AppendLine("    public static PipelineWriter CreatePipelineWriter(global::Plank.Writing.ParquetWriter writer, int rowCount)");
        builder.AppendLine("        => new(writer, rowCount);");
        builder.AppendLine();
        builder.AppendLine("    public struct Writer");
        builder.AppendLine("    {");
        builder.AppendLine("        global::Plank.Writing.RowGroupWriter _rowGroupWriter;");
        builder.AppendLine("        int _rowCount;");
        builder.AppendLine("        int _index;");
        builder.AppendLine("        bool _written;");

        for (var i = 0; i < columns.Length; i++)
            builder.Append("        ").Append(GetBufferType(columns[i].ClrTypeName)).Append(" _").Append(columns[i].PropertyName).AppendLine(";");

        builder.AppendLine();
        builder.AppendLine("        internal Writer(int rowCount)");
        builder.AppendLine("        {");
        builder.AppendLine("            if (rowCount < 0)");
        builder.AppendLine("                throw new global::System.ArgumentOutOfRangeException(nameof(rowCount), rowCount, \"Row count must be non-negative.\");");
        builder.AppendLine();
        builder.AppendLine("            _rowGroupWriter = default;");
        builder.AppendLine("            _rowCount = rowCount;");
        builder.AppendLine("            _index = 0;");
        builder.AppendLine("            _written = false;");
        for (var i = 0; i < columns.Length; i++)
            builder.Append("            _").Append(columns[i].PropertyName).Append(" = rowCount == 0 ? global::System.Array.Empty<").Append(columns[i].ClrTypeName).Append(">() : ").Append(GetBufferAllocation(columns[i].ClrTypeName)).AppendLine(";");
        builder.AppendLine("        }");
        builder.AppendLine();
        builder.AppendLine("        internal Writer(global::Plank.Writing.RowGroupWriter rowGroupWriter, int rowCount)");
        builder.AppendLine("        {");
        builder.AppendLine("            this = new Writer(rowCount);");
        builder.AppendLine("            _rowGroupWriter = rowGroupWriter;");
        builder.AppendLine("        }");
        builder.AppendLine();
        builder.AppendLine("        internal void Bind(global::Plank.Writing.RowGroupWriter rowGroupWriter)");
        builder.AppendLine("            => _rowGroupWriter = rowGroupWriter;");
        builder.AppendLine();
        builder.AppendLine("        internal bool IsFull => _index == _rowCount;");
        builder.AppendLine();
        builder.AppendLine("        internal bool IsEmpty => _index == 0;");
        builder.AppendLine();
        builder.AppendLine("        internal void ResetForReuse()");
        builder.AppendLine("        {");
        builder.AppendLine("            _index = 0;");
        builder.AppendLine("            _written = false;");
        builder.AppendLine("            _rowGroupWriter = default;");
        builder.AppendLine("        }");
        builder.AppendLine();
        builder.AppendLine("        public Row GetRow()");
        builder.AppendLine("        {");
        builder.AppendLine("            if (_written)");
        builder.AppendLine("                throw new global::System.InvalidOperationException(\"Rows are already written for this row group.\");");
        builder.AppendLine("            if (_index >= _rowCount)");
        builder.AppendLine("                throw new global::System.InvalidOperationException(\"No more row slots are available.\");");
        builder.AppendLine();
        builder.Append("            return new Row(_index");
        for (var i = 0; i < columns.Length; i++)
            builder.Append(", _").Append(columns[i].PropertyName);
        builder.AppendLine(");");
        builder.AppendLine("        }");
        builder.AppendLine();
        builder.AppendLine("        public void Next()");
        builder.AppendLine("        {");
        builder.AppendLine("            if (_written)");
        builder.AppendLine("                throw new global::System.InvalidOperationException(\"Rows are already written for this row group.\");");
        builder.AppendLine("            if (_index >= _rowCount)");
        builder.AppendLine("                throw new global::System.InvalidOperationException(\"No more row slots are available.\");");
        builder.AppendLine("            _index++;");
        builder.AppendLine("        }");
        builder.AppendLine();
        builder.AppendLine("        public async global::System.Threading.Tasks.ValueTask WriteAsync(global::System.Threading.CancellationToken cancellationToken = default)");
        builder.AppendLine("        {");
        builder.AppendLine("            if (_written)");
        builder.AppendLine("                throw new global::System.InvalidOperationException(\"This row writer was already written.\");");
        builder.AppendLine("            if (_index != _rowCount)");
        builder.AppendLine("                throw new global::System.InvalidOperationException(\"All rows must be filled before writing columns.\");");
        builder.AppendLine();
        builder.AppendLine("            _written = true;");

        for (var i = 0; i < columns.Length; i++)
            builder.Append("            await _rowGroupWriter.WriteAsync(Schema.Columns[")
                .Append(i)
                .Append("], _")
                .Append(columns[i].PropertyName)
                .AppendLine(", cancellationToken).ConfigureAwait(false);");

        builder.AppendLine("        }");
        builder.AppendLine("    }");
        builder.AppendLine();
        builder.AppendLine("    public sealed class PipelineWriter");
        builder.AppendLine("    {");
        builder.AppendLine("        readonly global::Plank.Writing.ParquetWriter _writer;");
        builder.AppendLine("        Writer _active;");
        builder.AppendLine("        Writer _pending;");
        builder.AppendLine("        global::System.Threading.Tasks.Task _inFlight;");
        builder.AppendLine("        global::System.Runtime.ExceptionServices.ExceptionDispatchInfo _fault;");
        builder.AppendLine("        bool _hasFault;");
        builder.AppendLine();
        builder.AppendLine("        internal PipelineWriter(global::Plank.Writing.ParquetWriter writer, int rowCount)");
        builder.AppendLine("        {");
        builder.AppendLine("            _writer = writer ?? throw new global::System.ArgumentNullException(nameof(writer));");
        builder.AppendLine("            _active = new Writer(rowCount);");
        builder.AppendLine("            _pending = new Writer(rowCount);");
        builder.AppendLine("            _inFlight = global::System.Threading.Tasks.Task.CompletedTask;");
        builder.AppendLine("            _fault = null!;");
        builder.AppendLine("            _hasFault = false;");
        builder.AppendLine("        }");
        builder.AppendLine();
        builder.AppendLine("        public Row GetRow()");
        builder.AppendLine("        {");
        builder.AppendLine("            ThrowIfFaulted();");
        builder.AppendLine("            return _active.GetRow();");
        builder.AppendLine("        }");
        builder.AppendLine();
        builder.AppendLine("        public void Next()");
        builder.AppendLine("        {");
        builder.AppendLine("            ThrowIfFaulted();");
        builder.AppendLine("            _active.Next();");
        builder.AppendLine("        }");
        builder.AppendLine();
        builder.AppendLine("        public async global::System.Threading.Tasks.ValueTask FlushAsync(global::System.Threading.CancellationToken cancellationToken = default)");
        builder.AppendLine("        {");
        builder.AppendLine("            ThrowIfFaulted();");
        builder.AppendLine("            if (!_active.IsFull)");
        builder.AppendLine("                throw new global::System.InvalidOperationException(\"All rows must be filled before flushing the row group.\");");
        builder.AppendLine("            if (_active.IsEmpty)");
        builder.AppendLine("                throw new global::System.InvalidOperationException(\"Row count must be greater than zero.\");");
        builder.AppendLine();
        builder.AppendLine("            await DrainInFlightAsync().ConfigureAwait(false);");
        builder.AppendLine();
        builder.AppendLine("            var writing = _active;");
        builder.AppendLine("            _active = _pending;");
        builder.AppendLine("            _active.ResetForReuse();");
        builder.AppendLine("            _pending = writing;");
        builder.AppendLine("            _inFlight = WritePendingAsync(cancellationToken);");
        builder.AppendLine("        }");
        builder.AppendLine();
        builder.AppendLine("        public async global::System.Threading.Tasks.ValueTask CompleteAsync()");
        builder.AppendLine("            => await DrainInFlightAsync().ConfigureAwait(false);");
        builder.AppendLine();
        builder.AppendLine("        async global::System.Threading.Tasks.Task DrainInFlightAsync()");
        builder.AppendLine("        {");
        builder.AppendLine("            try");
        builder.AppendLine("            {");
        builder.AppendLine("                await _inFlight.ConfigureAwait(false);");
        builder.AppendLine("            }");
        builder.AppendLine("            catch (global::System.Exception ex)");
        builder.AppendLine("            {");
        builder.AppendLine("                if (!_hasFault)");
        builder.AppendLine("                {");
        builder.AppendLine("                    _fault = global::System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex);");
        builder.AppendLine("                    _hasFault = true;");
        builder.AppendLine("                }");
        builder.AppendLine("            }");
        builder.AppendLine();
        builder.AppendLine("            ThrowIfFaulted();");
        builder.AppendLine("        }");
        builder.AppendLine();
        builder.AppendLine("        async global::System.Threading.Tasks.Task WritePendingAsync(global::System.Threading.CancellationToken cancellationToken)");
        builder.AppendLine("        {");
        builder.AppendLine("            try");
        builder.AppendLine("            {");
        builder.AppendLine("                var rowGroupWriter = _writer.StartRowGroup();");
        builder.AppendLine("                _pending.Bind(rowGroupWriter);");
        builder.AppendLine("                await _pending.WriteAsync(cancellationToken).ConfigureAwait(false);");
        builder.AppendLine("            }");
        builder.AppendLine("            catch (global::System.Exception ex)");
        builder.AppendLine("            {");
        builder.AppendLine("                if (!_hasFault)");
        builder.AppendLine("                {");
        builder.AppendLine("                    _fault = global::System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex);");
        builder.AppendLine("                    _hasFault = true;");
        builder.AppendLine("                }");
        builder.AppendLine("                throw;");
        builder.AppendLine("            }");
        builder.AppendLine("        }");
        builder.AppendLine();
        builder.AppendLine("        void ThrowIfFaulted()");
        builder.AppendLine("        {");
        builder.AppendLine("            if (_hasFault)");
        builder.AppendLine("                _fault.Throw();");
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

    static bool TryMapColumn(SchemaColumn column, ImmutableDictionary<string, string> typeHints, out MappedColumn mapped)
    {
        if (column.Repetition == "Repeated")
        {
            mapped = default;
            return false;
        }

        if (typeHints.TryGetValue(column.Name, out var hintedClrType))
        {
            if (!IsSupportedMapping(column, hintedClrType))
            {
                mapped = default;
                return false;
            }

            mapped = new MappedColumn(column.Name, ToIdentifier(column.Name), hintedClrType);
            return true;
        }

        var clrType = column.PhysicalType switch
        {
            "Boolean" => "bool",
            "Int32" when column.Repetition == "Optional" => "int?",
            "Int32" => "int",
            "Int64" when column.Repetition == "Optional" => "long?",
            "Int64" => "long",
            "Float" => "float",
            "Double" when column.Repetition == "Optional" => "double?",
            "Double" => "double",
            "ByteArray" when column.Repetition == "Optional" => "string?",
            "ByteArray" => "byte[]",
            _ => string.Empty
        };
        if (clrType.Length == 0)
        {
            mapped = default;
            return false;
        }

        mapped = new MappedColumn(column.Name, ToIdentifier(column.Name), clrType);
        return true;
    }

    static bool IsSupportedMapping(SchemaColumn column, string clrType)
    {
        if (column.Repetition == "Optional")
            return column.PhysicalType switch
            {
                "Int32" => clrType is "int" or "int?",
                "Int64" => clrType is "long" or "long?" or "global::System.DateTime" or "global::System.DateTime?",
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

    static bool TryExtractTypeHints(IPropertySymbol schemaProperty, out ImmutableDictionary<string, string> typeHints, out string error)
    {
        var builder = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        error = string.Empty;
        foreach (var attribute in schemaProperty.GetAttributes())
        {
            if (attribute.AttributeClass?.ToDisplayString() != "Plank.Schema.RowApiTypeHintAttribute")
                continue;
            if (attribute.ConstructorArguments.Length != 2 ||
                attribute.ConstructorArguments[0].Value is not string columnName ||
                attribute.ConstructorArguments[1].Kind is not TypedConstantKind.Type ||
                attribute.ConstructorArguments[1].Value is not ITypeSymbol clrTypeSymbol)
            {
                error = $"[RowApiTypeHint] on schema property '{schemaProperty.Name}' is malformed.";
                typeHints = ImmutableDictionary<string, string>.Empty;
                return false;
            }

            if (!TryFormatHintType(clrTypeSymbol, out var clrTypeName))
            {
                error = $"[RowApiTypeHint(\"{columnName}\", {clrTypeSymbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat)})] uses an unsupported CLR type.";
                typeHints = ImmutableDictionary<string, string>.Empty;
                return false;
            }

            if (builder.ContainsKey(columnName))
            {
                error = $"Duplicate [RowApiTypeHint] for column '{columnName}' on schema property '{schemaProperty.Name}'.";
                typeHints = ImmutableDictionary<string, string>.Empty;
                return false;
            }

            builder[columnName] = clrTypeName;
        }

        typeHints = builder.ToImmutable();
        return true;
    }

    static bool TryFormatHintType(ITypeSymbol clrTypeSymbol, out string clrTypeName)
    {
        clrTypeName = clrTypeSymbol switch
        {
            IArrayTypeSymbol { ElementType.SpecialType: SpecialType.System_Byte } => "byte[]",
            _ when clrTypeSymbol.SpecialType == SpecialType.System_Boolean => "bool",
            _ when clrTypeSymbol.SpecialType == SpecialType.System_Int32 => "int",
            _ when clrTypeSymbol.SpecialType == SpecialType.System_Int64 => "long",
            _ when clrTypeSymbol.SpecialType == SpecialType.System_Single => "float",
            _ when clrTypeSymbol.SpecialType == SpecialType.System_Double => "double",
            _ when clrTypeSymbol.SpecialType == SpecialType.System_String => "string",
            _ => clrTypeSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
        };

        if (clrTypeSymbol is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullableNamed &&
            nullableNamed.TypeArguments.Length == 1)
        {
            if (!TryFormatHintType(nullableNamed.TypeArguments[0], out var underlyingClrTypeName))
                return false;

            clrTypeName = $"{underlyingClrTypeName}?";
        }

        return clrTypeName is
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
    }

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

    static ImmutableArray<SchemaColumn>? TryExtractColumns(IPropertySymbol schemaProperty)
    {
        var declaration = schemaProperty.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax() as PropertyDeclarationSyntax;
        if (declaration?.Initializer?.Value is null)
            return null;

        if (!TryExtractSchemaArgument(declaration.Initializer.Value, out var schemaArgument))
            return null;

        if (!TryExtractColumnExpressions(schemaArgument, out var columnExpressions))
            return null;

        var columns = ImmutableArray.CreateBuilder<SchemaColumn>(columnExpressions.Count);
        foreach (var expression in columnExpressions)
        {
            if (!TryParseColumnExpression(expression, out var column))
                return null;
            columns.Add(column);
        }
        return columns.ToImmutable();
    }

    static bool TryExtractSchemaArgument(ExpressionSyntax expression, out ExpressionSyntax argument)
    {
        argument = null!;
        switch (expression)
        {
            case ObjectCreationExpressionSyntax { ArgumentList.Arguments.Count: > 0 } created:
                argument = created.ArgumentList.Arguments[0].Expression;
                return true;
            case ImplicitObjectCreationExpressionSyntax { ArgumentList.Arguments.Count: > 0 } created:
                argument = created.ArgumentList.Arguments[0].Expression;
                return true;
            default:
                return false;
        }
    }

    static bool TryExtractColumnExpressions(ExpressionSyntax argument, out SeparatedSyntaxList<ExpressionSyntax> expressions)
    {
        expressions = default;
        switch (argument)
        {
            case CollectionExpressionSyntax collection:
                expressions = collection.Elements
                    .OfType<ExpressionElementSyntax>()
                    .Select(static e => e.Expression)
                    .ToSeparatedSyntaxList();
                return expressions.Count > 0;
            case InvocationExpressionSyntax invocation when invocation.Expression.ToString().Contains("ImmutableArray.Create", StringComparison.Ordinal):
                expressions = invocation.ArgumentList.Arguments.Select(static a => a.Expression).ToSeparatedSyntaxList();
                return expressions.Count > 0;
            default:
                return false;
        }
    }

    static bool TryParseColumnExpression(ExpressionSyntax expression, out SchemaColumn column)
    {
        column = default;
        if (!TryExtractColumnArguments(expression, out var arguments))
            return false;
        if (arguments.Count < 3)
            return false;

        if (arguments[0].Expression is not LiteralExpressionSyntax { Token.ValueText: { } columnName })
            return false;

        if (!TryParseEnumMember(arguments[1].Expression, out var physicalType))
            return false;

        var repetition = "Unspecified";
        if (TryExtractColumnOptionsArguments(arguments[2].Expression, out var optionArguments) &&
            optionArguments.Count > 0 &&
            TryParseEnumMember(optionArguments[0].Expression, out var repetitionValue))
            repetition = repetitionValue;

        column = new SchemaColumn(columnName, physicalType, repetition);
        return true;
    }

    static bool TryExtractColumnArguments(ExpressionSyntax expression, out SeparatedSyntaxList<ArgumentSyntax> arguments)
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

    static bool TryExtractColumnOptionsArguments(ExpressionSyntax expression, out SeparatedSyntaxList<ArgumentSyntax> arguments)
        => TryExtractColumnArguments(expression, out arguments);

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
        public SchemaColumn(string name, string physicalType, string repetition)
        {
            Name = name;
            PhysicalType = physicalType;
            Repetition = repetition;
        }

        public string Name { get; }

        public string PhysicalType { get; }

        public string Repetition { get; }
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
