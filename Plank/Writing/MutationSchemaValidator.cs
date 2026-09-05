using TextEncoding = System.Text.Encoding;
using Plank.Reading.Logical.Internal;
using Plank.Reading.Physical;
using Plank.Schema;

namespace Plank.Writing;

/// <summary>Checks the on-disk schema whose existing pages will be retained without reencoding.</summary>
static class MutationSchemaValidator
{
    internal static void Validate(ParquetSchema schema, ParquetFileMetadata metadata)
    {
        // Reader projections may reorder or omit fields, widen required fields to optional, and normalize
        // legacy LIST/MAP layouts. None of those transformations is safe when reusing encoded pages.
        if (metadata.SchemaNodeCount == 0 || metadata.SchemaNodes[0].ChildCount != schema.Definitions.Length)
            throw Mismatch("schema", "field count");

        var ordinal = 1;
        foreach (var definition in schema.Definitions)
            ValidateNode(definition, definition.Name, parentOrdinal: 0, metadata, ref ordinal);
        if (ordinal != metadata.SchemaNodeCount)
            throw Mismatch("schema", "node count");
    }

    static void ValidateNode(ColumnDefinition definition, string name, int parentOrdinal,
        ParquetFileMetadata metadata, ref int ordinal)
    {
        var nodeOrdinal = ordinal;
        var childCount = definition.Kind switch
        {
            NodeKind.Leaf => 0,
            NodeKind.List or NodeKind.Map => 1,
            _ => definition.Children.Length
        };
        var node = ValidateNodeHeader(name, definition.Kind, definition.Repetition, definition.PhysicalType,
            definition.FieldId, childCount, parentOrdinal, metadata, ref ordinal);
        if (definition.Kind is NodeKind.Leaf or NodeKind.Group &&
            !Equals(definition.LogicalType, PhysicalSchemaBinder.ConvertLogicalType(metadata, node)))
            throw Mismatch(name, "logical type");
        if (definition.PhysicalType == ParquetPhysicalType.FixedLenByteArray &&
            node.TypeLength != (definition.Options ?? ColumnOptions.Default).TypeLength)
            throw Mismatch(name, "fixed byte length");

        switch (definition.Kind)
        {
            case NodeKind.Group:
                foreach (var child in definition.Children)
                    ValidateNode(child, child.Name, nodeOrdinal, metadata, ref ordinal);
                break;
            case NodeKind.List:
            case NodeKind.Map:
                var isList = definition.Kind == NodeKind.List;
                var wrapperOrdinal = ordinal;
                var wrapper = ValidateNodeHeader(isList ? "list" : "key_value", NodeKind.Group,
                    ParquetRepetition.Repeated, physicalType: null, fieldId: null, definition.Children.Length,
                    nodeOrdinal, metadata, ref ordinal);
                if (wrapper.LogicalType.Kind != LogicalTypeKind.None)
                    throw Mismatch(name, "collection wrapper annotation");
                for (var i = 0; i < definition.Children.Length; i++)
                    ValidateNode(definition.Children[i], isList ? "element" : i == 0 ? "key" : "value",
                        wrapperOrdinal, metadata, ref ordinal);
                break;
        }
    }

    static ParquetSchemaNodeInfo ValidateNodeHeader(string name, NodeKind kind, ParquetRepetition repetition,
        ParquetPhysicalType? physicalType, int? fieldId, int childCount, int parentOrdinal,
        ParquetFileMetadata metadata, ref int ordinal)
    {
        if (ordinal >= metadata.SchemaNodeCount)
            throw Mismatch(name, "node count");
        var node = metadata.SchemaNodes[ordinal++];
        if (!string.Equals(name, TextEncoding.UTF8.GetString(metadata.SchemaNodeNameUtf8(node.Ordinal)),
                StringComparison.Ordinal) || node.ParentOrdinal != parentOrdinal)
            throw Mismatch(name, "field path or order");
        if (node.Kind != kind || node.ChildCount != childCount || node.PhysicalType != physicalType)
            throw Mismatch(name, "node shape or physical type");
        var normalizedRepetition = repetition == ParquetRepetition.Unspecified
            ? ParquetRepetition.Required : repetition;
        if (node.Repetition != normalizedRepetition)
            throw Mismatch(name, "repetition");
        if (node.FieldId != fieldId)
            throw Mismatch(name, "field ID");
        return node;
    }

    static InvalidOperationException Mismatch(string name, string reason)
        => new($"Cannot append or merge: field '{name}' differs in {reason}. " +
            "The complete physical schema must match in order, shape, repetition, types, and field IDs.");
}
