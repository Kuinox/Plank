using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;

namespace Plank.SourceGen;

static class NestedParquetRowEmitter
{
    static readonly SymbolDisplayFormat TypeNameFormat = SymbolDisplayFormat.FullyQualifiedFormat
        .WithMiscellaneousOptions(SymbolDisplayFormat.FullyQualifiedFormat.MiscellaneousOptions |
            SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

    internal static bool TryEmit(SourceProductionContext context, INamedTypeSymbol schemaType)
    {
        if (!RequiresNestedEmitter(schemaType))
            return false;

        if (!AllowsAllocatingValues(schemaType))
        {
            context.ReportDiagnostic(Diagnostic.Create(ParquetRowGenerator.AllocatingValueNotAllowed,
                schemaType.Locations.FirstOrDefault(),
                $"Schema '{schemaType.Name}' contains a collection or nested object, which allocates during row materialization. " +
                "Set [ParquetSchema(AllowAllocatingValues = true)] to opt in."));
            return true;
        }

        if (!TryCreateModel(schemaType, out var model, out var error))
        {
            context.ReportDiagnostic(Diagnostic.Create(ParquetRowGenerator.UnsupportedSchemaDeclaration,
                schemaType.Locations.FirstOrDefault(), error));
            return true;
        }

        context.AddSource(GetHintName(schemaType), BuildSource(schemaType, model));
        return true;
    }

    static bool RequiresNestedEmitter(INamedTypeSymbol schemaType)
    {
        foreach (var property in GetProperties(schemaType))
            if (!IsFlatScalar(property.Type))
                return true;
        return false;
    }

    static bool TryCreateModel(INamedTypeSymbol schemaType, out Model model, out string error)
    {
        var roots = new List<Node>();
        foreach (var property in GetProperties(schemaType))
        {
            if (!TryCreatePropertyNode(property, out var root, out error))
            {
                model = default!;
                return false;
            }
            roots.Add(root);
        }

        if (roots.Count == 0)
        {
            model = default!;
            error = $"Schema type '{schemaType.Name}' does not declare any supported non-static properties.";
            return false;
        }

        var leaves = new List<Leaf>();
        var usedNames = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < roots.Count; i++)
        {
            var path = new List<Node>();
            var collections = new List<CollectionLevel>();
            CollectLeaves(roots[i], roots[i], path, collections, repetitionLevel: 0, definitionLevel: 0,
                leaves, usedNames);
        }

        for (var i = 0; i < leaves.Count; i++)
            leaves[i].Ordinal = i;

        for (var i = 0; i < roots.Count; i++)
            if (!ValidateShape(roots[i], out error))
            {
                model = default!;
                return false;
            }

        model = new Model(roots.ToImmutableArray(), leaves.ToImmutableArray());
        error = string.Empty;
        return true;
    }

    static bool TryCreatePropertyNode(IPropertySymbol property, out Node node, out string error)
    {
        var parquetName = property.Name;
        if (!TryReadNameOverride(property, ref parquetName, out error))
        {
            node = default!;
            return false;
        }
        if (parquetName.Length == 0)
        {
            node = default!;
            error = $"Property '{property.Name}' has an empty parquet column name.";
            return false;
        }

        return TryCreateNode(property.Type, property.NullableAnnotation, parquetName, property.Name,
            property, allowLeafOverrides: true, out node, out error);
    }

    static bool TryCreateNode(ITypeSymbol type, NullableAnnotation nullableAnnotation, string parquetName,
        string propertyName, IPropertySymbol? sourceProperty, bool allowLeafOverrides, out Node node,
        out string error)
    {
        if (IsFlatScalar(type))
        {
            if (!TryCreateScalar(type, nullableAnnotation, sourceProperty, allowLeafOverrides, out var scalar,
                    out error))
            {
                node = default!;
                return false;
            }

            node = new Node(NodeKind.Leaf, parquetName, propertyName, GetTypeName(type),
                IsNullable(type, nullableAnnotation), scalar, collectionKind: null, []);
            return true;
        }

        if (type is IArrayTypeSymbol array)
        {
            if (array.Rank != 1)
            {
                node = default!;
                error = $"Nested schema property '{propertyName}' must use one-dimensional arrays.";
                return false;
            }

            if (!TryCreateNode(array.ElementType, array.ElementNullableAnnotation, "element", "Element",
                    sourceProperty, allowLeafOverrides, out var element, out error))
            {
                node = default!;
                return false;
            }
            node = new Node(NodeKind.List, parquetName, propertyName, GetTypeName(type),
                IsNullable(type, nullableAnnotation), scalar: null, "Array", [element]);
            return true;
        }

        if (type is INamedTypeSymbol named && IsList(named))
        {
            if (!TryCreateNode(named.TypeArguments[0], named.TypeArgumentNullableAnnotations[0], "element", "Element",
                    sourceProperty, allowLeafOverrides, out var element, out error))
            {
                node = default!;
                return false;
            }
            node = new Node(NodeKind.List, parquetName, propertyName, GetTypeName(type),
                IsNullable(type, nullableAnnotation), scalar: null, "List", [element]);
            return true;
        }

        if (type is INamedTypeSymbol dictionary && IsDictionary(dictionary))
        {
            if (IsNullable(dictionary.TypeArguments[0], dictionary.TypeArgumentNullableAnnotations[0]))
            {
                node = default!;
                error = $"Map property '{propertyName}' cannot use a nullable key type.";
                return false;
            }
            if (!TryCreateNode(dictionary.TypeArguments[0], dictionary.TypeArgumentNullableAnnotations[0],
                    "key", "Key", sourceProperty: null, allowLeafOverrides: false, out var key, out error) ||
                !TryCreateNode(dictionary.TypeArguments[1], dictionary.TypeArgumentNullableAnnotations[1],
                    "value", "Value", sourceProperty: null, allowLeafOverrides: false, out var value, out error))
            {
                node = default!;
                return false;
            }
            if (key.Kind != NodeKind.Leaf || value.Kind != NodeKind.Leaf)
            {
                node = default!;
                error = $"Map property '{propertyName}' currently requires scalar key and value types.";
                return false;
            }
            node = new Node(NodeKind.Map, parquetName, propertyName, GetTypeName(type),
                IsNullable(type, nullableAnnotation), scalar: null, "Dictionary", [key, value]);
            return true;
        }

        if (type is not INamedTypeSymbol groupType || groupType.TypeKind is not (TypeKind.Class or TypeKind.Struct))
        {
            node = default!;
            error = $"Unsupported nested CLR type '{type.ToDisplayString()}' on property '{propertyName}'.";
            return false;
        }

        var childProperties = GetProperties(groupType);
        if (childProperties.IsDefaultOrEmpty)
        {
            node = default!;
            error = $"Nested group type '{groupType.ToDisplayString()}' does not declare supported properties.";
            return false;
        }
        if (!HasUsableConstructor(groupType))
        {
            node = default!;
            error = $"Nested group type '{groupType.ToDisplayString()}' requires an accessible parameterless constructor.";
            return false;
        }

        var children = ImmutableArray.CreateBuilder<Node>(childProperties.Length);
        foreach (var property in childProperties)
        {
            if (property.SetMethod is null)
            {
                node = default!;
                error = $"Nested group property '{groupType.Name}.{property.Name}' must have a setter or init accessor.";
                return false;
            }
            if (!TryCreatePropertyNode(property, out var child, out error))
            {
                node = default!;
                return false;
            }
            children.Add(child);
        }

        node = new Node(NodeKind.Group, parquetName, propertyName, GetTypeName(type),
            IsNullable(type, nullableAnnotation), scalar: null, collectionKind: null, children.ToImmutable());
        error = string.Empty;
        return true;
    }

    static void CollectLeaves(Node root, Node node, List<Node> path, List<CollectionLevel> collections,
        int repetitionLevel, int definitionLevel, List<Leaf> leaves, HashSet<string> usedNames)
    {
        path.Add(node);
        if (node.Optional)
            definitionLevel++;

        if (node.Kind is NodeKind.List or NodeKind.Map)
        {
            var definedDefinitionLevel = definitionLevel;
            repetitionLevel++;
            definitionLevel++;
            collections.Add(new CollectionLevel(repetitionLevel, definedDefinitionLevel, definitionLevel));
        }

        if (node.Kind == NodeKind.Leaf)
        {
            var endpointThreshold = collections.Count == 0
                ? 0
                : collections[collections.Count - 1].ElementDefinitionLevel;
            var endpointOptional = definitionLevel > endpointThreshold;
            var storageElementType = collections.Count == 0 ? node.Scalar!.UserType : node.Scalar!.StorageType;
            if (endpointOptional && !storageElementType.EndsWith("?", StringComparison.Ordinal) &&
                IsNonNullableValueType(storageElementType))
                storageElementType += "?";
            var storageShapeType = storageElementType;
            for (var i = 0; i < collections.Count; i++)
                storageShapeType += "[]";

            var preferredName = string.Join("_", path.Select(static part => ToIdentifier(part.PropertyName)));
            var uniqueName = preferredName;
            for (var suffix = 2; !usedNames.Add(uniqueName); suffix++)
                uniqueName = preferredName + suffix;
            var leaf = new Leaf(root, node, path.ToImmutableArray(), collections.ToImmutableArray(),
                uniqueName, storageShapeType, node.Scalar.StorageType, repetitionLevel, definitionLevel,
                endpointOptional);
            leaves.Add(leaf);
            for (var i = 0; i < path.Count; i++)
                path[i].Leaves.Add(leaf);
        }
        else
        {
            for (var i = 0; i < node.Children.Length; i++)
                CollectLeaves(root, node.Children[i], path, collections, repetitionLevel, definitionLevel,
                    leaves, usedNames);
        }

        if (node.Kind is NodeKind.List or NodeKind.Map)
            collections.RemoveAt(collections.Count - 1);
        path.RemoveAt(path.Count - 1);
    }

    static bool ValidateShape(Node root, out string error)
    {
        if (root.Kind == NodeKind.Group)
        {
            if (ContainsCollection(root))
            {
                error = $"Nested group property '{root.PropertyName}' cannot contain collections yet; place the collection at the schema root.";
                return false;
            }
            if (root.Leaves.Any(static leaf => leaf.MaxDefinitionLevel > 1))
            {
                error = $"Nested group property '{root.PropertyName}' has more than one optional boundary on a leaf, which the generated writer cannot preserve.";
                return false;
            }
            if (root.Optional && FindPresenceLeaf(root) is null)
            {
                error = $"Optional nested group '{root.PropertyName}' requires at least one required scalar property to preserve group presence.";
                return false;
            }
        }

        if (root.Kind == NodeKind.List && !ValidateListShape(root, out error))
            return false;
        if (root.Kind == NodeKind.Map && root.Children.Any(static child => child.Kind != NodeKind.Leaf))
        {
            error = $"Map property '{root.PropertyName}' currently requires scalar keys and values.";
            return false;
        }

        foreach (var leaf in root.Leaves)
            if (!leaf.Node.Scalar!.SupportsNestedStorage && leaf.MaxRepetitionLevel > 0)
            {
                error = $"Nested collection leaf '{leaf.Node.PropertyName}' uses CLR type '{leaf.Node.Scalar.UserType}', which is not supported by generated repeated storage.";
                return false;
            }

        error = string.Empty;
        return true;
    }

    static bool ValidateListShape(Node list, out string error)
    {
        var element = list.Children[0];
        if (element.Kind == NodeKind.Group)
        {
            if (element.Optional || element.Children.Any(static child => child.Kind != NodeKind.Leaf || child.Optional))
            {
                error = $"List-of-record property '{list.PropertyName}' currently requires a required record with required scalar properties.";
                return false;
            }
        }
        else if (element.Kind == NodeKind.Map)
        {
            error = $"List property '{list.PropertyName}' cannot directly contain maps yet.";
            return false;
        }
        else if (element.Kind == NodeKind.List)
        {
            if (element.Optional)
            {
                error = $"Nested-list property '{list.PropertyName}' currently requires non-null inner lists.";
                return false;
            }
            if (!ValidateListShape(element, out error))
                return false;
        }

        error = string.Empty;
        return true;
    }

    static string BuildSource(INamedTypeSymbol schemaType, Model model)
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
            .Append(EscapeIdentifier(schemaType.Name)).AppendLine();
        builder.AppendLine("{");
        AppendSchema(builder, model);
        builder.AppendLine();
        AppendDescriptors(builder, model);
        builder.AppendLine();
        AppendFactories(builder, schemaType);
        builder.AppendLine();
        AppendProjection(builder, model);
        builder.AppendLine();
        AppendWriters(builder, model, schemaType);
        builder.AppendLine();
        AppendRowReader(builder, model);
        builder.AppendLine();
        AppendMaterializers(builder, model);
        builder.AppendLine("}");
        return builder.ToString();
    }

    static void AppendSchema(StringBuilder builder, Model model)
    {
        builder.AppendLine("    public static global::Plank.Schema.ParquetSchema Schema { get; } = new([");
        for (var i = 0; i < model.Roots.Length; i++)
        {
            builder.Append("        ");
            AppendDefinition(builder, model.Roots[i]);
            builder.AppendLine(",");
        }
        builder.AppendLine("    ]);");
        builder.AppendLine("    const int DefaultRowBatchSize = 1024;");
    }

    static void AppendDefinition(StringBuilder builder, Node node)
    {
        switch (node.Kind)
        {
            case NodeKind.Leaf:
                builder.Append("global::Plank.Schema.ColumnDefinition.Leaf(\"").Append(Escape(node.Name))
                    .Append("\", global::Plank.Schema.ParquetPhysicalType.").Append(node.Scalar!.PhysicalType)
                    .Append(", ").Append(GetColumnOptionsExpression(node));
                if (node.Scalar.LogicalExpression is { } logical)
                    builder.Append(", ").Append(logical);
                builder.Append(')');
                break;
            case NodeKind.List:
                builder.Append("global::Plank.Schema.ColumnDefinition.List(\"").Append(Escape(node.Name))
                    .Append("\", ");
                AppendDefinition(builder, node.Children[0]);
                builder.Append(", global::Plank.Schema.ParquetRepetition.")
                    .Append(node.Optional ? "Optional" : "Required").Append(')');
                break;
            case NodeKind.Map:
                builder.Append("global::Plank.Schema.ColumnDefinition.Map(\"").Append(Escape(node.Name))
                    .Append("\", ");
                AppendDefinition(builder, node.Children[0]);
                builder.Append(", ");
                AppendDefinition(builder, node.Children[1]);
                builder.Append(", global::Plank.Schema.ParquetRepetition.")
                    .Append(node.Optional ? "Optional" : "Required").Append(')');
                break;
            case NodeKind.Group:
                builder.Append("global::Plank.Schema.ColumnDefinition.")
                    .Append(node.Optional ? "OptionalGroup" : "RequiredGroup").Append("(\"")
                    .Append(Escape(node.Name)).Append('"');
                for (var i = 0; i < node.Children.Length; i++)
                {
                    builder.Append(", ");
                    AppendDefinition(builder, node.Children[i]);
                }
                builder.Append(')');
                break;
        }
    }

    static void AppendDescriptors(StringBuilder builder, Model model)
    {
        for (var i = 0; i < model.Leaves.Length; i++)
        {
            var leaf = model.Leaves[i];
            if (leaf.UsesNestedDescriptor)
            {
                builder.Append("    static readonly global::Plank.RowApi.RowApiNestedColumnDescriptor<")
                    .Append(leaf.StorageShapeType).Append(", ").Append(leaf.StorageElementType).Append("> ")
                    .Append(leaf.DescriptorName).Append(" = new(\"").Append(Escape(leaf.Root.PropertyName))
                    .Append("\", Schema.LeafColumns[").Append(i).Append(']');
                for (var levelIndex = 0; levelIndex < leaf.CollectionLevels.Length; levelIndex++)
                {
                    var level = leaf.CollectionLevels[levelIndex];
                    builder.Append(", new global::Plank.RowApi.RowApiCollectionLevel(")
                        .Append(level.RepetitionLevel).Append(", ").Append(level.DefinedDefinitionLevel)
                        .Append(", ").Append(level.ElementDefinitionLevel).Append(')');
                }
                builder.AppendLine(");");
            }
            else
            {
                builder.Append("    static readonly global::Plank.RowApi.RowApiColumnDescriptor<")
                    .Append(leaf.StorageShapeType).Append("> ").Append(leaf.DescriptorName)
                    .Append(" = new(\"").Append(Escape(leaf.Root.PropertyName)).Append("\", Schema.LeafColumns[")
                    .Append(i).AppendLine("]);");
            }
        }
        builder.AppendLine();
        builder.AppendLine("    static readonly global::Plank.RowApi.RowApiColumnDescriptor[] s_rowApiColumns = [");
        for (var i = 0; i < model.Leaves.Length; i++)
            builder.Append("        ").Append(model.Leaves[i].DescriptorName).AppendLine(",");
        builder.AppendLine("    ];");
    }

    static void AppendFactories(StringBuilder builder, INamedTypeSymbol schemaType)
    {
        var rowTypeName = EscapeIdentifier(schemaType.Name);
        builder.Append("    public delegate global::System.ReadOnlySpan<byte> Route(").Append(rowTypeName)
            .AppendLine(" row, global::Plank.IParquetBufferPool bufferPool, out global::Plank.ParquetBuffer? allocation);");
        builder.AppendLine();
        builder.AppendLine("    public static DatasetWriter CreateDatasetWriter<TFile>(Route route, TFile[] files, global::Plank.Dataset.DatasetWriterOptions? options = null)");
        builder.AppendLine("        where TFile : class, global::Plank.Reading.IParquetReadSource, global::Plank.Writing.IParquetWriteSource");
        builder.AppendLine("        => new(route ?? throw new global::System.ArgumentNullException(nameof(route)),");
        builder.AppendLine("            files ?? throw new global::System.ArgumentNullException(nameof(files)),");
        builder.AppendLine("            options ?? global::Plank.Dataset.DatasetWriterOptions.Default);");
        builder.AppendLine();
        builder.AppendLine("    public static DatasetWriter CreateDatasetWriter<TFile>(Route route, global::Plank.Dataset.DatasetFilePath filePath, TFile[] files, global::Plank.Dataset.DatasetWriterOptions? options = null)");
        builder.AppendLine("        where TFile : class, global::Plank.Writing.IParquetWriteSource");
        builder.AppendLine("        => new(route ?? throw new global::System.ArgumentNullException(nameof(route)),");
        builder.AppendLine("            filePath ?? throw new global::System.ArgumentNullException(nameof(filePath)),");
        builder.AppendLine("            files ?? throw new global::System.ArgumentNullException(nameof(files)),");
        builder.AppendLine("            options ?? global::Plank.Dataset.DatasetWriterOptions.Default);");
        builder.AppendLine();
        builder.AppendLine("    public static Writer CreateRowWriter(global::Plank.Writing.RowGroupWriter rowGroupWriter, global::Plank.Writing.ParquetWriterOptions? options = null)");
        builder.AppendLine("        => new(rowGroupWriter, options ?? global::Plank.Writing.ParquetWriterOptions.Default);");
        builder.AppendLine();
        builder.AppendLine("    public static PipelineWriter CreateRowWriter(global::System.IO.Stream stream, global::Plank.Writing.ParquetWriterOptions? options = null)");
        builder.AppendLine("        => new(stream, options ?? global::Plank.Writing.ParquetWriterOptions.Default);");
        builder.AppendLine();
        builder.AppendLine("    public static PipelineWriter CreateRowWriter<TFile>(global::Plank.Writing.ParquetFilePath filePath, TFile file, global::Plank.Writing.ParquetWriterOptions? options = null)");
        builder.AppendLine("        where TFile : class, global::Plank.Writing.IParquetWriteSource");
        builder.AppendLine("        => new(file, filePath ?? throw new global::System.ArgumentNullException(nameof(filePath)), options ?? global::Plank.Writing.ParquetWriterOptions.Default);");
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
    }

    static void AppendProjection(StringBuilder builder, Model model)
    {
        builder.AppendLine("    public readonly struct Projection");
        builder.AppendLine("    {");
        builder.AppendLine("        readonly global::Plank.RowApi.RowApiColumnDescriptor[]? _columns;");
        builder.AppendLine("        Projection(global::Plank.RowApi.RowApiColumnDescriptor[] columns) => _columns = columns;");
        builder.AppendLine("        internal global::Plank.RowApi.RowApiColumnDescriptor[]? Columns => _columns;");
        builder.AppendLine("        public static Projection All => default;");
        builder.AppendLine("        public static Projection None { get; } = new([]);");
        for (var i = 0; i < model.Roots.Length; i++)
        {
            var root = model.Roots[i];
            builder.Append("        public static Projection ").Append(EscapeIdentifier(root.PropertyName))
                .Append(" { get; } = new([");
            for (var leafIndex = 0; leafIndex < root.Leaves.Count; leafIndex++)
            {
                if (leafIndex > 0)
                    builder.Append(", ");
                builder.Append(root.Leaves[leafIndex].DescriptorName);
            }
            builder.AppendLine("]);");
        }
        builder.AppendLine("        public static Projection operator |(Projection left, Projection right)");
        builder.AppendLine("        {");
        builder.AppendLine("            if (left._columns is null || right._columns is null) return All;");
        builder.AppendLine("            if (left._columns.Length == 0) return right;");
        builder.AppendLine("            if (right._columns.Length == 0) return left;");
        builder.AppendLine("            var combined = new global::Plank.RowApi.RowApiColumnDescriptor[left._columns.Length + right._columns.Length];");
        builder.AppendLine("            left._columns.CopyTo(combined, 0);");
        builder.AppendLine("            right._columns.CopyTo(combined, left._columns.Length);");
        builder.AppendLine("            return new Projection(combined);");
        builder.AppendLine("        }");
        builder.AppendLine("    }");
    }

    static void AppendWriters(StringBuilder builder, Model model, INamedTypeSymbol schemaType)
    {
        var rowTypeName = EscapeIdentifier(schemaType.Name);
        builder.AppendLine("    public struct Writer");
        builder.AppendLine("    {");
        builder.AppendLine("        readonly global::Plank.RowApi.RowGroupWriterCore<BufferSlot> _core;");
        builder.AppendLine("        internal Writer(global::Plank.Writing.RowGroupWriter rowGroupWriter, global::Plank.Writing.ParquetWriterOptions options)");
        builder.AppendLine("        {");
        builder.AppendLine("            _ = options ?? throw new global::System.ArgumentNullException(nameof(options));");
        builder.AppendLine("            var slot = new BufferSlot(rowGroupWriter, DefaultRowBatchSize);");
        builder.AppendLine("            _core = new global::Plank.RowApi.RowGroupWriterCore<BufferSlot>(rowGroupWriter, slot);");
        builder.AppendLine("        }");
        builder.AppendLine("        public Row GetRow() => _core.GetSlotForRow().GetRow();");
        builder.AppendLine("        public void Next() => _core.Next();");
        builder.AppendLine("        public void Write() => _core.Write();");
        builder.AppendLine("    }");
        builder.AppendLine();
        builder.AppendLine("    public sealed class PipelineWriter : global::Plank.RowApi.PipelineRowWriterBase<BufferSlot>");
        builder.AppendLine("    {");
        builder.AppendLine("        internal PipelineWriter(global::System.IO.Stream stream, global::Plank.Writing.ParquetWriterOptions options)");
        builder.AppendLine("            : this(stream, options.RowApiMaxParallelism, null, options) { }");
        builder.AppendLine("        internal PipelineWriter(global::Plank.Writing.IParquetWriteSource file, global::Plank.Writing.ParquetFilePath filePath, global::Plank.Writing.ParquetWriterOptions options)");
        builder.AppendLine("            : base(file, filePath, Schema, options.RowApiMaxParallelism, null, options, DefaultRowBatchSize, \"PlankNestedRowApiWorker\") { }");
        builder.AppendLine("        internal PipelineWriter(global::System.IO.Stream stream, global::System.Action<int>? onFlush, global::Plank.Writing.ParquetWriterOptions options)");
        builder.AppendLine("            : this(stream, options.RowApiMaxParallelism, onFlush, options) { }");
        builder.AppendLine("        internal PipelineWriter(global::System.IO.Stream stream, uint maxParallelism, global::Plank.Writing.ParquetWriterOptions options)");
        builder.AppendLine("            : this(stream, maxParallelism, null, options) { }");
        builder.AppendLine("        internal PipelineWriter(global::System.IO.Stream stream, uint maxParallelism, global::System.Action<int>? onFlush, global::Plank.Writing.ParquetWriterOptions options)");
        builder.AppendLine("            : base(stream, Schema, maxParallelism, onFlush, options, DefaultRowBatchSize, \"PlankNestedRowApiWorker\") { }");
        builder.AppendLine("        protected override BufferSlot CreateSlot(global::Plank.Writing.ParquetWriter writer) => new(writer, RowBatchSize);");
        builder.AppendLine("        public Row GetRow() => GetSlotForRow().GetRow();");
        builder.AppendLine("        public void Next() => NextRow();");
        builder.AppendLine("        public void Complete() => CompleteWriter();");
        builder.AppendLine("    }");
        builder.AppendLine();
        builder.Append("    public sealed class DatasetWriter : global::Plank.Dataset.DatasetWriterBase<")
            .Append(rowTypeName).AppendLine(">, global::System.IDisposable");
        builder.AppendLine("    {");
        builder.AppendLine("        readonly Route _route;");
        builder.AppendLine("        internal DatasetWriter(Route route, global::Plank.Writing.IParquetWriteSource[] files, global::Plank.Dataset.DatasetWriterOptions options)");
        builder.AppendLine("            : base(Schema, s_rowApiColumns, DefaultRowBatchSize, files, options)");
        builder.AppendLine("        {");
        builder.AppendLine("            _route = route;");
        builder.AppendLine("            InitializeSlots();");
        builder.AppendLine("        }");
        builder.AppendLine("        internal DatasetWriter(Route route, global::Plank.Dataset.DatasetFilePath filePath, global::Plank.Writing.IParquetWriteSource[] files, global::Plank.Dataset.DatasetWriterOptions options)");
        builder.AppendLine("            : base(Schema, s_rowApiColumns, DefaultRowBatchSize, files, filePath, options)");
        builder.AppendLine("        {");
        builder.AppendLine("            _route = route;");
        builder.AppendLine("            InitializeSlots();");
        builder.AppendLine("        }");
        builder.Append("        protected override void CopyRow(").Append(rowTypeName)
            .AppendLine(" row, int slotIndex, int rowIndex)");
        builder.AppendLine("        {");
        for (var i = 0; i < model.Roots.Length; i++)
        {
            var root = model.Roots[i];
            if (root.Kind == NodeKind.Leaf)
            {
                var leaf = root.Leaves[0];
                builder.Append("            SetColumnValue<").Append(leaf.StorageShapeType)
                    .Append(">(slotIndex, ").Append(leaf.Ordinal).Append(", rowIndex, row.")
                    .Append(EscapeIdentifier(root.PropertyName)).AppendLine(");");
                continue;
            }

            builder.Append("            var value").Append(i).Append(" = row.")
                .Append(EscapeIdentifier(root.PropertyName)).AppendLine(";");
            for (var leafIndex = 0; leafIndex < root.Leaves.Count; leafIndex++)
            {
                var leaf = root.Leaves[leafIndex];
                builder.Append("            SetColumnValue<").Append(leaf.StorageShapeType)
                    .Append(">(slotIndex, ").Append(leaf.Ordinal).Append(", rowIndex, Project")
                    .Append(leaf.UniqueName).Append("(value").Append(i).AppendLine("));");
            }
        }
        builder.AppendLine("        }");
        builder.Append("        protected override global::System.ReadOnlySpan<byte> SelectPath(")
            .Append(rowTypeName)
            .AppendLine(" row, global::Plank.IParquetBufferPool bufferPool, out global::Plank.ParquetBuffer? allocation)");
        builder.AppendLine("            => _route(row, bufferPool, out allocation);");
        builder.Append("        public void Queue(").Append(rowTypeName).AppendLine(" row) => QueueRow(row);");
        builder.AppendLine("        public void Dispose() => DisposeDataset();");
        builder.AppendLine("    }");
        builder.AppendLine();
        builder.AppendLine("    public sealed class BufferSlot : global::Plank.RowApi.RowBufferSlot");
        builder.AppendLine("    {");
        builder.AppendLine("        internal BufferSlot(global::Plank.Writing.RowGroupWriter rowGroupWriter, int rowCount) : base(rowGroupWriter, s_rowApiColumns, rowCount) { }");
        builder.AppendLine("        internal BufferSlot(global::Plank.Writing.ParquetWriter writer, int rowCount) : base(writer, s_rowApiColumns, rowCount) { }");
        builder.AppendLine("        internal Row GetRow()");
        builder.AppendLine("        {");
        builder.AppendLine("            EnsureRowAvailable();");
        builder.Append("            return new Row(Index, this");
        for (var i = 0; i < model.Leaves.Length; i++)
            builder.Append(", GetValues<").Append(model.Leaves[i].StorageShapeType).Append(">(").Append(i).Append(')');
        builder.AppendLine(");");
        builder.AppendLine("        }");
        builder.AppendLine("    }");
        builder.AppendLine();
        builder.AppendLine("    public readonly ref struct Row");
        builder.AppendLine("    {");
        builder.AppendLine("        readonly int _index;");
        builder.AppendLine("        readonly BufferSlot _ownerSlot;");
        for (var i = 0; i < model.Leaves.Length; i++)
            builder.Append("        readonly global::System.Span<").Append(model.Leaves[i].StorageShapeType)
                .Append("> _").Append(model.Leaves[i].UniqueName).AppendLine(";");
        builder.Append("        internal Row(int index, BufferSlot ownerSlot");
        for (var i = 0; i < model.Leaves.Length; i++)
            builder.Append(", global::System.Span<").Append(model.Leaves[i].StorageShapeType).Append("> p")
                .Append(model.Leaves[i].UniqueName);
        builder.AppendLine(")");
        builder.AppendLine("        {");
        builder.AppendLine("            _index = index;");
        builder.AppendLine("            _ownerSlot = ownerSlot;");
        for (var i = 0; i < model.Leaves.Length; i++)
            builder.Append("            _").Append(model.Leaves[i].UniqueName).Append(" = p")
                .Append(model.Leaves[i].UniqueName).AppendLine(";");
        builder.AppendLine("        }");
        for (var i = 0; i < model.Roots.Length; i++)
        {
            builder.AppendLine();
            AppendWriteRowProperty(builder, model.Roots[i]);
        }
        builder.AppendLine("    }");
    }

    static void AppendWriteRowProperty(StringBuilder builder, Node root)
    {
        if (root.Kind == NodeKind.Leaf)
        {
            var leaf = root.Leaves[0];
            builder.Append("        public ref ").Append(root.UserType).Append(' ')
                .Append(EscapeIdentifier(root.PropertyName)).Append(" => ref _")
                .Append(leaf.UniqueName).AppendLine("[_index];");
            return;
        }

        builder.Append("        public ").Append(root.UserType).Append(' ')
            .Append(EscapeIdentifier(root.PropertyName)).AppendLine();
        builder.AppendLine("        {");
        builder.Append("            get => Read").Append(ToIdentifier(root.PropertyName)).Append('(');
        AppendLeafArguments(builder, root, static leaf => $"_{leaf.UniqueName}[_index]");
        builder.AppendLine(");");
        builder.AppendLine("            set");
        builder.AppendLine("            {");
        for (var i = 0; i < root.Leaves.Count; i++)
        {
            var leaf = root.Leaves[i];
            builder.Append("                _").Append(leaf.UniqueName).Append("[_index] = Project")
                .Append(leaf.UniqueName).AppendLine("(value);");
        }
        builder.AppendLine("            }");
        builder.AppendLine("        }");
    }

    static void AppendRowReader(StringBuilder builder, Model model)
    {
        builder.AppendLine("    public sealed class RowReader : global::System.IDisposable");
        builder.AppendLine("    {");
        builder.AppendLine("        readonly global::Plank.RowApi.RowReaderCore _core;");
        builder.AppendLine("        internal RowReader(global::System.IO.Stream stream, Projection projection, global::Plank.RowApi.RowReaderOptions options, global::Plank.Reading.ParquetSchemaEvolutionOptions? schemaEvolution)");
        builder.AppendLine("            => _core = new global::Plank.RowApi.RowReaderCore(stream, Schema, s_rowApiColumns, projection.Columns, options, schemaEvolution);");
        builder.AppendLine("        internal RowReader(global::Plank.Reading.IParquetReadSource source, Projection projection, global::Plank.RowApi.RowReaderOptions options, global::Plank.Reading.ParquetSchemaEvolutionOptions? schemaEvolution)");
        builder.AppendLine("            => _core = new global::Plank.RowApi.RowReaderCore(source, Schema, s_rowApiColumns, projection.Columns, options, schemaEvolution);");
        builder.AppendLine("        public Enumerator GetEnumerator() => new(this);");
        builder.AppendLine("        public void Reset(global::System.IO.Stream stream, Projection projection = default, global::Plank.Reading.ParquetSchemaEvolutionOptions? schemaEvolution = null) => _core.Reset(stream, projection.Columns, schemaEvolution);");
        builder.AppendLine("        public void Reset(global::Plank.Reading.IParquetReadSource source, Projection projection = default, global::Plank.Reading.ParquetSchemaEvolutionOptions? schemaEvolution = null) => _core.Reset(source, projection.Columns, schemaEvolution);");
        builder.AppendLine("        public ReadRow Current { get { _core.ThrowIfNotPositioned(); return new ReadRow(_core); } }");
        builder.AppendLine("        public bool MoveNext() => _core.MoveNext();");
        builder.AppendLine("        public void Dispose() => _core.Dispose();");
        builder.AppendLine("        public readonly struct Enumerator : global::System.IDisposable");
        builder.AppendLine("        {");
        builder.AppendLine("            readonly RowReader _reader;");
        builder.AppendLine("            internal Enumerator(RowReader reader) => _reader = reader;");
        builder.AppendLine("            public ReadRow Current => _reader.Current;");
        builder.AppendLine("            public bool MoveNext() => _reader.MoveNext();");
        builder.AppendLine("            public void Dispose() { }");
        builder.AppendLine("        }");
        builder.AppendLine("    }");
        builder.AppendLine();
        builder.AppendLine("    public readonly ref struct ReadRow");
        builder.AppendLine("    {");
        builder.AppendLine("        readonly global::Plank.RowApi.RowReaderCore _core;");
        builder.AppendLine("        internal ReadRow(global::Plank.RowApi.RowReaderCore core) => _core = core;");
        for (var i = 0; i < model.Roots.Length; i++)
        {
            builder.AppendLine();
            AppendReadRowProperty(builder, model.Roots[i]);
        }
        builder.AppendLine("    }");
    }

    static void AppendReadRowProperty(StringBuilder builder, Node root)
    {
        if (root.Kind == NodeKind.Leaf)
        {
            var leaf = root.Leaves[0];
            if (leaf.Node.Scalar!.IsBinary)
            {
                builder.Append("        public ").Append(root.UserType).Append(' ')
                    .Append(EscapeIdentifier(root.PropertyName)).AppendLine();
                builder.AppendLine("        {");
                builder.AppendLine("            get");
                builder.AppendLine("            {");
                builder.Append("                var value = _core.GetCurrentBinary(").Append(leaf.DescriptorName)
                    .AppendLine(");");
                builder.AppendLine("                if (value.IsNull) return default!;");
                builder.Append("                return ").Append(ConvertBinaryFromSpan(leaf.Node.Scalar, "value.Value"))
                    .AppendLine(";");
                builder.AppendLine("            }");
                builder.AppendLine("        }");
            }
            else
            {
                builder.Append("        public ref ").Append(root.UserType).Append(' ')
                    .Append(EscapeIdentifier(root.PropertyName)).Append(" => ref _core.GetCurrent(")
                    .Append(leaf.DescriptorName).AppendLine(");");
            }
            return;
        }

        builder.Append("        public ").Append(root.UserType).Append(' ')
            .Append(EscapeIdentifier(root.PropertyName)).Append(" => Read")
            .Append(ToIdentifier(root.PropertyName)).Append('(');
        AppendLeafArguments(builder, root, static leaf => leaf.UsesNestedDescriptor
            ? $"_core.GetCurrentNested({leaf.DescriptorName})"
            : leaf.Node.Scalar!.IsBinary
                ? $"Read{leaf.UniqueName}Binary(_core.GetCurrentBinary({leaf.DescriptorName}))"
            : $"_core.GetCurrent({leaf.DescriptorName})");
        builder.AppendLine(");");
    }

    static void AppendMaterializers(StringBuilder builder, Model model)
    {
        if (model.Leaves.Any(static leaf => leaf.MaxRepetitionLevel > 0 &&
                leaf.Node.Scalar!.NonNullableUserType is "global::System.DateTime" or
                    "global::System.DateTimeOffset"))
        {
            builder.AppendLine();
            builder.AppendLine("    static long GeneratedNestedToUnixMicroseconds(global::System.DateTime value)");
            builder.AppendLine("    {");
            builder.AppendLine("        if (value.Kind != global::System.DateTimeKind.Utc)");
            builder.AppendLine("            throw new global::System.InvalidOperationException($\"DateTime values must have kind 'Utc', got '{value.Kind}'.\");");
            builder.AppendLine("        return GeneratedNestedToUnixMicroseconds(value.Ticks);");
            builder.AppendLine("    }");
            builder.AppendLine();
            builder.AppendLine("    static long GeneratedNestedToUnixMicroseconds(global::System.DateTimeOffset value)");
            builder.AppendLine("        => GeneratedNestedToUnixMicroseconds(value.UtcDateTime.Ticks);");
            builder.AppendLine();
            builder.AppendLine("    static long GeneratedNestedToUnixMicroseconds(long ticks)");
            builder.AppendLine("    {");
            builder.AppendLine("        var delta = checked(ticks - global::System.DateTime.UnixEpoch.Ticks);");
            builder.AppendLine("        var result = delta / 10;");
            builder.AppendLine("        return delta >= 0 || delta % 10 == 0 ? result : result - 1;");
            builder.AppendLine("    }");
        }

        for (var i = 0; i < model.Leaves.Length; i++)
        {
            var leaf = model.Leaves[i];
            if (leaf.UsesNestedDescriptor || !leaf.Node.Scalar!.IsBinary)
                continue;

            builder.AppendLine();
            builder.Append("    static ").Append(leaf.StorageShapeType).Append(" Read")
                .Append(leaf.UniqueName).AppendLine("Binary(global::Plank.RowApi.RowReaderBinaryValue value)");
            builder.AppendLine("    {");
            builder.AppendLine("        if (value.IsNull) return default!;");
            builder.Append("        return ").Append(ConvertBinaryFromSpan(leaf.Node.Scalar, "value.Value"))
                .AppendLine(";");
            builder.AppendLine("    }");
        }

        for (var i = 0; i < model.Roots.Length; i++)
        {
            var root = model.Roots[i];
            if (root.Kind == NodeKind.Leaf)
                continue;

            builder.AppendLine();
            AppendReadMaterializer(builder, root);
            for (var leafIndex = 0; leafIndex < root.Leaves.Count; leafIndex++)
            {
                builder.AppendLine();
                AppendWriteProjector(builder, root, root.Leaves[leafIndex]);
            }
        }
    }

    static void AppendReadMaterializer(StringBuilder builder, Node root)
    {
        builder.Append("    static ").Append(root.UserType).Append(" Read")
            .Append(ToIdentifier(root.PropertyName)).Append('(');
        for (var i = 0; i < root.Leaves.Count; i++)
        {
            if (i > 0)
                builder.Append(", ");
            builder.Append(root.Leaves[i].StorageShapeType).Append(" p").Append(root.Leaves[i].UniqueName);
        }
        builder.AppendLine(")");
        builder.AppendLine("    {");
        builder.Append("        ").Append(root.UserType).AppendLine(" result = default!;");
        var variableIndex = 0;
        AppendReadAssignment(builder, root, "result", [], 2, ref variableIndex);
        builder.AppendLine("        return result;");
        builder.AppendLine("    }");
    }

    static void AppendReadAssignment(StringBuilder builder, Node node, string target,
        ImmutableArray<string> indexes, int indent, ref int variableIndex)
    {
        var padding = new string(' ', indent * 4);
        if (node.Kind == NodeKind.Leaf)
        {
            var leaf = node.Leaves.Single(static candidate => candidate.Node.Kind == NodeKind.Leaf);
            var access = GetLeafAccess(leaf, indexes);
            builder.Append(padding).Append(target).Append(" = ")
                .Append(ConvertFromStorage(leaf, access)).AppendLine(";");
            return;
        }

        if (node.Kind == NodeKind.Group)
        {
            if (node.Optional)
            {
                var presenceLeaf = FindPresenceLeaf(node)!;
                builder.Append(padding).Append("if (").Append(GetLeafAccess(presenceLeaf, indexes))
                    .AppendLine(" is null)");
                builder.Append(padding).Append("    ").Append(target).AppendLine(" = null!;");
                builder.Append(padding).AppendLine("else");
                builder.Append(padding).AppendLine("{");
                AppendReadGroupBody(builder, node, target, indexes, indent + 1, ref variableIndex);
                builder.Append(padding).AppendLine("}");
            }
            else
                AppendReadGroupBody(builder, node, target, indexes, indent, ref variableIndex);
            return;
        }

        var firstLeaf = node.Leaves[0];
        var source = GetLeafAccess(firstLeaf, indexes);
        if (node.Optional)
        {
            builder.Append(padding).Append("if (").Append(source).AppendLine(" is null)");
            builder.Append(padding).Append("    ").Append(target).AppendLine(" = null!;");
            builder.Append(padding).AppendLine("else");
            builder.Append(padding).AppendLine("{");
            AppendReadCollectionBody(builder, node, target, source, indexes, indent + 1, ref variableIndex);
            builder.Append(padding).AppendLine("}");
        }
        else
            AppendReadCollectionBody(builder, node, target, source, indexes, indent, ref variableIndex);
    }

    static void AppendReadGroupBody(StringBuilder builder, Node node, string target,
        ImmutableArray<string> indexes, int indent, ref int variableIndex)
    {
        var padding = new string(' ', indent * 4);
        var childVariables = new string[node.Children.Length];
        for (var i = 0; i < node.Children.Length; i++)
        {
            var child = node.Children[i];
            var variable = "v" + variableIndex++;
            childVariables[i] = variable;
            builder.Append(padding).Append(child.UserType).Append(' ').Append(variable).AppendLine(" = default!;");
            AppendReadAssignment(builder, child, variable, indexes, indent, ref variableIndex);
        }
        builder.Append(padding).Append(target).Append(" = new ").Append(TrimNullable(node.UserType)).AppendLine();
        builder.Append(padding).AppendLine("{");
        for (var i = 0; i < node.Children.Length; i++)
            builder.Append(padding).Append("    ").Append(EscapeIdentifier(node.Children[i].PropertyName))
                .Append(" = ").Append(childVariables[i]).AppendLine(",");
        builder.Append(padding).AppendLine("};");
    }

    static void AppendReadCollectionBody(StringBuilder builder, Node node, string target, string source,
        ImmutableArray<string> indexes, int indent, ref int variableIndex)
    {
        var padding = new string(' ', indent * 4);
        var index = "i" + indexes.Length;
        var nonNullSource = source + "!";
        if (node.Kind == NodeKind.List)
        {
            var element = node.Children[0];
            if (node.CollectionKind == "Array")
                builder.Append(padding).Append(target).Append(" = (").Append(node.UserType)
                    .Append(")global::System.Array.CreateInstance(typeof(").Append(element.UserType)
                    .Append("), ").Append(nonNullSource).AppendLine(".Length);");
            else
                builder.Append(padding).Append(target).Append(" = new ").Append(TrimNullable(node.UserType))
                    .Append('(').Append(nonNullSource).AppendLine(".Length);");
            builder.Append(padding).Append("for (var ").Append(index).Append(" = 0; ").Append(index)
                .Append(" < ").Append(nonNullSource).Append(".Length; ").Append(index).AppendLine("++)");
            builder.Append(padding).AppendLine("{");
            var elementVariable = "v" + variableIndex++;
            builder.Append(padding).Append("    ").Append(element.UserType).Append(' ')
                .Append(elementVariable).AppendLine(" = default!;");
            AppendReadAssignment(builder, element, elementVariable, indexes.Add(index), indent + 1,
                ref variableIndex);
            if (node.CollectionKind == "Array")
                builder.Append(padding).Append("    ").Append(target).Append("![").Append(index).Append("] = ")
                    .Append(elementVariable).AppendLine(";");
            else
                builder.Append(padding).Append("    ").Append(target).Append("!.Add(").Append(elementVariable)
                    .AppendLine(");");
            builder.Append(padding).AppendLine("}");
            return;
        }

        var key = node.Children[0];
        var value = node.Children[1];
        builder.Append(padding).Append(target).Append(" = new ").Append(TrimNullable(node.UserType))
            .Append('(').Append(nonNullSource).AppendLine(".Length);");
        builder.Append(padding).Append("for (var ").Append(index).Append(" = 0; ").Append(index)
            .Append(" < ").Append(nonNullSource).Append(".Length; ").Append(index).AppendLine("++)");
        builder.Append(padding).AppendLine("{");
        var keyVariable = "v" + variableIndex++;
        var valueVariable = "v" + variableIndex++;
        builder.Append(padding).Append("    ").Append(key.UserType).Append(' ').Append(keyVariable)
            .AppendLine(" = default!;");
        AppendReadAssignment(builder, key, keyVariable, indexes.Add(index), indent + 1, ref variableIndex);
        builder.Append(padding).Append("    ").Append(value.UserType).Append(' ').Append(valueVariable)
            .AppendLine(" = default!;");
        AppendReadAssignment(builder, value, valueVariable, indexes.Add(index), indent + 1, ref variableIndex);
        builder.Append(padding).Append("    ").Append(target).Append("!.Add(").Append(keyVariable).Append(", ")
            .Append(valueVariable).AppendLine(");");
        builder.Append(padding).AppendLine("}");
    }

    static void AppendWriteProjector(StringBuilder builder, Node root, Leaf leaf)
    {
        builder.Append("    static ").Append(leaf.StorageShapeType).Append(" Project")
            .Append(leaf.UniqueName).Append('(').Append(root.UserType).AppendLine(" value)");
        builder.AppendLine("    {");
        builder.Append("        ").Append(leaf.StorageShapeType).AppendLine(" result = default!;");
        var indexes = new List<string>();
        AppendProjectAssignment(builder, root, leaf, "result", "value", depth: 0, indent: 2, indexes);
        builder.AppendLine("        return result;");
        builder.AppendLine("    }");
    }

    static void AppendProjectAssignment(StringBuilder builder, Node node, Leaf leaf, string target,
        string source, int depth, int indent, List<string> indexes)
    {
        var padding = new string(' ', indent * 4);
        if (node.Kind == NodeKind.Leaf)
        {
            builder.Append(padding).Append(target).Append(" = ").Append(ConvertToStorage(leaf, source))
                .AppendLine(";");
            return;
        }

        if (node.Kind == NodeKind.Group)
        {
            var child = node.Children.First(candidate => candidate.Leaves.Contains(leaf));
            if (node.Optional)
            {
                builder.Append(padding).Append("if (").Append(source).AppendLine(" is null)");
                builder.Append(padding).Append("    ").Append(target).AppendLine(" = default!;");
                builder.Append(padding).AppendLine("else");
                AppendProjectAssignment(builder, child, leaf, target,
                    source + "!." + EscapeIdentifier(child.PropertyName), depth, indent + 1, indexes);
            }
            else
                AppendProjectAssignment(builder, child, leaf, target,
                    source + "." + EscapeIdentifier(child.PropertyName), depth, indent, indexes);
            return;
        }

        var collectionChild = node.Kind == NodeKind.Map
            ? node.Children.First(candidate => candidate.Leaves.Contains(leaf))
            : node.Children[0];
        builder.Append(padding).Append("if (").Append(source).AppendLine(" is null)");
        if (node.Optional)
            builder.Append(padding).Append("    ").Append(target).AppendLine(" = null!;");
        else
            builder.Append(padding).AppendLine("    throw new global::System.InvalidOperationException(\"Required generated collection cannot be null.\");");
        builder.Append(padding).AppendLine("else");
        builder.Append(padding).AppendLine("{");
        var elementStorageType = GetStorageTypeAtDepth(leaf, depth + 1);
        var nonNullSource = source + "!";
        var count = node.Kind == NodeKind.List && node.CollectionKind == "Array"
            ? nonNullSource + ".Length"
            : nonNullSource + ".Count";
        builder.Append(padding).Append("    ").Append(target).Append(" = (")
            .Append(GetStorageTypeAtDepth(leaf, depth)).Append(")global::System.Array.CreateInstance(typeof(")
            .Append(elementStorageType).Append("), ").Append(count).AppendLine(");");
        if (node.Kind == NodeKind.Map)
        {
            var pair = "pair" + depth;
            var index = "i" + depth;
            builder.Append(padding).Append("    var ").Append(index).AppendLine(" = 0;");
            builder.Append(padding).Append("    foreach (var ").Append(pair).Append(" in ").Append(nonNullSource)
                .AppendLine(")");
            builder.Append(padding).AppendLine("    {");
            var childSource = collectionChild == node.Children[0] ? pair + ".Key" : pair + ".Value";
            AppendProjectAssignment(builder, collectionChild, leaf, target + "[" + index + "]",
                childSource, depth + 1, indent + 2, indexes);
            builder.Append(padding).Append("        ").Append(index).AppendLine("++;");
            builder.Append(padding).AppendLine("    }");
        }
        else
        {
            var index = "i" + depth;
            builder.Append(padding).Append("    for (var ").Append(index).Append(" = 0; ").Append(index)
                .Append(" < ").Append(count).Append("; ").Append(index).AppendLine("++)");
            builder.Append(padding).AppendLine("    {");
            AppendProjectAssignment(builder, collectionChild, leaf, target + "[" + index + "]",
                nonNullSource + "[" + index + "]", depth + 1, indent + 2, indexes);
            builder.Append(padding).AppendLine("    }");
        }
        builder.Append(padding).AppendLine("}");
    }

    static string GetLeafAccess(Leaf leaf, ImmutableArray<string> indexes)
    {
        var builder = new StringBuilder("p" + leaf.UniqueName);
        for (var i = 0; i < indexes.Length; i++)
            builder.Append('[').Append(indexes[i]).Append(']');
        return builder.ToString();
    }

    static string GetStorageTypeAtDepth(Leaf leaf, int depth)
    {
        var type = leaf.StorageShapeType;
        for (var i = 0; i < depth; i++)
            type = type.Substring(0, type.Length - 2);
        return type;
    }

    static void AppendLeafArguments(StringBuilder builder, Node root, Func<Leaf, string> expression)
    {
        for (var i = 0; i < root.Leaves.Count; i++)
        {
            if (i > 0)
                builder.Append(", ");
            builder.Append(expression(root.Leaves[i]));
        }
    }

    static bool TryCreateScalar(ITypeSymbol type, NullableAnnotation nullableAnnotation,
        IPropertySymbol? property, bool allowOverrides, out Scalar scalar, out string error)
    {
        var userType = GetTypeName(type);
        var nonNullableType = GetNonNullableType(type);
        var normalized = GetTypeName(nonNullableType.WithNullableAnnotation(NullableAnnotation.NotAnnotated));
        var physicalType = normalized switch
        {
            "bool" => "Boolean",
            "byte" or "ushort" or "int" or "uint" or "global::System.DateOnly" => "Int32",
            "long" or "ulong" or "global::System.DateTime" or "global::System.DateTimeOffset" or
                "global::System.TimeOnly" => "Int64",
            "float" => "Float",
            "double" => "Double",
            "string" or "byte[]" or "global::System.ReadOnlyMemory<byte>" => "ByteArray",
            "global::System.Guid" => "FixedLenByteArray",
            _ => string.Empty
        };
        if (physicalType.Length == 0)
        {
            scalar = default!;
            error = $"Unsupported nested scalar CLR type '{type.ToDisplayString()}'.";
            return false;
        }

        var logicalExpression = normalized switch
        {
            "byte" => "new global::Plank.Schema.LogicalType.Int(8, false)",
            "ushort" => "new global::Plank.Schema.LogicalType.Int(16, false)",
            "uint" => "new global::Plank.Schema.LogicalType.Int(32, false)",
            "ulong" => "new global::Plank.Schema.LogicalType.Int(64, false)",
            "global::System.DateOnly" => "new global::Plank.Schema.LogicalType.Date()",
            "global::System.TimeOnly" => "new global::Plank.Schema.LogicalType.Time(global::Plank.Schema.TimeUnit.Micros, false)",
            "global::System.DateTime" or "global::System.DateTimeOffset" =>
                "new global::Plank.Schema.LogicalType.Timestamp(global::Plank.Schema.TimeUnit.Micros, true)",
            "string" => "new global::Plank.Schema.LogicalType.String()",
            "global::System.Guid" => "new global::Plank.Schema.LogicalType.Uuid()",
            _ => null
        };
        ImmutableArray<string> encodings = [];
        string? compression = null;
        int? compressionLevel = null;
        var inferredPhysicalType = physicalType;
        if (allowOverrides && property is not null &&
            !TryReadLeafOverrides(property, normalized, ref physicalType, ref logicalExpression, ref encodings,
                ref compression, ref compressionLevel, out error))
        {
            scalar = default!;
            return false;
        }
        if (physicalType != inferredPhysicalType)
        {
            scalar = default!;
            error = $"Property '{property?.Name}' cannot override physical type '{inferredPhysicalType}' with '{physicalType}' in a generated nested schema.";
            return false;
        }
        for (var i = 0; i < encodings.Length; i++)
            if (!IsEncodingCompatible(encodings[i], physicalType))
            {
                scalar = default!;
                error = $"Property '{property?.Name}' selects encoding '{encodings[i]}', which is incompatible with physical type '{physicalType}'.";
                return false;
            }

        var storageType = normalized switch
        {
            "bool" => "bool",
            "byte" or "ushort" or "int" or "uint" => "int",
            "long" or "ulong" => "long",
            "float" => "float",
            "double" => "double",
            "string" or "byte[]" or "global::System.ReadOnlyMemory<byte>" or "global::System.Guid" => "byte[]",
            "global::System.DateOnly" => "int",
            "global::System.DateTime" or "global::System.DateTimeOffset" or "global::System.TimeOnly" => "long",
            _ => normalized
        };
        var supportsNestedStorage = normalized is
            "bool" or "byte" or "ushort" or "int" or "uint" or "long" or "ulong" or "float" or "double" or
            "string" or "byte[]" or "global::System.ReadOnlyMemory<byte>" or "global::System.Guid" or
            "global::System.DateOnly" or "global::System.DateTime" or "global::System.DateTimeOffset" or
            "global::System.TimeOnly";
        scalar = new Scalar(userType, normalized, physicalType, logicalExpression, encodings,
            normalized == "global::System.Guid" ? 16u : 0u, compression, compressionLevel, storageType,
            supportsNestedStorage,
            physicalType is "ByteArray" or "FixedLenByteArray" or "Int96");
        error = string.Empty;
        return true;
    }

    static bool TryReadNameOverride(IPropertySymbol property, ref string name, out string error)
    {
        error = string.Empty;
        var attribute = GetColumnAttribute(property);
        if (attribute is null)
            return true;
        for (var i = 0; i < attribute.ConstructorArguments.Length; i++)
            if (attribute.AttributeConstructor?.Parameters[i].Type.SpecialType == SpecialType.System_String &&
                attribute.ConstructorArguments[i].Value is string value)
                name = value;
        return true;
    }

    static bool TryReadLeafOverrides(IPropertySymbol property, string normalizedType, ref string physicalType,
        ref string? logicalExpression, ref ImmutableArray<string> encodings, ref string? compression,
        ref int? compressionLevel, out string error)
    {
        error = string.Empty;
        var attribute = GetColumnAttribute(property);
        if (attribute is null)
            return true;
        for (var i = 0; i < attribute.ConstructorArguments.Length; i++)
        {
            var parameter = attribute.AttributeConstructor?.Parameters[i];
            if (parameter?.Type.ToDisplayString() != "Plank.Schema.ParquetPhysicalType")
                continue;
            if (!TryGetEnumValue(attribute.ConstructorArguments[i], out var value) ||
                !TryGetPhysicalType(value, out physicalType))
            {
                error = $"Property '{property.Name}' declares an invalid ParquetPhysicalType override.";
                return false;
            }
        }
        foreach (var argument in attribute.NamedArguments)
        {
            if (argument.Key == "Encodings")
            {
                var encodingBuilder = ImmutableArray.CreateBuilder<string>();
                foreach (var item in argument.Value.Values)
                {
                    if (!TryGetEnumValue(item, out var value) || !TryGetEncoding(value, out var encoding))
                    {
                        error = $"Property '{property.Name}' declares an invalid EncodingKind override.";
                        return false;
                    }
                    encodingBuilder.Add(encoding);
                }
                encodings = encodingBuilder.ToImmutable();
            }
            else if (argument.Key == "Compression")
            {
                if (!TryGetEnumValue(argument.Value, out var value) ||
                    !TryGetCompression(value, out compression))
                {
                    error = $"Property '{property.Name}' declares an invalid CompressionKind override.";
                    return false;
                }
            }
            else if (argument.Key == "CompressionLevel")
            {
                if (argument.Value.Value is not int value)
                {
                    error = $"Property '{property.Name}' declares an invalid compression level.";
                    return false;
                }
                compressionLevel = value;
            }
            else if (argument.Key == "LogicalType")
            {
                if (!TryGetEnumValue(argument.Value, out var logical) ||
                    !TryApplyLogicalOverride(property, normalizedType, logical, ref logicalExpression, out error))
                {
                    if (error.Length == 0)
                        error = $"Property '{property.Name}' declares an invalid LogicalTypeKind override.";
                    return false;
                }
            }
        }
        return true;
    }

    static bool TryApplyLogicalOverride(IPropertySymbol property, string normalizedType, int logical,
        ref string? logicalExpression, out string error)
    {
        error = string.Empty;
        var compatible = logical switch
        {
            0 => normalizedType is not ("byte" or "ushort" or "uint" or "ulong" or
                "global::System.DateOnly" or "global::System.TimeOnly" or "global::System.DateTime" or
                "global::System.DateTimeOffset"),
            1 or 2 => normalizedType is "string" or "byte[]" or "global::System.ReadOnlyMemory<byte>",
            3 => normalizedType == "global::System.Guid",
            4 => normalizedType == "global::System.DateOnly",
            5 => normalizedType == "global::System.TimeOnly",
            6 => normalizedType is "global::System.DateTime" or "global::System.DateTimeOffset",
            7 => normalizedType is "byte" or "ushort" or "uint" or "ulong",
            _ => false
        };
        if (!compatible)
        {
            error = $"Property '{property.Name}' logical type override is incompatible with CLR type '{normalizedType}'.";
            return false;
        }

        logicalExpression = logical switch
        {
            0 => null,
            1 => "new global::Plank.Schema.LogicalType.String()",
            2 => "new global::Plank.Schema.LogicalType.Json()",
            3 => "new global::Plank.Schema.LogicalType.Uuid()",
            _ => logicalExpression
        };
        return true;
    }

    static bool IsEncodingCompatible(string encoding, string physicalType)
        => encoding switch
        {
            "Plain" or "PlainDictionary" or "RleDictionary" => true,
            "Rle" => physicalType == "Boolean",
            "BitPacked" => false,
            "DeltaBinaryPacked" => physicalType is "Int32" or "Int64",
            "DeltaLengthByteArray" or "DeltaByteArray" => physicalType == "ByteArray",
            "ByteStreamSplit" => physicalType is "Int32" or "Int64" or "Float" or "Double" or
                "FixedLenByteArray",
            _ => false
        };

    static string GetColumnOptionsExpression(Node node)
    {
        var scalar = node.Scalar!;
        var builder = new StringBuilder("new global::Plank.Schema.ColumnOptions(global::Plank.Schema.ParquetRepetition.");
        builder.Append(node.Optional ? "Optional" : "Required");
        if (!scalar.Encodings.IsDefaultOrEmpty)
        {
            builder.Append(", global::System.Collections.Immutable.ImmutableArray.Create(");
            for (var i = 0; i < scalar.Encodings.Length; i++)
            {
                if (i > 0)
                    builder.Append(", ");
                builder.Append("global::Plank.Schema.EncodingKind.").Append(scalar.Encodings[i]);
            }
            builder.Append(')');
        }
        if (scalar.TypeLength > 0)
        {
            if (scalar.Encodings.IsDefaultOrEmpty)
                builder.Append(", default");
            builder.Append(", ").Append(scalar.TypeLength);
        }
        if (scalar.Compression is { } compression)
            builder.Append(", compression: global::Plank.Schema.CompressionKind.").Append(compression);
        if (scalar.CompressionLevel is { } compressionLevel)
            builder.Append(", compressionLevel: ").Append(compressionLevel);
        return builder.Append(')').ToString();
    }

    static string ConvertFromStorage(Leaf leaf, string expression)
    {
        var scalar = leaf.Node.Scalar!;
        if (leaf.EndpointOptional && !leaf.Node.Optional && IsNonNullableValueType(scalar.NonNullableUserType))
            expression += "!.Value";
        if (leaf.MaxRepetitionLevel == 0)
            return expression;
        return scalar.NonNullableUserType switch
        {
            "byte" => $"unchecked((byte){expression})",
            "ushort" => $"unchecked((ushort){expression})",
            "uint" => $"unchecked((uint){expression})",
            "ulong" => $"unchecked((ulong){expression})",
            "string" => $"{expression} is null ? null! : global::System.Text.Encoding.UTF8.GetString({expression})",
            "global::System.ReadOnlyMemory<byte>" => scalar.UserType.EndsWith("?", StringComparison.Ordinal)
                ? $"{expression} is null ? (global::System.ReadOnlyMemory<byte>?)null : new global::System.ReadOnlyMemory<byte>({expression})"
                : $"{expression} is null ? default : new global::System.ReadOnlyMemory<byte>({expression})",
            "global::System.Guid" => scalar.UserType.EndsWith("?", StringComparison.Ordinal)
                ? $"{expression} is null ? (global::System.Guid?)null : new global::System.Guid({expression}, bigEndian: true)"
                : $"{expression} is null ? default : new global::System.Guid({expression}, bigEndian: true)",
            "global::System.DateOnly" => scalar.UserType.EndsWith("?", StringComparison.Ordinal)
                ? $"{expression}.HasValue ? global::System.DateOnly.FromDayNumber(checked(new global::System.DateOnly(1970, 1, 1).DayNumber + {expression}!.Value)) : (global::System.DateOnly?)null"
                : $"global::System.DateOnly.FromDayNumber(checked(new global::System.DateOnly(1970, 1, 1).DayNumber + {expression}))",
            "global::System.TimeOnly" => scalar.UserType.EndsWith("?", StringComparison.Ordinal)
                ? $"{expression}.HasValue ? new global::System.TimeOnly(checked({expression}!.Value * 10)) : (global::System.TimeOnly?)null"
                : $"new global::System.TimeOnly(checked({expression} * 10))",
            "global::System.DateTime" => scalar.UserType.EndsWith("?", StringComparison.Ordinal)
                ? $"{expression}.HasValue ? global::System.DateTime.UnixEpoch.AddTicks(checked({expression}!.Value * 10)) : (global::System.DateTime?)null"
                : $"global::System.DateTime.UnixEpoch.AddTicks(checked({expression} * 10))",
            "global::System.DateTimeOffset" => scalar.UserType.EndsWith("?", StringComparison.Ordinal)
                ? $"{expression}.HasValue ? global::System.DateTimeOffset.UnixEpoch.AddTicks(checked({expression}!.Value * 10)) : (global::System.DateTimeOffset?)null"
                : $"global::System.DateTimeOffset.UnixEpoch.AddTicks(checked({expression} * 10))",
            _ => expression
        };
    }

    static string ConvertToStorage(Leaf leaf, string expression)
    {
        var scalar = leaf.Node.Scalar!;
        if (leaf.MaxRepetitionLevel == 0)
            return expression;
        return scalar.NonNullableUserType switch
        {
            "byte" or "ushort" or "uint" => $"unchecked((int){expression})",
            "ulong" => $"unchecked((long){expression})",
            "string" => $"{expression} is null ? null! : global::System.Text.Encoding.UTF8.GetBytes({expression}!)",
            "global::System.ReadOnlyMemory<byte>" => scalar.UserType.EndsWith("?", StringComparison.Ordinal)
                ? $"{expression}.HasValue ? {expression}!.Value.ToArray() : null!"
                : $"{expression}.ToArray()",
            "global::System.Guid" => scalar.UserType.EndsWith("?", StringComparison.Ordinal)
                ? $"{expression}.HasValue ? {expression}!.Value.ToByteArray(bigEndian: true) : null!"
                : $"{expression}.ToByteArray(bigEndian: true)",
            "global::System.DateOnly" => scalar.UserType.EndsWith("?", StringComparison.Ordinal)
                ? $"{expression}.HasValue ? checked({expression}!.Value.DayNumber - new global::System.DateOnly(1970, 1, 1).DayNumber) : (int?)null"
                : $"checked({expression}.DayNumber - new global::System.DateOnly(1970, 1, 1).DayNumber)",
            "global::System.TimeOnly" => scalar.UserType.EndsWith("?", StringComparison.Ordinal)
                ? $"{expression}.HasValue ? {expression}!.Value.Ticks / 10 : (long?)null"
                : $"{expression}.Ticks / 10",
            "global::System.DateTime" or "global::System.DateTimeOffset" =>
                scalar.UserType.EndsWith("?", StringComparison.Ordinal)
                    ? $"{expression}.HasValue ? GeneratedNestedToUnixMicroseconds({expression}!.Value) : (long?)null"
                    : $"GeneratedNestedToUnixMicroseconds({expression})",
            _ => expression
        };
    }

    static string ConvertBinaryFromSpan(Scalar scalar, string expression)
        => scalar.NonNullableUserType switch
        {
            "string" => $"global::System.Text.Encoding.UTF8.GetString({expression})",
            "byte[]" => $"{expression}.ToArray()",
            "global::System.ReadOnlyMemory<byte>" => $"new global::System.ReadOnlyMemory<byte>({expression}.ToArray())",
            "global::System.Guid" => $"new global::System.Guid({expression}, bigEndian: true)",
            _ => $"{expression}.ToArray()"
        };

    static Leaf? FindPresenceLeaf(Node node)
        => node.Leaves.FirstOrDefault(leaf => leaf.Node.Kind == NodeKind.Leaf && !leaf.Node.Optional);

    static bool ContainsCollection(Node node)
        => node.Kind is NodeKind.List or NodeKind.Map || node.Children.Any(ContainsCollection);

    static ImmutableArray<IPropertySymbol> GetProperties(INamedTypeSymbol type)
        => type.GetMembers().OfType<IPropertySymbol>()
            .Where(static property => !property.IsStatic && !property.IsIndexer && !property.IsImplicitlyDeclared)
            .OrderBy(static property => property.Locations.FirstOrDefault()?.SourceTree?.FilePath, StringComparer.Ordinal)
            .ThenBy(static property => property.Locations.FirstOrDefault()?.SourceSpan.Start ?? int.MaxValue)
            .ThenBy(static property => property.Name, StringComparer.Ordinal)
            .ToImmutableArray();

    static bool HasUsableConstructor(INamedTypeSymbol type)
        => type.TypeKind == TypeKind.Struct || type.InstanceConstructors.Any(static constructor =>
            constructor.Parameters.Length == 0 && constructor.DeclaredAccessibility is
                Accessibility.Public or Accessibility.Internal or Accessibility.ProtectedOrInternal);

    static bool IsFlatScalar(ITypeSymbol type)
    {
        type = GetNonNullableType(type);
        if (type.SpecialType is SpecialType.System_Boolean or SpecialType.System_Byte or
            SpecialType.System_UInt16 or SpecialType.System_Int32 or SpecialType.System_UInt32 or
            SpecialType.System_Int64 or SpecialType.System_UInt64 or SpecialType.System_Single or
            SpecialType.System_Double or SpecialType.System_Decimal or SpecialType.System_String)
            return true;
        if (type is IArrayTypeSymbol { Rank: 1, ElementType.SpecialType: SpecialType.System_Byte })
            return true;
        var name = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        return name is "global::System.ReadOnlyMemory<byte>" or "global::System.DateOnly" or
            "global::System.DateTime" or "global::System.DateTimeOffset" or "global::System.TimeOnly" or
            "global::System.Guid";
    }

    static bool IsList(INamedTypeSymbol type)
        => type.OriginalDefinition.ToDisplayString() == "System.Collections.Generic.List<T>";

    static bool IsDictionary(INamedTypeSymbol type)
        => type.OriginalDefinition.ToDisplayString() == "System.Collections.Generic.Dictionary<TKey, TValue>";

    static ITypeSymbol GetNonNullableType(ITypeSymbol type)
        => type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable
            ? nullable.TypeArguments[0]
            : type;

    static bool IsNullable(ITypeSymbol type, NullableAnnotation annotation)
        => type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } ||
           type.IsReferenceType && annotation == NullableAnnotation.Annotated;

    static string GetTypeName(ITypeSymbol type)
        => type.ToDisplayString(TypeNameFormat);

    static bool IsNonNullableValueType(string type)
        => type is "bool" or "byte" or "ushort" or "int" or "uint" or "long" or "ulong" or "float" or "double" or
            "global::System.DateOnly" or "global::System.DateTime" or "global::System.DateTimeOffset" or
            "global::System.TimeOnly" or "global::System.Guid" or "global::System.ReadOnlyMemory<byte>";

    static string TrimNullable(string type)
        => type.EndsWith("?", StringComparison.Ordinal) ? type.Substring(0, type.Length - 1) : type;

    static AttributeData? GetColumnAttribute(IPropertySymbol property)
        => property.GetAttributes().FirstOrDefault(static attribute =>
            attribute.AttributeClass?.ToDisplayString() == "Plank.Schema.ParquetColumnAttribute");

    static bool AllowsAllocatingValues(INamedTypeSymbol schemaType)
        => schemaType.GetAttributes().FirstOrDefault(static attribute =>
                attribute.AttributeClass?.ToDisplayString() == "Plank.Schema.ParquetSchemaAttribute")?
            .NamedArguments.Any(static argument =>
                argument.Key == "AllowAllocatingValues" && argument.Value.Value is true) == true;

    static bool TryGetEnumValue(TypedConstant constant, out int value)
    {
        if (constant.Value is int intValue)
        {
            value = intValue;
            return true;
        }
        value = 0;
        return false;
    }

    static bool TryGetPhysicalType(int value, out string physicalType)
    {
        physicalType = value switch
        {
            0 => "Boolean", 1 => "Int32", 2 => "Int64", 3 => "Int96", 4 => "Float", 5 => "Double",
            6 => "ByteArray", 7 => "FixedLenByteArray", _ => string.Empty
        };
        return physicalType.Length > 0;
    }

    static bool TryGetEncoding(int value, out string encoding)
    {
        encoding = value switch
        {
            0 => "Plain", 1 => "PlainDictionary", 2 => "RleDictionary", 3 => "Rle", 4 => "BitPacked",
            5 => "DeltaBinaryPacked", 6 => "DeltaLengthByteArray", 7 => "DeltaByteArray", 8 => "ByteStreamSplit",
            _ => string.Empty
        };
        return encoding.Length > 0;
    }

    static bool TryGetCompression(int value, out string? compression)
    {
        compression = value switch
        {
            0 => "None",
            1 => "Snappy",
            2 => "Gzip",
            3 => "Zstd",
            4 => "Lz4",
            5 => "Brotli",
            6 => "Lz4Legacy",
            _ => null
        };
        return compression is not null;
    }

    static string GetHintName(INamedTypeSymbol schemaType)
        => ToIdentifier(schemaType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)) + ".NestedSchemaApi.g.cs";

    static string GetAccessibilityKeyword(Accessibility accessibility)
        => accessibility == Accessibility.Public ? "public" : "internal";

    static string Escape(string value)
        => Microsoft.CodeAnalysis.CSharp.SymbolDisplay.FormatLiteral(value, quote: false);

    static string EscapeIdentifier(string value)
        => Microsoft.CodeAnalysis.CSharp.SyntaxFacts.GetKeywordKind(value) ==
            Microsoft.CodeAnalysis.CSharp.SyntaxKind.None ? value : $"@{value}";

    static string ToIdentifier(string value)
    {
        var builder = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            builder.Append(i == 0 ? char.IsLetter(c) || c == '_' ? c : '_' : char.IsLetterOrDigit(c) || c == '_' ? c : '_');
        }
        return builder.Length == 0 ? "_" : builder.ToString();
    }

    enum NodeKind
    {
        Leaf,
        List,
        Map,
        Group
    }

    sealed class Model(ImmutableArray<Node> roots, ImmutableArray<Leaf> leaves)
    {
        internal ImmutableArray<Node> Roots { get; } = roots;
        internal ImmutableArray<Leaf> Leaves { get; } = leaves;
    }

    sealed class Node(NodeKind kind, string name, string propertyName, string userType, bool optional,
        Scalar? scalar, string? collectionKind, ImmutableArray<Node> children)
    {
        internal NodeKind Kind { get; } = kind;
        internal string Name { get; } = name;
        internal string PropertyName { get; } = propertyName;
        internal string UserType { get; } = userType;
        internal bool Optional { get; } = optional;
        internal Scalar? Scalar { get; } = scalar;
        internal string? CollectionKind { get; } = collectionKind;
        internal ImmutableArray<Node> Children { get; } = children;
        internal List<Leaf> Leaves { get; } = [];
    }

    sealed class Scalar(string userType, string nonNullableUserType, string physicalType,
        string? logicalExpression, ImmutableArray<string> encodings, uint typeLength, string? compression,
        int? compressionLevel, string storageType, bool supportsNestedStorage, bool isBinary)
    {
        internal string UserType { get; } = userType;
        internal string NonNullableUserType { get; } = nonNullableUserType;
        internal string PhysicalType { get; } = physicalType;
        internal string? LogicalExpression { get; } = logicalExpression;
        internal ImmutableArray<string> Encodings { get; } = encodings;
        internal uint TypeLength { get; } = typeLength;
        internal string? Compression { get; } = compression;
        internal int? CompressionLevel { get; } = compressionLevel;
        internal string StorageType { get; } = storageType;
        internal bool SupportsNestedStorage { get; } = supportsNestedStorage;
        internal bool IsBinary { get; } = isBinary;
    }

    sealed class Leaf(Node root, Node node, ImmutableArray<Node> path,
        ImmutableArray<CollectionLevel> collectionLevels, string uniqueName, string storageShapeType,
        string storageElementType, int maxRepetitionLevel, int maxDefinitionLevel, bool endpointOptional)
    {
        internal Node Root { get; } = root;
        internal Node Node { get; } = node;
        internal ImmutableArray<Node> Path { get; } = path;
        internal ImmutableArray<CollectionLevel> CollectionLevels { get; } = collectionLevels;
        internal string UniqueName { get; } = uniqueName;
        internal string StorageShapeType { get; } = storageShapeType;
        internal string StorageElementType { get; } = storageElementType;
        internal int MaxRepetitionLevel { get; } = maxRepetitionLevel;
        internal int MaxDefinitionLevel { get; } = maxDefinitionLevel;
        internal bool EndpointOptional { get; } = endpointOptional;
        internal int Ordinal { get; set; }
        internal string DescriptorName => $"s_{UniqueName}RowApiColumn";
        internal bool UsesNestedDescriptor => MaxRepetitionLevel > 0;
    }

    readonly struct CollectionLevel
    {
        internal CollectionLevel(int repetitionLevel, int definedDefinitionLevel, int elementDefinitionLevel)
        {
            RepetitionLevel = repetitionLevel;
            DefinedDefinitionLevel = definedDefinitionLevel;
            ElementDefinitionLevel = elementDefinitionLevel;
        }

        internal int RepetitionLevel { get; }
        internal int DefinedDefinitionLevel { get; }
        internal int ElementDefinitionLevel { get; }
    }
}
