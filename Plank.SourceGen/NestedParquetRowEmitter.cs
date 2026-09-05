using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;

namespace Plank.SourceGen;

static class NestedParquetRowEmitter
{
    static readonly SymbolDisplayFormat TypeNameFormat = SymbolDisplayFormat.FullyQualifiedFormat
        .WithMiscellaneousOptions(SymbolDisplayFormat.FullyQualifiedFormat.MiscellaneousOptions |
            SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

    internal static bool TryEmit(SourceProductionContext context, INamedTypeSymbol schemaType, Compilation compilation)
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

        if (!ValidateNestedSetters(model.Roots, schemaType, compilation, out error))
        {
            context.ReportDiagnostic(Diagnostic.Create(ParquetRowGenerator.UnsupportedSchemaDeclaration,
                schemaType.Locations.FirstOrDefault(), error));
            return true;
        }

        var hasDiagnostics = false;
        foreach (var leaf in model.Leaves)
            foreach (var diagnostic in ParquetRowGenerator.ValidateSchemaColumns([leaf.Node.Scalar!.Column]))
            {
                context.ReportDiagnostic(Diagnostic.Create(diagnostic.Descriptor,
                    schemaType.Locations.FirstOrDefault(), diagnostic.Message));
                hasDiagnostics = true;
            }
        if (!hasDiagnostics)
            context.AddSource(GetHintName(schemaType), BuildSource(schemaType, model));
        return true;
    }

    static bool ValidateNestedSetters(IEnumerable<Node> nodes, INamedTypeSymbol schemaType,
        Compilation compilation, out string error)
    {
        foreach (var node in nodes)
        {
            if (node.Kind == NodeKind.Group)
            {
                var groupType = (INamedTypeSymbol)GetNonNullableType(node.TypeSymbol);
                foreach (var property in GetProperties(groupType))
                {
                    if (property.SetMethod is not null &&
                        compilation.IsSymbolAccessibleWithin(property.SetMethod, schemaType, groupType))
                        continue;

                    error = $"Nested group property '{groupType.Name}.{property.Name}' requires a setter or init accessor " +
                        $"accessible from schema type '{schemaType.ToDisplayString()}'.";
                    return false;
                }
            }
            if (!ValidateNestedSetters(node.Children, schemaType, compilation, out error))
                return false;
        }
        error = string.Empty;
        return true;
    }

    static bool RequiresNestedEmitter(INamedTypeSymbol schemaType)
    {
        foreach (var property in GetProperties(schemaType))
            if (!IsFlatScalar(property.Type) && !DeclaresConverter(property))
                return true;
        return false;
    }

    static bool DeclaresConverter(IPropertySymbol property)
        => GetColumnAttribute(property)?.NamedArguments.Any(static argument =>
            argument.Key == "Converter" && argument.Value.Value is ITypeSymbol) == true;

    static bool TryCreateModel(INamedTypeSymbol schemaType, out Model model, out string error)
    {
        var roots = new List<Node>();
        var activeTypes = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default) { schemaType };
        foreach (var property in GetProperties(schemaType))
        {
            if (!TryCreatePropertyNode(property, activeTypes, out var root, out error))
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

        if (!ValidateSiblingNames(roots, out error))
        {
            model = default!;
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

    static bool ValidateSiblingNames(IEnumerable<Node> nodes, out string error)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in nodes)
        {
            if (!names.Add(node.Name))
            {
                error = $"Duplicate column name '{node.Name}' is not allowed within the same group.";
                return false;
            }
            if (!ValidateSiblingNames(node.Children, out error))
                return false;
        }
        error = string.Empty;
        return true;
    }

    static bool TryCreatePropertyNode(IPropertySymbol property, HashSet<INamedTypeSymbol> activeTypes,
        out Node node, out string error)
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

        if (DeclaresConverter(property))
        {
            node = default!;
            error = $"Property '{property.Name}' declares a converter, which is not supported in generated nested schemas.";
            return false;
        }
        if (!TryCreateNode(property.Type, property.NullableAnnotation, parquetName, property.Name,
                property, allowLeafOverrides: true, activeTypes, out node, out error))
            return false;

        // Field IDs identify the declared property, including LIST/MAP/group containers. They must
        // not migrate to synthetic list elements when leaf options are forwarded to those elements.
        node.FieldId = GetColumnAttribute(property)?.NamedArguments
            .FirstOrDefault(static argument => argument.Key == "FieldId").Value.Value as int?;
        var optionTarget = node;
        while (optionTarget.Kind == NodeKind.List)
            optionTarget = optionTarget.Children[0];
        if (optionTarget.Kind != NodeKind.Leaf && HasLeafOverrides(property))
        {
            error = $"Property '{property.Name}' declares leaf column options on a group or map. " +
                "Place these options on its scalar properties instead.";
            return false;
        }
        return true;
    }

    static bool TryCreateNode(ITypeSymbol type, NullableAnnotation nullableAnnotation, string parquetName,
        string propertyName, IPropertySymbol? sourceProperty, bool allowLeafOverrides,
        HashSet<INamedTypeSymbol> activeTypes, out Node node, out string error)
    {
        if (IsFlatScalar(type))
        {
            if (!TryCreateScalar(type, nullableAnnotation, sourceProperty, allowLeafOverrides, out var scalar,
                    out error))
            {
                node = default!;
                return false;
            }

            node = new Node(NodeKind.Leaf, parquetName, propertyName, type,
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
                    sourceProperty, allowLeafOverrides, activeTypes, out var element, out error))
            {
                node = default!;
                return false;
            }
            node = new Node(NodeKind.List, parquetName, propertyName, type,
                IsNullable(type, nullableAnnotation), scalar: null, "Array", [element]);
            return true;
        }

        if (type is INamedTypeSymbol named && IsList(named))
        {
            if (!TryCreateNode(named.TypeArguments[0], named.TypeArgumentNullableAnnotations[0], "element", "Element",
                    sourceProperty, allowLeafOverrides, activeTypes, out var element, out error))
            {
                node = default!;
                return false;
            }
            node = new Node(NodeKind.List, parquetName, propertyName, type,
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
                    "key", "Key", sourceProperty: null, allowLeafOverrides: false, activeTypes, out var key, out error) ||
                !TryCreateNode(dictionary.TypeArguments[1], dictionary.TypeArgumentNullableAnnotations[1],
                    "value", "Value", sourceProperty: null, allowLeafOverrides: false, activeTypes, out var value, out error))
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
            node = new Node(NodeKind.Map, parquetName, propertyName, type,
                IsNullable(type, nullableAnnotation), scalar: null, "Dictionary", [key, value]);
            return true;
        }

        if (GetNonNullableType(type) is not INamedTypeSymbol groupType ||
            groupType.TypeKind is not (TypeKind.Class or TypeKind.Struct))
        {
            node = default!;
            error = $"Unsupported nested CLR type '{type.ToDisplayString()}' on property '{propertyName}'.";
            return false;
        }

        if (!activeTypes.Add(groupType))
        {
            node = default!;
            error = $"Property '{propertyName}' references recursive group type '{groupType.ToDisplayString()}'. " +
                "Recursive CLR types cannot be represented by a finite Parquet schema.";
            return false;
        }
        try
        {
            return TryCreateGroupNode(type, groupType, nullableAnnotation, parquetName, propertyName, activeTypes,
                out node, out error);
        }
        finally
        {
            // Only ancestors are recursive; independent sibling groups may share a CLR type.
            activeTypes.Remove(groupType);
        }
    }

    static bool TryCreateGroupNode(ITypeSymbol type, INamedTypeSymbol groupType, NullableAnnotation nullableAnnotation,
        string parquetName, string propertyName, HashSet<INamedTypeSymbol> activeTypes, out Node node,
        out string error)
    {
        if (!ParquetRowGenerator.ValidateDtoInheritance(groupType, out error))
        {
            node = default!;
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
            if (!TryCreatePropertyNode(property, activeTypes, out var child, out error))
            {
                node = default!;
                return false;
            }
            children.Add(child);
        }

        node = new Node(NodeKind.Group, parquetName, propertyName, type,
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
        var names = new GeneratedMemberNames(schemaType);
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
        AppendSchema(builder, names, model);
        builder.AppendLine();
        AppendDescriptors(builder, names, model);
        builder.AppendLine();
        AppendFactories(builder, names, schemaType);
        builder.AppendLine();
        AppendProjection(builder, names, model);
        builder.AppendLine();
        AppendWriters(builder, names, model, schemaType);
        builder.AppendLine();
        AppendRowReader(builder, names, model);
        builder.AppendLine();
        AppendMaterializers(builder, names, model);
        builder.AppendLine("}");
        return builder.ToString();
    }

    static void AppendSchema(StringBuilder builder, GeneratedMemberNames names, Model model)
    {
        builder.AppendLine("    public static global::Plank.Schema.ParquetSchema " + names.Root("Schema") + " { get; } = new([");
        for (var i = 0; i < model.Roots.Length; i++)
        {
            builder.Append("        ");
            AppendDefinition(builder, model.Roots[i]);
            builder.AppendLine(",");
        }
        builder.AppendLine("    ]);");
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
        if (node.FieldId is { } fieldId)
            builder.Append(" with { FieldId = ").Append(fieldId).Append(" }");
    }

    static void AppendDescriptors(StringBuilder builder, GeneratedMemberNames names, Model model)
    {
        for (var i = 0; i < model.Leaves.Length; i++)
        {
            var leaf = model.Leaves[i];
            if (leaf.UsesNestedDescriptor)
            {
                builder.Append("    static readonly global::Plank.RowApi.RowApiNestedColumnDescriptor<")
                    .Append(leaf.StorageShapeType).Append(", ").Append(leaf.StorageElementType).Append("> ")
                    .Append(names.Helper(leaf.DescriptorName)).Append(" = new(\"").Append(Escape(leaf.Root.PropertyName))
                    .Append("\", " + names.Root("Schema") + ".LeafColumns[").Append(i).Append(']');
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
                    .Append(leaf.StorageShapeType).Append("> ").Append(names.Helper(leaf.DescriptorName))
                    .Append(" = new(\"").Append(Escape(leaf.Root.PropertyName)).Append("\", " + names.Root("Schema") + ".LeafColumns[")
                    .Append(i).AppendLine("]);");
            }
        }
        builder.AppendLine();
        builder.AppendLine("    static readonly global::Plank.RowApi.RowApiColumnDescriptor[] " + names.Root("s_rowApiColumns") + " = [");
        for (var i = 0; i < model.Leaves.Length; i++)
            builder.Append("        ").Append(names.Helper(model.Leaves[i].DescriptorName)).AppendLine(",");
        builder.AppendLine("    ];");
    }

    static void AppendFactories(StringBuilder builder, GeneratedMemberNames names, INamedTypeSymbol schemaType)
    {
        var rowTypeName = schemaType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        builder.Append("    public delegate global::System.ReadOnlySpan<byte> " + names.Root("Route") + "(").Append(rowTypeName)
            .AppendLine(" row, global::Plank.IParquetBufferPool bufferPool, out global::Plank.ParquetBuffer? allocation);");
        builder.AppendLine();
        builder.AppendLine("    public static " + names.Root("DatasetWriter") + " " + names.Root("CreateDatasetWriter") + "<TFile>(" + names.Root("Route") + " route, TFile[] files, global::Plank.Dataset.DatasetWriterOptions? options = null)");
        builder.AppendLine("        where TFile : class, global::Plank.Reading.IParquetReadSource, global::Plank.Writing.IParquetWriteSource");
        builder.AppendLine("        => new(route ?? throw new global::System.ArgumentNullException(nameof(route)),");
        builder.AppendLine("            files ?? throw new global::System.ArgumentNullException(nameof(files)),");
        builder.AppendLine("            options ?? global::Plank.Dataset.DatasetWriterOptions.Default);");
        builder.AppendLine();
        builder.AppendLine("    public static " + names.Root("DatasetWriter") + " " + names.Root("CreateDatasetWriter") + "<TFile>(" + names.Root("Route") + " route, global::Plank.Dataset.DatasetFilePath filePath, TFile[] files, global::Plank.Dataset.DatasetWriterOptions? options = null)");
        builder.AppendLine("        where TFile : class, global::Plank.Writing.IParquetWriteSource");
        builder.AppendLine("        => new(route ?? throw new global::System.ArgumentNullException(nameof(route)),");
        builder.AppendLine("            filePath ?? throw new global::System.ArgumentNullException(nameof(filePath)),");
        builder.AppendLine("            files ?? throw new global::System.ArgumentNullException(nameof(files)),");
        builder.AppendLine("            options ?? global::Plank.Dataset.DatasetWriterOptions.Default);");
        builder.AppendLine();
        builder.AppendLine("    public static " + names.Root("Writer") + " " + names.Root("CreateRowWriter") + "(global::Plank.Writing.RowGroupWriter rowGroupWriter, global::Plank.Writing.ParquetWriterOptions? options = null)");
        builder.AppendLine("        => new(rowGroupWriter, options ?? global::Plank.Writing.ParquetWriterOptions.Default);");
        builder.AppendLine();
        builder.AppendLine("    public static " + names.Root("PipelineWriter") + " " + names.Root("CreateRowWriter") + "(global::System.IO.Stream stream, global::Plank.Writing.ParquetWriterOptions? options = null)");
        builder.AppendLine("        => new(stream, options ?? global::Plank.Writing.ParquetWriterOptions.Default);");
        builder.AppendLine();
        builder.AppendLine("    public static " + names.Root("PipelineWriter") + " " + names.Root("CreateRowWriter") + "<TFile>(global::Plank.Writing.ParquetFilePath filePath, TFile file, global::Plank.Writing.ParquetWriterOptions? options = null)");
        builder.AppendLine("        where TFile : class, global::Plank.Writing.IParquetWriteSource");
        builder.AppendLine("        => new(file, filePath ?? throw new global::System.ArgumentNullException(nameof(filePath)), options ?? global::Plank.Writing.ParquetWriterOptions.Default);");
        builder.AppendLine();
        builder.AppendLine("    public static " + names.Root("PipelineWriter") + " " + names.Root("CreateRowWriter") + "(global::System.IO.Stream stream, global::System.Action<int>? onFlush, global::Plank.Writing.ParquetWriterOptions? options = null)");
        builder.AppendLine("        => new(stream, onFlush, options ?? global::Plank.Writing.ParquetWriterOptions.Default);");
        builder.AppendLine();
        builder.AppendLine("    public static " + names.Root("PipelineWriter") + " " + names.Root("CreateRowWriter") + "(global::System.IO.Stream stream, uint maxParallelism, global::Plank.Writing.ParquetWriterOptions? options = null)");
        builder.AppendLine("        => new(stream, maxParallelism, options ?? global::Plank.Writing.ParquetWriterOptions.Default);");
        builder.AppendLine();
        builder.AppendLine("    public static " + names.Root("PipelineWriter") + " " + names.Root("CreateRowWriter") + "(global::System.IO.Stream stream, uint maxParallelism, global::System.Action<int>? onFlush, global::Plank.Writing.ParquetWriterOptions? options = null)");
        builder.AppendLine("        => new(stream, maxParallelism, onFlush, options ?? global::Plank.Writing.ParquetWriterOptions.Default);");
        builder.AppendLine();
        builder.AppendLine("    public static " + names.Root("RowReader") + " " + names.Root("CreateRowReader") + "(global::System.IO.Stream stream, " + names.Root("Projection") + " projection = default, global::Plank.RowApi.RowReaderOptions? options = null, global::Plank.Reading.ParquetSchemaEvolutionOptions? schemaEvolution = null)");
        builder.AppendLine("        => new(stream, projection, options ?? global::Plank.RowApi.RowReaderOptions.Default, schemaEvolution);");
        builder.AppendLine();
        builder.AppendLine("    public static " + names.Root("RowReader") + " " + names.Root("CreateRowReader") + "(global::Plank.Reading.IParquetReadSource source, " + names.Root("Projection") + " projection = default, global::Plank.RowApi.RowReaderOptions? options = null, global::Plank.Reading.ParquetSchemaEvolutionOptions? schemaEvolution = null)");
        builder.AppendLine("        => new(source, projection, options ?? global::Plank.RowApi.RowReaderOptions.Default, schemaEvolution);");
    }

    static void AppendProjection(StringBuilder builder, GeneratedMemberNames names, Model model)
    {
        builder.AppendLine("    public readonly struct " + names.Root("Projection"));
        builder.AppendLine("    {");
        builder.AppendLine("        readonly global::Plank.RowApi.RowApiColumnDescriptor[]? " + names.Helper("_columns") + ";");
        builder.AppendLine("        " + names.Root("Projection") + "(global::Plank.RowApi.RowApiColumnDescriptor[] columns) => " + names.Helper("_columns") + " = columns;");
        builder.AppendLine("        internal global::Plank.RowApi.RowApiColumnDescriptor[]? " + names.Get("projection:Columns", "Columns") + " => " + names.Helper("_columns") + ";");
        builder.AppendLine("        public static " + names.Root("Projection") + " " + names.Get("projection:All", "All") + " => default;");
        builder.AppendLine("        public static " + names.Root("Projection") + " " + names.Get("projection:None", "None") + " { get; } = new([]);");
        for (var i = 0; i < model.Roots.Length; i++)
        {
            var root = model.Roots[i];
            builder.Append("        public static " + names.Root("Projection") + " ").Append(EscapeIdentifier(root.PropertyName))
                .Append(" { get; } = new([");
            for (var leafIndex = 0; leafIndex < root.Leaves.Count; leafIndex++)
            {
                if (leafIndex > 0)
                    builder.Append(", ");
                builder.Append(names.Helper(root.Leaves[leafIndex].DescriptorName));
            }
            builder.AppendLine("]);");
        }
        builder.AppendLine("        public static " + names.Root("Projection") + " operator |(" + names.Root("Projection") + " left, " + names.Root("Projection") + " right)");
        builder.AppendLine("        {");
        builder.AppendLine("            if (left." + names.Helper("_columns") + " is null || right." + names.Helper("_columns") + " is null) return " + names.Get("projection:All", "All") + ";");
        builder.AppendLine("            if (left." + names.Helper("_columns") + ".Length == 0) return right;");
        builder.AppendLine("            if (right." + names.Helper("_columns") + ".Length == 0) return left;");
        builder.AppendLine("            var combined = new global::Plank.RowApi.RowApiColumnDescriptor[left." + names.Helper("_columns") + ".Length + right." + names.Helper("_columns") + ".Length];");
        builder.AppendLine("            left." + names.Helper("_columns") + ".CopyTo(combined, 0);");
        builder.AppendLine("            right." + names.Helper("_columns") + ".CopyTo(combined, left." + names.Helper("_columns") + ".Length);");
        builder.AppendLine("            return new " + names.Root("Projection") + "(combined);");
        builder.AppendLine("        }");
        builder.AppendLine("    }");
    }

    static void AppendWriters(StringBuilder builder, GeneratedMemberNames names, Model model, INamedTypeSymbol schemaType)
    {
        var rowSizePlan = ParquetRowGenerator.CreateRowSizePlan(model.Leaves.Select(static leaf =>
            new ParquetRowGenerator.RowSizeColumn(leaf.Node.Scalar!.PhysicalType, leaf.StorageShapeType,
                leaf.Node.Scalar.TypeLength,
                leaf.CollectionLevels.IsEmpty && leaf.Node.Scalar.StorageType != "byte[]")));
        var rowTypeName = schemaType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        builder.AppendLine("    public struct " + names.Root("Writer"));
        builder.AppendLine("    {");
        builder.AppendLine("        readonly global::Plank.RowApi.RowGroupWriterCore<" + names.Root("BufferSlot") + "> " + names.Helper("_core") + ";");
        builder.AppendLine("        internal " + names.Root("Writer") + "(global::Plank.Writing.RowGroupWriter rowGroupWriter, global::Plank.Writing.ParquetWriterOptions options)");
        builder.AppendLine("        {");
        builder.AppendLine("            _ = options ?? throw new global::System.ArgumentNullException(nameof(options));");
        builder.AppendLine("            var slot = new " + names.Root("BufferSlot") + "(rowGroupWriter, options.RowApiInitialRowCapacity);");
        builder.AppendLine("            " + names.Helper("_core") + " = new global::Plank.RowApi.RowGroupWriterCore<" + names.Root("BufferSlot") + ">(rowGroupWriter, slot);");
        builder.AppendLine("        }");
        builder.AppendLine("        public " + names.Root("Row") + " GetRow() => " + names.Helper("_core") + ".GetSlotForRow().GetRow();");
        builder.AppendLine("        public void Write() => " + names.Helper("_core") + ".Write();");
        builder.AppendLine("    }");
        builder.AppendLine();
        builder.AppendLine("    public sealed class " + names.Root("PipelineWriter") + " : global::Plank.RowApi.PipelineRowWriterBase<" + names.Root("BufferSlot") + ">");
        builder.AppendLine("    {");
        if (rowSizePlan.IsFixed)
            builder.AppendLine("        readonly int " + names.Helper("_rowsPerGroup") + ";");
        builder.AppendLine("        bool " + names.Helper("_rowPending") + ";");
        builder.AppendLine();
        builder.AppendLine("        internal " + names.Root("PipelineWriter") + "(global::System.IO.Stream stream, global::Plank.Writing.ParquetWriterOptions options)");
        builder.AppendLine("            : this(stream, options.RowApiMaxParallelism, null, options) { }");
        builder.AppendLine("        internal " + names.Root("PipelineWriter") + "(global::Plank.Writing.IParquetWriteSource file, global::Plank.Writing.ParquetFilePath filePath, global::Plank.Writing.ParquetWriterOptions options)");
        builder.AppendLine("            : base(file, filePath, " + names.Root("Schema") + ", options.RowApiMaxParallelism, null, options, options.RowApiInitialRowCapacity, \"PlankNestedRowApiWorker\")");
        builder.AppendLine("        {");
        if (rowSizePlan.IsFixed)
            builder.Append("            " + names.Helper("_rowsPerGroup") + " = GetFixedRowsPerGroup(").Append(rowSizePlan.FixedSizeExpression).AppendLine(");");
        builder.AppendLine("        }");
        builder.AppendLine("        internal " + names.Root("PipelineWriter") + "(global::System.IO.Stream stream, global::System.Action<int>? onFlush, global::Plank.Writing.ParquetWriterOptions options)");
        builder.AppendLine("            : this(stream, options.RowApiMaxParallelism, onFlush, options) { }");
        builder.AppendLine("        internal " + names.Root("PipelineWriter") + "(global::System.IO.Stream stream, uint maxParallelism, global::Plank.Writing.ParquetWriterOptions options)");
        builder.AppendLine("            : this(stream, maxParallelism, null, options) { }");
        builder.AppendLine("        internal " + names.Root("PipelineWriter") + "(global::System.IO.Stream stream, uint maxParallelism, global::System.Action<int>? onFlush, global::Plank.Writing.ParquetWriterOptions options)");
        builder.AppendLine("            : base(stream, " + names.Root("Schema") + ", maxParallelism, onFlush, options, options.RowApiInitialRowCapacity, \"PlankNestedRowApiWorker\")");
        builder.AppendLine("        {");
        if (rowSizePlan.IsFixed)
            builder.Append("            " + names.Helper("_rowsPerGroup") + " = GetFixedRowsPerGroup(").Append(rowSizePlan.FixedSizeExpression).AppendLine(");");
        builder.AppendLine("        }");
        builder.AppendLine("        protected override " + names.Root("BufferSlot") + " CreateSlot(global::Plank.Writing.ParquetWriter writer) => new(writer, RowBatchSize);");
        builder.AppendLine("        [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]");
        builder.AppendLine("        public " + names.Root("Row") + " GetRow() => GetSlotForNextRow().GetRow();");
        RowCursorEmitter.AppendFactory(builder, names);
        builder.AppendLine("        [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]");
        builder.AppendLine("        internal " + names.Root("BufferSlot") + " GetSlotForNextRow()");
        builder.AppendLine("        {");
        builder.AppendLine("            var slot = GetSlotForRow();");
        builder.AppendLine("            if (" + names.Helper("_rowPending") + ")");
        if (rowSizePlan.IsFixed)
            builder.AppendLine("                slot = CommitFixedRow(slot, " + names.Helper("_rowsPerGroup") + ");");
        else
            builder.Append("                slot = CommitVariableRow(slot, slot.GetRowSize(")
                .Append(rowSizePlan.FixedSizeExpression).AppendLine("));");
        builder.AppendLine("            else");
        builder.AppendLine("                " + names.Helper("_rowPending") + " = true;");
        builder.AppendLine("            return slot;");
        builder.AppendLine("        }");
        builder.AppendLine("        public void Complete()");
        builder.AppendLine("        {");
        builder.AppendLine("            if (" + names.Helper("_rowPending") + ")");
        builder.AppendLine("            {");
        builder.AppendLine("                var slot = GetSlotForRow();");
        if (rowSizePlan.IsFixed)
            builder.AppendLine("                CommitFixedRow(slot, " + names.Helper("_rowsPerGroup") + ");");
        else
            builder.Append("                CommitVariableRow(slot, slot.GetRowSize(")
                .Append(rowSizePlan.FixedSizeExpression).AppendLine("));");
        builder.AppendLine("                " + names.Helper("_rowPending") + " = false;");
        builder.AppendLine("            }");
        builder.AppendLine("            CompleteWriter();");
        builder.AppendLine("        }");
        builder.AppendLine("        public void Reset(global::System.IO.Stream stream)");
        builder.AppendLine("        {");
        builder.AppendLine("            ResetWriter(stream);");
        builder.AppendLine("            " + names.Helper("_rowPending") + " = false;");
        builder.AppendLine("        }");
        builder.AppendLine("    }");
        builder.AppendLine();
        builder.Append("    public sealed class " + names.Root("DatasetWriter") + " : global::Plank.Dataset.DatasetWriterBase<")
            .Append(rowTypeName).AppendLine(">, global::System.IDisposable");
        builder.AppendLine("    {");
        builder.AppendLine("        readonly " + names.Root("Route") + " " + names.Helper("_route") + ";");
        builder.AppendLine("        internal " + names.Root("DatasetWriter") + "(" + names.Root("Route") + " route, global::Plank.Writing.IParquetWriteSource[] files, global::Plank.Dataset.DatasetWriterOptions options)");
        builder.AppendLine("            : base(" + names.Root("Schema") + ", " + names.Root("s_rowApiColumns") + ", options.WriterOptions.RowApiInitialRowCapacity, files, options)");
        builder.AppendLine("        {");
        builder.AppendLine("            " + names.Helper("_route") + " = route;");
        builder.AppendLine("            InitializeSlots();");
        builder.AppendLine("        }");
        builder.AppendLine("        internal " + names.Root("DatasetWriter") + "(" + names.Root("Route") + " route, global::Plank.Dataset.DatasetFilePath filePath, global::Plank.Writing.IParquetWriteSource[] files, global::Plank.Dataset.DatasetWriterOptions options)");
        builder.AppendLine("            : base(" + names.Root("Schema") + ", " + names.Root("s_rowApiColumns") + ", options.WriterOptions.RowApiInitialRowCapacity, files, filePath, options)");
        builder.AppendLine("        {");
        builder.AppendLine("            " + names.Helper("_route") + " = route;");
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
                    .Append(">(slotIndex, ").Append(leaf.Ordinal).Append(", rowIndex, ")
                    .Append(names.Get("project:" + leaf.UniqueName, "Project" + leaf.UniqueName))
                    .Append("(value").Append(i).AppendLine("));");
            }
        }
        builder.AppendLine("        }");
        builder.Append("        protected override global::System.ReadOnlySpan<byte> SelectPath(")
            .Append(rowTypeName)
            .AppendLine(" row, global::Plank.IParquetBufferPool bufferPool, out global::Plank.ParquetBuffer? allocation)");
        builder.AppendLine("            => " + names.Helper("_route") + "(row, bufferPool, out allocation);");
        builder.Append("        public void Queue(").Append(rowTypeName).AppendLine(" row) => QueueRow(row);");
        builder.AppendLine("        public void Dispose() => DisposeDataset();");
        builder.AppendLine("    }");
        builder.AppendLine();
        builder.AppendLine("    public sealed class " + names.Root("BufferSlot") + " : global::Plank.RowApi.RowBufferSlot");
        builder.AppendLine("    {");
        RowCursorEmitter.AppendSlotMembers(builder);
        for (var i = 0; i < model.Leaves.Length; i++)
            builder.Append("        internal ").Append(model.Leaves[i].StorageShapeType).Append("[] _column")
                .Append(i).AppendLine(" = null!;");
        builder.AppendLine();
        builder.AppendLine("        internal " + names.Root("BufferSlot") + "(global::Plank.Writing.RowGroupWriter rowGroupWriter, int rowCount)");
        builder.AppendLine("            : base(rowGroupWriter, " + names.Root("s_rowApiColumns") + ", rowCount)");
        builder.AppendLine("        {");
        builder.AppendLine("            RefreshBuffers();");
        builder.AppendLine("        }");
        builder.AppendLine();
        builder.AppendLine("        internal " + names.Root("BufferSlot") + "(global::Plank.Writing.ParquetWriter writer, int rowCount)");
        builder.AppendLine("            : base(writer, " + names.Root("s_rowApiColumns") + ", rowCount)");
        builder.AppendLine("        {");
        builder.AppendLine("            RefreshBuffers();");
        builder.AppendLine("        }");
        builder.AppendLine();
        builder.AppendLine("        [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]");
        builder.AppendLine("        internal " + names.Root("Row") + " GetRow()");
        builder.AppendLine("        {");
        builder.AppendLine("            return new " + names.Root("Row") + "(Index, this);");
        builder.AppendLine("        }");
        if (!rowSizePlan.IsFixed)
        {
            builder.AppendLine();
            builder.AppendLine("        internal ulong GetRowSize(ulong fixedSizeBytes)");
            builder.AppendLine("        {");
            builder.AppendLine("            var size = fixedSizeBytes;");
            foreach (var columnIndex in rowSizePlan.VariableColumnIndices)
            {
                var leaf = model.Leaves[columnIndex];
                builder.Append("            size = checked(size + EstimateValueSize(")
                    .Append(UncheckedBufferElement($"_column{columnIndex}", "Index"))
                    .Append(", global::Plank.Schema.ParquetPhysicalType.")
                    .Append(leaf.Node.Scalar!.PhysicalType).Append(", ").Append(leaf.Node.Scalar.TypeLength)
                    .AppendLine("U));");
            }
            builder.AppendLine("            return size;");
            builder.AppendLine("        }");
        }
        builder.AppendLine();
        builder.AppendLine("        protected override void OnBuffersResized()");
        builder.AppendLine("            => RefreshBuffers();");
        builder.AppendLine();
        builder.AppendLine("        void RefreshBuffers()");
        builder.AppendLine("        {");
        for (var i = 0; i < model.Leaves.Length; i++)
            builder.Append("            _column").Append(i).Append(" = GetValues<")
                .Append(model.Leaves[i].StorageShapeType).Append(">(").Append(i).AppendLine(");");
        builder.AppendLine("        }");
        builder.AppendLine("    }");
        builder.AppendLine();
        builder.AppendLine("    /// <summary>A writable view over the current row buffers.</summary>");
        builder.AppendLine("    /// <remarks>Use only until the writer advances, completes, resets, or is disposed.</remarks>");
        builder.AppendLine("    public readonly ref struct " + names.Root("Row"));
        builder.AppendLine("    {");
        builder.AppendLine("        readonly int " + names.Helper("_index") + ";");
        builder.AppendLine("        readonly " + names.Root("BufferSlot") + " " + names.Helper("_ownerSlot") + ";");
        builder.AppendLine("        internal " + names.Root("Row") + "(int index, " + names.Root("BufferSlot") + " ownerSlot)");
        builder.AppendLine("        {");
        builder.AppendLine("            " + names.Helper("_index") + " = index;");
        builder.AppendLine("            " + names.Helper("_ownerSlot") + " = ownerSlot;");
        builder.AppendLine("        }");
        for (var i = 0; i < model.Roots.Length; i++)
        {
            builder.AppendLine();
            AppendWriteRowProperty(builder, names, model.Roots[i],
                leaf => UncheckedBufferElement($"{names.Helper("_ownerSlot")}._column{leaf.Ordinal}", names.Helper("_index")));
        }
        builder.AppendLine("    }");
        RowCursorEmitter.AppendCursor(builder, names,
            model.Leaves.Select(static leaf => leaf.StorageShapeType).ToArray(), () =>
            {
                foreach (var root in model.Roots)
                    AppendWriteRowProperty(builder, names, root,
                        leaf => $"global::System.Runtime.CompilerServices.Unsafe.Add(ref {names.Helper("_buffers")}._column{leaf.Ordinal}, {names.Helper("_index")})");
            });
    }

    static void AppendWriteRowProperty(StringBuilder builder, GeneratedMemberNames names, Node root, Func<Leaf, string> element)
    {
        if (root.Kind == NodeKind.Leaf)
        {
            var leaf = root.Leaves[0];
            builder.Append("        public ").Append(root.UserType).Append(' ')
                .Append(EscapeIdentifier(root.PropertyName)).AppendLine();
            builder.AppendLine("        {");
            builder.Append("            set => ").Append(element(leaf)).AppendLine(" = value;");
            builder.AppendLine("        }");
            return;
        }

        builder.Append("        public ").Append(root.UserType).Append(' ')
            .Append(EscapeIdentifier(root.PropertyName)).AppendLine();
        builder.AppendLine("        {");
        builder.Append("            get => ").Append(names.Get("read:" + root.PropertyName, "Read" + ToIdentifier(root.PropertyName))).Append('(');
        AppendLeafArguments(builder, root, element);
        builder.AppendLine(");");
        builder.AppendLine("            set");
        builder.AppendLine("            {");
        for (var i = 0; i < root.Leaves.Count; i++)
        {
            var leaf = root.Leaves[i];
            builder.Append("                ")
                .Append(element(leaf))
                .Append(" = ")
                .Append(names.Get("project:" + leaf.UniqueName, "Project" + leaf.UniqueName)).AppendLine("(value);");
        }
        builder.AppendLine("            }");
        builder.AppendLine("        }");
    }

    static string UncheckedBufferElement(string bufferExpression, string indexExpression)
        => $"global::System.Runtime.CompilerServices.Unsafe.Add(ref global::System.Runtime.InteropServices.MemoryMarshal.GetArrayDataReference({bufferExpression}), {indexExpression})";

    static void AppendRowReader(StringBuilder builder, GeneratedMemberNames names, Model model)
    {
        builder.AppendLine("    public sealed class " + names.Root("RowReader") + " : global::System.IDisposable");
        builder.AppendLine("    {");
        builder.AppendLine("        readonly global::Plank.RowApi.RowReaderCore " + names.Helper("_core") + ";");
        builder.AppendLine("        internal " + names.Root("RowReader") + "(global::System.IO.Stream stream, " + names.Root("Projection") + " projection, global::Plank.RowApi.RowReaderOptions options, global::Plank.Reading.ParquetSchemaEvolutionOptions? schemaEvolution)");
        builder.AppendLine("            => " + names.Helper("_core") + " = new global::Plank.RowApi.RowReaderCore(stream, " + names.Root("Schema") + ", " + names.Root("s_rowApiColumns") + ", projection." + names.Get("projection:Columns", "Columns") + ", options, schemaEvolution);");
        builder.AppendLine("        internal " + names.Root("RowReader") + "(global::Plank.Reading.IParquetReadSource source, " + names.Root("Projection") + " projection, global::Plank.RowApi.RowReaderOptions options, global::Plank.Reading.ParquetSchemaEvolutionOptions? schemaEvolution)");
        builder.AppendLine("            => " + names.Helper("_core") + " = new global::Plank.RowApi.RowReaderCore(source, " + names.Root("Schema") + ", " + names.Root("s_rowApiColumns") + ", projection." + names.Get("projection:Columns", "Columns") + ", options, schemaEvolution);");
        builder.AppendLine("        public Enumerator GetEnumerator() => new(this);");
        builder.AppendLine("        public void Reset(global::System.IO.Stream stream, " + names.Root("Projection") + " projection = default, global::Plank.Reading.ParquetSchemaEvolutionOptions? schemaEvolution = null) => " + names.Helper("_core") + ".Reset(stream, projection." + names.Get("projection:Columns", "Columns") + ", schemaEvolution);");
        builder.AppendLine("        public void Reset(global::Plank.Reading.IParquetReadSource source, " + names.Root("Projection") + " projection = default, global::Plank.Reading.ParquetSchemaEvolutionOptions? schemaEvolution = null) => " + names.Helper("_core") + ".Reset(source, projection." + names.Get("projection:Columns", "Columns") + ", schemaEvolution);");
        builder.AppendLine("        public " + names.Root("ReadRow") + " Current { get { " + names.Helper("_core") + ".ThrowIfNotPositioned(); return new " + names.Root("ReadRow") + "(" + names.Helper("_core") + "); } }");
        builder.AppendLine("        public bool MoveNext() => " + names.Helper("_core") + ".MoveNext();");
        builder.AppendLine("        public void Dispose() => " + names.Helper("_core") + ".Dispose();");
        builder.AppendLine("        public readonly struct Enumerator : global::System.IDisposable");
        builder.AppendLine("        {");
        builder.AppendLine("            readonly " + names.Root("RowReader") + " " + names.Helper("_reader") + ";");
        builder.AppendLine("            internal Enumerator(" + names.Root("RowReader") + " reader) => " + names.Helper("_reader") + " = reader;");
        builder.AppendLine("            public " + names.Root("ReadRow") + " Current => " + names.Helper("_reader") + ".Current;");
        builder.AppendLine("            public bool MoveNext() => " + names.Helper("_reader") + ".MoveNext();");
        builder.AppendLine("            public void Dispose() { }");
        builder.AppendLine("        }");
        builder.AppendLine("    }");
        builder.AppendLine();
        builder.AppendLine("    public readonly ref struct " + names.Root("ReadRow"));
        builder.AppendLine("    {");
        builder.AppendLine("        readonly global::Plank.RowApi.RowReaderCore " + names.Helper("_core") + ";");
        builder.AppendLine("        internal " + names.Root("ReadRow") + "(global::Plank.RowApi.RowReaderCore core) => " + names.Helper("_core") + " = core;");
        for (var i = 0; i < model.Roots.Length; i++)
        {
            builder.AppendLine();
            AppendReadRowProperty(builder, names, model.Roots[i]);
        }
        builder.AppendLine("    }");
    }

    static void AppendReadRowProperty(StringBuilder builder, GeneratedMemberNames names, Node root)
    {
        if (root.Kind == NodeKind.Leaf)
        {
            var leaf = root.Leaves[0];
            if (leaf.Node.Scalar!.IsBinary)
            {
                if (IsRetainableBinary(leaf.Node.Scalar))
                {
                    builder.Append("        public global::Plank.RowApi.RowReaderBinaryValue ")
                        .Append(EscapeIdentifier(root.PropertyName)).AppendLine();
                    builder.Append("            => " + names.Helper("_core") + ".GetCurrentBinary(").Append(names.Helper(leaf.DescriptorName))
                        .AppendLine(");");
                }
                else
                {
                    builder.Append("        public ").Append(root.UserType).Append(' ')
                        .Append(EscapeIdentifier(root.PropertyName)).AppendLine();
                    builder.AppendLine("        {");
                    builder.AppendLine("            get");
                    builder.AppendLine("            {");
                    builder.Append("                var value = " + names.Helper("_core") + ".GetCurrentBinary(").Append(names.Helper(leaf.DescriptorName))
                        .AppendLine(");");
                    builder.AppendLine("                if (value.IsNull) return default!;");
                    builder.Append("                return ").Append(ConvertBinaryFromSpan(leaf.Node.Scalar, "value.Value"))
                        .AppendLine(";");
                    builder.AppendLine("            }");
                    builder.AppendLine("        }");
                }
            }
            else
            {
                builder.Append("        public ref ").Append(root.UserType).Append(' ')
                    .Append(EscapeIdentifier(root.PropertyName)).Append(" => ref " + names.Helper("_core") + ".GetCurrent(")
                    .Append(names.Helper(leaf.DescriptorName)).AppendLine(");");
            }
            return;
        }

        builder.Append("        public ").Append(root.UserType).Append(' ')
            .Append(EscapeIdentifier(root.PropertyName)).Append(" => ")
            .Append(names.Get("read:" + root.PropertyName, "Read" + ToIdentifier(root.PropertyName))).Append('(');
        AppendLeafArguments(builder, root, leaf => leaf.UsesNestedDescriptor
            ? $"{names.Helper("_core")}.GetCurrentNested({names.Helper(leaf.DescriptorName)})"
            : leaf.Node.Scalar!.IsBinary
                ? $"{names.Get("read-binary:" + leaf.UniqueName, "Read" + leaf.UniqueName + "Binary")}({names.Helper("_core")}.GetCurrentBinary({names.Helper(leaf.DescriptorName)}))"
            : $"{names.Helper("_core")}.GetCurrent({names.Helper(leaf.DescriptorName)})");
        builder.AppendLine(");");
    }

    static void AppendMaterializers(StringBuilder builder, GeneratedMemberNames names, Model model)
    {
        if (model.Leaves.Any(static leaf => leaf.MaxRepetitionLevel > 0 &&
                leaf.Node.Scalar!.NonNullableUserType is "global::System.DateTime" or
                    "global::System.DateTimeOffset"))
        {
            builder.AppendLine();
            builder.AppendLine("    static long " + names.Root("GeneratedNestedToUnixMicroseconds") + "(global::System.DateTime value)");
            builder.AppendLine("    {");
            builder.AppendLine("        if (value.Kind != global::System.DateTimeKind.Utc)");
            builder.AppendLine("            throw new global::System.InvalidOperationException($\"DateTime values must have kind 'Utc', got '{value.Kind}'.\");");
            builder.AppendLine("        return " + names.Root("GeneratedNestedToUnixMicroseconds") + "(value.Ticks);");
            builder.AppendLine("    }");
            builder.AppendLine();
            builder.AppendLine("    static long " + names.Root("GeneratedNestedToUnixMicroseconds") + "(global::System.DateTimeOffset value)");
            builder.AppendLine("        => " + names.Root("GeneratedNestedToUnixMicroseconds") + "(value.UtcDateTime.Ticks);");
            builder.AppendLine();
            builder.AppendLine("    static long " + names.Root("GeneratedNestedToUnixMicroseconds") + "(long ticks)");
            builder.AppendLine("    {");
            builder.AppendLine("        var delta = checked(ticks - global::System.DateTime.UnixEpoch.Ticks);");
            builder.AppendLine("        var result = delta / 10;");
            builder.AppendLine("        return delta >= 0 || delta % 10 == 0 ? result : result - 1;");
            builder.AppendLine("    }");
        }

        for (var i = 0; i < model.Leaves.Length; i++)
        {
            var leaf = model.Leaves[i];
            if (leaf.Root.Kind == NodeKind.Leaf || leaf.UsesNestedDescriptor || !leaf.Node.Scalar!.IsBinary)
                continue;

            builder.AppendLine();
            builder.Append("    static ").Append(leaf.StorageShapeType).Append(' ')
                .Append(names.Get("read-binary:" + leaf.UniqueName, "Read" + leaf.UniqueName + "Binary")).AppendLine("(global::Plank.RowApi.RowReaderBinaryValue value)");
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
            AppendReadMaterializer(builder, names, root);
            for (var leafIndex = 0; leafIndex < root.Leaves.Count; leafIndex++)
            {
                builder.AppendLine();
                AppendWriteProjector(builder, names, root, root.Leaves[leafIndex]);
            }
        }
    }

    static void AppendReadMaterializer(StringBuilder builder, GeneratedMemberNames names, Node root)
    {
        builder.Append("    static ").Append(root.UserType).Append(' ')
            .Append(names.Get("read:" + root.PropertyName, "Read" + ToIdentifier(root.PropertyName))).Append('(');
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
                    .Append(")global::System.Array.CreateInstance(typeof(").Append(element.RuntimeType)
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

    static void AppendWriteProjector(StringBuilder builder, GeneratedMemberNames names, Node root, Leaf leaf)
    {
        builder.Append("    static ").Append(leaf.StorageShapeType).Append(' ')
            .Append(names.Get("project:" + leaf.UniqueName, "Project" + leaf.UniqueName)).Append('(').Append(root.UserType).AppendLine(" value)");
        builder.AppendLine("    {");
        builder.Append("        ").Append(leaf.StorageShapeType).AppendLine(" result = default!;");
        var indexes = new List<string>();
        AppendProjectAssignment(builder, names, root, leaf, "result", "value", depth: 0, indent: 2, indexes);
        builder.AppendLine("        return result;");
        builder.AppendLine("    }");
    }

    static void AppendProjectAssignment(StringBuilder builder, GeneratedMemberNames names, Node node, Leaf leaf, string target,
        string source, int depth, int indent, List<string> indexes)
    {
        var padding = new string(' ', indent * 4);
        if (node.Kind == NodeKind.Leaf)
        {
            builder.Append(padding).Append(target).Append(" = ").Append(ConvertToStorage(names, leaf, source))
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
                AppendProjectAssignment(builder, names, child, leaf, target,
                    source + (node.IsNullableValueType ? ".Value." : "!.") + EscapeIdentifier(child.PropertyName),
                    depth, indent + 1, indexes);
            }
            else
                AppendProjectAssignment(builder, names, child, leaf, target,
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
            AppendProjectAssignment(builder, names, collectionChild, leaf, target + "[" + index + "]",
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
            AppendProjectAssignment(builder, names, collectionChild, leaf, target + "[" + index + "]",
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
        if (!ParquetRowGenerator.TryExtractColumn(type, nullableAnnotation,
                allowOverrides ? property : null, out var column, out error))
        {
            scalar = default!;
            return false;
        }
        var physicalType = column.PhysicalType;
        if (!ParquetRowGenerator.IsSupportedMapping(column, column.ClrTypeName))
        {
            scalar = default!;
            error = $"Property '{property?.Name}' CLR type '{column.ClrTypeName}' is incompatible with physical type '{physicalType}'.";
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
        // Decimal is materialized by the typed row reader even when its physical carrier is binary.
        // Guid/string/binary values use the span reader and their explicit conversions instead.
        scalar = new Scalar(userType, normalized, column, storageType, supportsNestedStorage,
            normalized != "decimal" && (physicalType is "ByteArray" or "FixedLenByteArray" or "Int96"));
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

    static bool HasLeafOverrides(IPropertySymbol property)
    {
        var attribute = GetColumnAttribute(property);
        return attribute is not null &&
            (attribute.ConstructorArguments.Any(static argument => argument.Type?.ToDisplayString() ==
                "Plank.Schema.ParquetPhysicalType") ||
             attribute.NamedArguments.Any(static argument => argument.Key != "FieldId"));
    }

    static string GetColumnOptionsExpression(Node node)
        => ParquetRowGenerator.GetColumnOptionsExpression(node.Scalar!.Column);

    static string ConvertFromStorage(Leaf leaf, string expression)
    {
        var scalar = leaf.Node.Scalar!;
        if (leaf.EndpointOptional && !leaf.Node.Optional && IsNonNullableValueType(scalar.NonNullableUserType))
            expression += "!.Value";
        if (leaf.MaxRepetitionLevel == 0)
            return expression;
        return scalar.NonNullableUserType switch
        {
            "byte" or "ushort" or "uint" or "ulong" =>
                ConvertUnsignedInteger(leaf, expression, scalar.NonNullableUserType),
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

    static string ConvertToStorage(GeneratedMemberNames names, Leaf leaf, string expression)
    {
        var scalar = leaf.Node.Scalar!;
        if (leaf.MaxRepetitionLevel == 0)
            return expression;
        return scalar.NonNullableUserType switch
        {
            "byte" or "ushort" or "uint" or "ulong" =>
                ConvertUnsignedInteger(leaf, expression, scalar.StorageType),
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
                    ? $"{expression}.HasValue ? {names.Root("GeneratedNestedToUnixMicroseconds")}({expression}!.Value) : (long?)null"
                    : $"{names.Root("GeneratedNestedToUnixMicroseconds")}({expression})",
            _ => expression
        };
    }

    static string ConvertUnsignedInteger(Leaf leaf, string expression, string targetType)
    {
        // Lift the conversion for optional values so null survives in both directions.
        // Keep the unchecked conversion: UINT32/UINT64 use signed physical storage and
        // values above the signed maximum must retain their original bit patterns.
        if (leaf.Node.Optional)
            targetType += "?";
        return $"unchecked(({targetType}){expression})";
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

    static bool IsRetainableBinary(Scalar scalar)
        => scalar.NonNullableUserType is "byte[]" or "global::System.ReadOnlyMemory<byte>";

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
        => type is "bool" or "byte" or "ushort" or "int" or "uint" or "long" or "ulong" or "float" or "double" or "decimal" or
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

    sealed class Node(NodeKind kind, string name, string propertyName, ITypeSymbol userType, bool optional,
        Scalar? scalar, string? collectionKind, ImmutableArray<Node> children)
    {
        internal int? FieldId { get; set; }
        internal NodeKind Kind { get; } = kind;
        internal string Name { get; } = name;
        internal string PropertyName { get; } = propertyName;
        internal ITypeSymbol TypeSymbol { get; } = userType;
        internal string UserType { get; } = GetTypeName(userType);
        // Runtime type expressions omit reference annotations, but retain Nullable<T>.
        internal string RuntimeType { get; } = userType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        internal bool IsNullableValueType { get; } = userType is INamedTypeSymbol
            { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T };
        internal bool Optional { get; } = optional;
        internal Scalar? Scalar { get; } = scalar;
        internal string? CollectionKind { get; } = collectionKind;
        internal ImmutableArray<Node> Children { get; } = children;
        internal List<Leaf> Leaves { get; } = [];
    }

    sealed class Scalar(string userType, string nonNullableUserType, ParquetRowGenerator.SchemaColumn column,
        string storageType, bool supportsNestedStorage, bool isBinary)
    {
        internal ParquetRowGenerator.SchemaColumn Column { get; } = column;
        internal string UserType { get; } = userType;
        internal string NonNullableUserType { get; } = nonNullableUserType;
        internal string PhysicalType => Column.PhysicalType;
        internal string? LogicalExpression => Column.LogicalType is { } logical
            ? ParquetRowGenerator.GetLogicalTypeExpression(logical) : null;
        internal uint TypeLength => Column.TypeLength;
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
