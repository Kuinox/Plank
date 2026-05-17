using System.Collections.Immutable;
using Plank.Schema;

namespace Plank.StrykerTests;

/// <summary>
/// Tests targeting surviving mutants in ParquetSchema.cs:
/// - Line 70: Unspecified repetition → Required normalization
/// - Line 100/122: leaf path building and LeafProjectionInfo construction
/// - Lines 144-145: nextRepeatedLevel and nextDefinitionLevel computation
/// - Lines 154/162: repetition override in leaf nodes
/// </summary>
public class ParquetSchemaTests
{
    // ──────────────── Repetition normalization (line 70) ────────────────

    [Fact]
    public void Column_UnspecifiedRepetition_NormalizedToRequired()
    {
        // Column with Unspecified repetition should be normalized to Required in schema
        var col = new Column("v", ParquetPhysicalType.Int32,
            new ColumnOptions(ParquetRepetition.Unspecified));
        var schema = new ParquetSchema([col]);
        // Verify that the definition in the schema has Required repetition
        Assert.Equal(1, schema.Definitions.Length);
        Assert.Equal(ParquetRepetition.Required, schema.Definitions[0].Repetition);
    }

    [Fact]
    public void Column_OptionalRepetition_PreservedInDefinition()
    {
        var col = new Column("v", ParquetPhysicalType.Int32,
            new ColumnOptions(ParquetRepetition.Optional));
        var schema = new ParquetSchema([col]);
        Assert.Equal(ParquetRepetition.Optional, schema.Definitions[0].Repetition);
    }

    [Fact]
    public void Column_RequiredRepetition_PreservedInDefinition()
    {
        var col = new Column("v", ParquetPhysicalType.Int32,
            new ColumnOptions(ParquetRepetition.Required));
        var schema = new ParquetSchema([col]);
        Assert.Equal(ParquetRepetition.Required, schema.Definitions[0].Repetition);
    }

    // ──────────────── Leaf paths (line 100) ────────────────

    [Fact]
    public void Schema_TwoColumns_TwoLeafPaths()
    {
        var schema = new ParquetSchema([
            new Column("first", ParquetPhysicalType.Int32),
            new Column("second", ParquetPhysicalType.Float)
        ]);
        Assert.Equal(2, schema.LeafPaths.Length);
        Assert.Equal("first", schema.LeafPaths[0][0]);
        Assert.Equal("second", schema.LeafPaths[1][0]);
    }

    [Fact]
    public void Schema_SingleColumn_SingleLeafPath()
    {
        var schema = new ParquetSchema([
            new Column("my_col", ParquetPhysicalType.Double)
        ]);
        Assert.Equal(1, schema.LeafPaths.Length);
        Assert.Equal("my_col", schema.LeafPaths[0][0]);
    }

    [Fact]
    public void Schema_Empty_NoLeafPaths()
    {
        var schema = new ParquetSchema(ImmutableArray<Column>.Empty);
        Assert.Empty(schema.LeafPaths);
    }

    // ──────────────── LeafProjectionInfo construction (line 122) ────────────────

    [Fact]
    public void Schema_RequiredColumn_MaxLevelsZero()
    {
        var schema = new ParquetSchema([new Column("v", ParquetPhysicalType.Int32)]);
        Assert.Equal(1, schema.LeafProjectionInfos.Length);
        var info = schema.LeafProjectionInfos[0];
        Assert.Equal(0, info.MaxRepetitionLevel);
        Assert.Equal(0, info.MaxDefinitionLevel);
        Assert.False(info.IsList);
    }

    [Fact]
    public void Schema_OptionalColumn_MaxDefinitionLevelOne()
    {
        var schema = new ParquetSchema([new Column("v", ParquetPhysicalType.Int32,
            new ColumnOptions(ParquetRepetition.Optional))]);
        var info = schema.LeafProjectionInfos[0];
        Assert.Equal(0, info.MaxRepetitionLevel);
        Assert.Equal(1, info.MaxDefinitionLevel); // optional → definition level 1
        Assert.False(info.IsList);
    }

    // ──────────────── Nested schema via ColumnDefinition (lines 144-165) ────────────────

    [Fact]
    public void NestedList_RepetitionAndDefinitionLevels()
    {
        // List<int> required list → repeated element
        // MaxRepetitionLevel = 1 (list element repeated), MaxDefinitionLevel = 1
        var schema = new ParquetSchema([ColumnDef.List("nums",
            ColumnDef.RequiredLeaf("element", ParquetPhysicalType.Int32))]);
        Assert.True(schema.Columns.Length > 0);
        var info = schema.LeafProjectionInfos[0];
        Assert.True(info.MaxRepetitionLevel >= 1); // repeated element
        Assert.True(info.IsList);
    }

    [Fact]
    public void OptionalList_DefinitionLevelsHigher()
    {
        // Optional List<int> → one more definition level for the list itself
        var schema = new ParquetSchema([ColumnDef.List("nums",
            ColumnDef.RequiredLeaf("element", ParquetPhysicalType.Int32),
            repetition: ParquetRepetition.Optional)]);
        var info = schema.LeafProjectionInfos[0];
        Assert.True(info.MaxDefinitionLevel >= 2); // optional list + repeated element
    }

    [Fact]
    public void NestedList_OptionalElement_HigherDefinition()
    {
        // List<int?> — optional element → higher definition level
        var schema = new ParquetSchema([ColumnDef.List("nums",
            ColumnDef.OptionalLeaf("element", ParquetPhysicalType.Int32))]);
        var info = schema.LeafProjectionInfos[0];
        Assert.True(info.ElementOptional);
        Assert.True(info.MaxDefinitionLevel >= 2);
    }

    // ──────────────── ColumnDefinition-based schema construction ────────────────

    [Fact]
    public void ColumnDefinition_RequiredGroup_TwoChildren()
    {
        var schema = new ParquetSchema([ColumnDef.RequiredGroup("grp",
            ColumnDef.RequiredLeaf("x", ParquetPhysicalType.Int32),
            ColumnDef.RequiredLeaf("y", ParquetPhysicalType.Int32))]);
        Assert.Equal(2, schema.Columns.Length);
        Assert.Equal("grp.x", schema.Columns[0].Name);
        Assert.Equal("grp.y", schema.Columns[1].Name);
    }

    [Fact]
    public void Schema_FromDefinitions_MatchesColumns()
    {
        // Schema constructed from Column[] and from ColumnDefinition[] should give same result
        var colSchema = new ParquetSchema([new Column("v", ParquetPhysicalType.Int32)]);
        var defSchema = new ParquetSchema([ColumnDef.RequiredLeaf("v", ParquetPhysicalType.Int32)]);
        Assert.Equal(colSchema.Columns.Length, defSchema.Columns.Length);
        Assert.Equal(colSchema.Columns[0].Name, defSchema.Columns[0].Name);
        Assert.Equal(colSchema.Columns[0].PhysicalType, defSchema.Columns[0].PhysicalType);
    }

    // ──────────────── Invalid column definition (TryProjectLeafColumns returns false) ────────────────

    [Fact]
    public void ColumnDefinition_NullPhysicalType_EmptyColumns()
    {
        // Leaf with no PhysicalType → TryCollectLeaves returns false → empty columns
        var def = new ColumnDefinition { Name = "v", Kind = NodeKind.Leaf, Repetition = ParquetRepetition.Required, PhysicalType = null };
        var schema = new ParquetSchema(ImmutableArray.Create(def));
        Assert.Empty(schema.Columns);
    }

    [Fact]
    public void ColumnDefinition_GroupNoChildren_EmptyColumns()
    {
        // Group with no children → TryCollectLeaves returns false
        var def = new ColumnDefinition { Name = "grp", Kind = NodeKind.Group, Repetition = ParquetRepetition.Required };
        var schema = new ParquetSchema(ImmutableArray.Create(def));
        Assert.Empty(schema.Columns);
    }
}
