using Microsoft.CodeAnalysis;

namespace Plank.SourceGen.Tests;

internal sealed class InheritedDtoDiagnosticTests
{
    [Test]
    [Arguments("", "public int Value { get; set; }")]
    [Arguments("public int Id { get; set; }", "")]
    [Arguments("public int Id { get; set; }", "public int Value { get; set; }")]
    [Arguments("public int Id { get; set; }", "public int[] Values { get; set; } = [];")]
    [Arguments("public virtual int Id { get; set; }", "public override int Id { get; set; }")]
    [Arguments("public int Id { get; set; }", "public new long Id { get; set; }")]
    public async Task RootSchemasRejectCustomBaseClasses(string baseMembers, string members)
    {
        var source = $$"""
            using Plank.Schema;

            class Base { {{baseMembers}} }
            [ParquetSchema(AllowAllocatingValues = true)]
            partial class Schema : Base { {{members}} }
            """;

        var result = GeneratorTestHarness.Run(new GeneratorTestHarness.SourceFile("Schema.cs", source));

        await AssertInheritanceDiagnostic(result, "Schema", "Base").ConfigureAwait(false);
    }

    [Test]
    [Arguments("Derived")]
    [Arguments("Derived?")]
    [Arguments("Derived[]")]
    [Arguments("List<Derived>")]
    [Arguments("List<Derived[]>")]
    [Arguments("Dictionary<int, Derived>")]
    [Arguments("Dictionary<Derived, int>")]
    public async Task NestedDtosRejectCustomBaseClasses(string propertyType)
    {
        var source = $$"""
            using System.Collections.Generic;
            using Plank.Schema;

            [ParquetSchema(AllowAllocatingValues = true)]
            partial class Schema
            {
                public {{propertyType}} Details { get; set; } = default!;
            }
            class Base { public int Id { get; set; } }
            class Derived : Base { public int Value { get; set; } }
            """;

        var result = GeneratorTestHarness.Run(new GeneratorTestHarness.SourceFile("Schema.cs", source));

        await AssertInheritanceDiagnostic(result, "Derived", "Base").ConfigureAwait(false);
    }

    [Test]
    [Arguments("class", "", "")]
    [Arguments("class", "public int Id { get; set; }", "")]
    [Arguments("record", "public int Id { get; set; }", "public int Value { get; set; }")]
    public async Task DeeplyNestedDtosRejectInheritance(string kind, string baseMembers, string members)
    {
        var source = $$"""
            using Plank.Schema;

            [ParquetSchema(AllowAllocatingValues = true)]
            partial class Schema { public Container Details { get; set; } = new(); }
            struct Container { public Derived Item { get; set; } }
            {{kind}} Base { {{baseMembers}} }
            {{kind}} Derived : Base { {{members}} }
            """;

        var result = GeneratorTestHarness.Run(new GeneratorTestHarness.SourceFile("Schema.cs", source));

        await AssertInheritanceDiagnostic(result, "Derived", "Base").ConfigureAwait(false);
    }

    [Test]
    [Arguments("class")]
    [Arguments("struct")]
    [Arguments("record")]
    [Arguments("record struct")]
    public async Task PlainDtosAndInterfacesRemainSupported(string nestedKind)
    {
        var source = $$"""
            using System.Collections.Generic;
            using Plank.Schema;

            interface IValue { int Value { get; set; } }
            [ParquetSchema(AllowAllocatingValues = true)]
            partial class Schema : object, IValue
            {
                public int Value { get; set; }
                public Details Item { get; set; } = new();
                public Details[] Items { get; set; } = [];
                public List<Details> MoreItems { get; set; } = [];
            }
            {{nestedKind}} Details : IValue { public int Value { get; set; } }
            """;

        var result = GeneratorTestHarness.Run(new GeneratorTestHarness.SourceFile("Schema.cs", source));

        await Assert.That(result.GeneratorExceptions).IsEmpty();
        await Assert.That(result.GeneratorDiagnostics).IsEmpty();
        await Assert.That(result.CompilationDiagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)).IsEmpty();
        await Assert.That(result.GeneratedSources).HasSingleItem();
    }

    static async Task AssertInheritanceDiagnostic(GeneratorTestHarness.RunResult result, string type, string baseType)
    {
        await Assert.That(result.GeneratorExceptions).IsEmpty();
        await Assert.That(result.CompilationDiagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)).IsEmpty();
        await Assert.That(result.GeneratorDiagnostics).HasSingleItem();
        await Assert.That(result.GeneratorDiagnostics[0].Id).IsEqualTo("PLANKGEN003");
        await Assert.That(result.GeneratorDiagnostics[0].Severity).IsEqualTo(DiagnosticSeverity.Error);
        await Assert.That(result.GeneratorDiagnostics[0].GetMessage(System.Globalization.CultureInfo.InvariantCulture)
            .Contains($"'{type}' inherits from '{baseType}'", StringComparison.Ordinal)).IsTrue();
        await Assert.That(result.GeneratedSources).IsEmpty();
    }
}
