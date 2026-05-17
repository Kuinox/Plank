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

    [Test]
    public void Column_UnspecifiedRepetition_NormalizedToRequired()
    {
        // Column with Unspecified repetition should be normalized to Required in schema
        var col = new Column("v", ParquetPhysicalType.Int32,
            new ColumnOptions(ParquetRepetition.Unspecified));
        var schema = new ParquetSchema([col]);
        // Verify that the definition in the schema has Required repetition
        ClassicAssert.AreEqual(1, schema.Definitions.Length);
        ClassicAssert.AreEqual(ParquetRepetition.Required, schema.Definitions[0].Repetition);
    }

    [Test]
    public void Column_OptionalRepetition_PreservedInDefinition()
    {
        var col = new Column("v", ParquetPhysicalType.Int32,
            new ColumnOptions(ParquetRepetition.Optional));
        var schema = new ParquetSchema([col]);
        ClassicAssert.AreEqual(ParquetRepetition.Optional, schema.Definitions[0].Repetition);
    }

    [Test]
    public void Column_RequiredRepetition_PreservedInDefinition()
    {
        var col = new Column("v", ParquetPhysicalType.Int32,
            new ColumnOptions(ParquetRepetition.Required));
        var schema = new ParquetSchema([col]);
        ClassicAssert.AreEqual(ParquetRepetition.Required, schema.Definitions[0].Repetition);
    }

    // ──────────────── Leaf paths (line 100) ────────────────

    [Test]
    public void Schema_TwoColumns_TwoLeafPaths()
    {
        var schema = new ParquetSchema([
            new Column("first", ParquetPhysicalType.Int32),
            new Column("second", ParquetPhysicalType.Float)
        ]);
        ClassicAssert.AreEqual(2, schema.LeafPaths.Length);
        ClassicAssert.AreEqual("first", schema.LeafPaths[0][0]);
        ClassicAssert.AreEqual("second", schema.LeafPaths[1][0]);
    }

    [Test]
    public void Schema_SingleColumn_SingleLeafPath()
    {
        var schema = new ParquetSchema([
            new Column("my_col", ParquetPhysicalType.Double)
        ]);
        ClassicAssert.AreEqual(1, schema.LeafPaths.Length);
        ClassicAssert.AreEqual("my_col", schema.LeafPaths[0][0]);
    }

    [Test]
    public void Schema_Empty_NoLeafPaths()
    {
        var schema = new ParquetSchema(ImmutableArray<Column>.Empty);
        ClassicAssert.IsEmpty(schema.LeafPaths);
    }

    // ──────────────── LeafProjectionInfo construction (line 122) ────────────────

    [Test]
    public void Schema_RequiredColumn_MaxLevelsZero()
    {
        var schema = new ParquetSchema([new Column("v", ParquetPhysicalType.Int32)]);
        ClassicAssert.AreEqual(1, schema.LeafProjectionInfos.Length);
        var info = schema.LeafProjectionInfos[0];
        ClassicAssert.AreEqual(0, info.MaxRepetitionLevel);
        ClassicAssert.AreEqual(0, info.MaxDefinitionLevel);
        ClassicAssert.IsFalse(info.IsList);
    }

    [Test]
    public void Schema_OptionalColumn_MaxDefinitionLevelOne()
    {
        var schema = new ParquetSchema([new Column("v", ParquetPhysicalType.Int32,
            new ColumnOptions(ParquetRepetition.Optional))]);
        var info = schema.LeafProjectionInfos[0];
        ClassicAssert.AreEqual(0, info.MaxRepetitionLevel);
        ClassicAssert.AreEqual(1, info.MaxDefinitionLevel); // optional → definition level 1
        ClassicAssert.IsFalse(info.IsList);
    }

    // ──────────────── Nested schema via ColumnDefinition (lines 144-165) ────────────────

    [Test]
    public void NestedList_RepetitionAndDefinitionLevels()
    {
        // List<int> required list → repeated element
        // MaxRepetitionLevel = 1 (list element repeated), MaxDefinitionLevel = 1
        var schema = new ParquetSchema([ColumnDef.List("nums",
            ColumnDef.RequiredLeaf("element", ParquetPhysicalType.Int32))]);
        ClassicAssert.IsTrue(schema.Columns.Length > 0);
        var info = schema.LeafProjectionInfos[0];
        ClassicAssert.IsTrue(info.MaxRepetitionLevel >= 1); // repeated element
        ClassicAssert.IsTrue(info.IsList);
    }

    [Test]
    public void OptionalList_DefinitionLevelsHigher()
    {
        // Optional List<int> → one more definition level for the list itself
        var schema = new ParquetSchema([ColumnDef.List("nums",
            ColumnDef.RequiredLeaf("element", ParquetPhysicalType.Int32),
            repetition: ParquetRepetition.Optional)]);
        var info = schema.LeafProjectionInfos[0];
        ClassicAssert.IsTrue(info.MaxDefinitionLevel >= 2); // optional list + repeated element
    }

    [Test]
    public void NestedList_OptionalElement_HigherDefinition()
    {
        // List<int?> — optional element → higher definition level
        var schema = new ParquetSchema([ColumnDef.List("nums",
            ColumnDef.OptionalLeaf("element", ParquetPhysicalType.Int32))]);
        var info = schema.LeafProjectionInfos[0];
        ClassicAssert.IsTrue(info.ElementOptional);
        ClassicAssert.IsTrue(info.MaxDefinitionLevel >= 2);
    }

    // ──────────────── ColumnDefinition-based schema construction ────────────────

    [Test]
    public void ColumnDefinition_RequiredGroup_TwoChildren()
    {
        var schema = new ParquetSchema([ColumnDef.RequiredGroup("grp",
            ColumnDef.RequiredLeaf("x", ParquetPhysicalType.Int32),
            ColumnDef.RequiredLeaf("y", ParquetPhysicalType.Int32))]);
        ClassicAssert.AreEqual(2, schema.Columns.Length);
        ClassicAssert.AreEqual("grp.x", schema.Columns[0].Name);
        ClassicAssert.AreEqual("grp.y", schema.Columns[1].Name);
    }

    [Test]
    public void Schema_FromDefinitions_MatchesColumns()
    {
        // Schema constructed from Column[] and from ColumnDefinition[] should give same result
        var colSchema = new ParquetSchema([new Column("v", ParquetPhysicalType.Int32)]);
        var defSchema = new ParquetSchema([ColumnDef.RequiredLeaf("v", ParquetPhysicalType.Int32)]);
        ClassicAssert.AreEqual(colSchema.Columns.Length, defSchema.Columns.Length);
        ClassicAssert.AreEqual(colSchema.Columns[0].Name, defSchema.Columns[0].Name);
        ClassicAssert.AreEqual(colSchema.Columns[0].PhysicalType, defSchema.Columns[0].PhysicalType);
    }

    // ──────────────── Invalid column definition (TryProjectLeafColumns returns false) ────────────────

    [Test]
    public void ColumnDefinition_NullPhysicalType_EmptyColumns()
    {
        // Leaf with no PhysicalType → TryCollectLeaves returns false → empty columns
        var def = new ColumnDefinition { Name = "v", Kind = NodeKind.Leaf, Repetition = ParquetRepetition.Required, PhysicalType = null };
        var schema = new ParquetSchema(ImmutableArray.Create(def));
        ClassicAssert.IsEmpty(schema.Columns);
    }

    [Test]
    public void ColumnDefinition_GroupNoChildren_EmptyColumns()
    {
        // Group with no children → TryCollectLeaves returns false
        var def = new ColumnDefinition { Name = "grp", Kind = NodeKind.Group, Repetition = ParquetRepetition.Required };
        var schema = new ParquetSchema(ImmutableArray.Create(def));
        ClassicAssert.IsEmpty(schema.Columns);
    }
}
