namespace Plank.SourceGen.Tests;

internal sealed class GeneratedRowWriterAccessTests
{
    [Test]
    public async Task FlatRowsUseUncheckedArrayDataReferences()
    {
        const string source = """
            using Plank.Schema;

            namespace Regression;

            [ParquetSchema]
            partial class RowSchema
            {
                public int Value { get; set; }
                public long Count { get; set; }
            }
            """;

        var generated = Generate(source);

        await Assert.That(generated).Contains("internal int[] _column0 = null!;");
        await Assert.That(generated).Contains(
            "=> ref global::System.Runtime.CompilerServices.Unsafe.Add(ref GetArrayDataReferenceUnchecked(ref _column0), index);");
        await Assert.That(generated).Contains("return new Row(Index, this);");
        await Assert.That(generated).Contains("public ref int Value => ref _ownerSlot.GetColumn0(_index);");
        await Assert.That(generated.Contains("_ownerSlot._column0[_index]", StringComparison.Ordinal)).IsFalse();
        await Assert.That(generated.Contains("return new Row(Index, this, GetValues", StringComparison.Ordinal))
            .IsFalse();
    }

    [Test]
    public async Task NullableFlatRowsUseUncheckedArrayDataReferences()
    {
        const string source = """
            using Plank.Schema;

            namespace Regression;

            [ParquetSchema]
            partial class RowSchema
            {
                public int? Value { get; set; }
            }
            """;

        var generated = Generate(source);

        await Assert.That(generated).Contains("internal int?[] _column0 = null!;");
        await Assert.That(generated).Contains("GetArrayDataReferenceUnchecked(ref _column0)");
        await Assert.That(generated).Contains("public ref int? Value => ref _ownerSlot.GetColumn0(_index);");
        await Assert.That(generated.Contains("PinnedColumn", StringComparison.Ordinal)).IsFalse();
    }

    [Test]
    public async Task NestedScalarLeavesUseUncheckedArrayDataReferences()
    {
        const string source = """
            using Plank.Schema;

            namespace Regression;

            [ParquetSchema(AllowAllocatingValues = true)]
            partial class RowSchema
            {
                public Details Value { get; set; } = new();
            }

            class Details
            {
                public int Count { get; set; }
                public string Name { get; set; } = "";
            }
            """;

        var generated = Generate(source);

        await Assert.That(generated).Contains("internal int[] _column0 = null!;");
        await Assert.That(generated).Contains("GetArrayDataReferenceUnchecked(ref _column0)");
        await Assert.That(generated).Contains("_ownerSlot.GetColumn0(_index)");
        await Assert.That(generated).Contains("_column1 = GetValues<string>(1);");
        await Assert.That(generated).Contains("GetArrayDataReferenceUnchecked(ref _column1)");
        await Assert.That(generated.Contains("PinnedColumn", StringComparison.Ordinal)).IsFalse();
    }

    [Test]
    public async Task NestedRowsReferenceCachedTypedBuffers()
    {
        const string source = """
            using Plank.Schema;

            namespace Regression;

            [ParquetSchema(AllowAllocatingValues = true)]
            partial class RowSchema
            {
                public int[] Values { get; set; } = [];
            }
            """;

        var generated = Generate(source);

        await Assert.That(generated).Contains("_column0 = GetValues<");
        await Assert.That(generated).Contains("return new Row(Index, this);");
        await Assert.That(generated).Contains("GetArrayDataReferenceUnchecked(ref _column0)");
        await Assert.That(generated).Contains("_ownerSlot.GetColumn0(_index)");
        await Assert.That(generated.Contains("PinnedColumn", StringComparison.Ordinal)).IsFalse();
        await Assert.That(generated.Contains("_ownerSlot._column0[_index]", StringComparison.Ordinal)).IsFalse();
        await Assert.That(generated.Contains("return new Row(Index, this, GetValues", StringComparison.Ordinal))
            .IsFalse();
    }

    [Test]
    public async Task FixedRowsGenerateOnlyTheRowCountCutoff()
    {
        const string source = """
            using Plank.Schema;

            namespace Regression;

            [ParquetSchema]
            partial class RowSchema
            {
                public int Value { get; set; }
                public long Count { get; set; }
            }
            """;

        var generated = Generate(source);

        await Assert.That(generated).Contains("readonly int _rowsPerGroup;");
        await Assert.That(generated).Contains("GetFixedRowsPerGroup(checked(4UL + 8UL))");
        await Assert.That(generated).Contains("slot = CommitFixedRow(slot, _rowsPerGroup);");
        await Assert.That(generated).Contains("bool _rowPending;");
        await Assert.That(generated.Contains("public void Next()", StringComparison.Ordinal)).IsFalse();
        await Assert.That(generated.Contains("GetRowSize(ulong", StringComparison.Ordinal)).IsFalse();
        await Assert.That(generated.Contains("EstimateValueSize(", StringComparison.Ordinal)).IsFalse();
    }

    [Test]
    public async Task MixedRowsGenerateDirectSizeReadsForOnlyVariableColumns()
    {
        const string source = """
            using System;
            using Plank.Schema;

            namespace Regression;

            [ParquetSchema]
            partial class RowSchema
            {
                public int Value { get; set; }
                public ReadOnlyMemory<byte> Payload { get; set; }
            }
            """;

        var generated = Generate(source);

        await Assert.That(generated.Contains("readonly int _rowsPerGroup;", StringComparison.Ordinal)).IsFalse();
        await Assert.That(generated).Contains(
            "slot = CommitVariableRow(slot, slot.GetRowSize(checked(4UL)));");
        await Assert.That(generated).Contains("bool _rowPending;");
        await Assert.That(generated.Contains("public void Next()", StringComparison.Ordinal)).IsFalse();
        await Assert.That(generated).Contains(
            "EstimateValueSize(GetColumn1(Index), global::Plank.Schema.ParquetPhysicalType.ByteArray, 0U)");
        await Assert.That(generated.Contains(
            "global::System.Runtime.InteropServices.MemoryMarshal.GetArrayDataReference(",
            StringComparison.Ordinal)).IsFalse();
    }

    static string Generate(string source)
    {
        var result = GeneratorTestHarness.Run(new GeneratorTestHarness.SourceFile("RowSchema.cs", source));
        var errors = result.CompilationDiagnostics
            .Where(static diagnostic => diagnostic.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
            .Select(static diagnostic => diagnostic.ToString())
            .ToArray();
        if (errors.Length != 0)
            throw new InvalidOperationException(string.Join(Environment.NewLine, errors));
        return result.GeneratedSources.Single().Text;
    }
}
