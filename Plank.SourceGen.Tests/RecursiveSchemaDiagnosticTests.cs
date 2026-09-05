using Microsoft.CodeAnalysis;

namespace Plank.SourceGen.Tests;

internal sealed class RecursiveSchemaDiagnosticTests
{
    [Test]
    [Arguments("Schema?")]
    [Arguments("Schema[]")]
    [Arguments("List<Schema>")]
    [Arguments("Dictionary<int, Schema>")]
    [Arguments("Dictionary<Schema, int>")]
    public async Task RecursiveSchemasReportDiagnosticWithoutCrashing(string recursiveType)
    {
        var source = $$"""
            using System.Collections.Generic;
            using Plank.Schema;

            [ParquetSchema(AllowAllocatingValues = true)]
            partial class Schema
            {
                public int Value { get; set; }
                public {{recursiveType}} Parent { get; set; } = default!;
            }
            """;

        var result = GeneratorTestHarness.Run(new GeneratorTestHarness.SourceFile("Schema.cs", source));

        await AssertRecursiveDiagnostic(result).ConfigureAwait(false);
    }

    [Test]
    public async Task MutuallyRecursiveGroupsReportDiagnosticWithoutCrashing()
    {
        const string source = """
            using Plank.Schema;

            [ParquetSchema(AllowAllocatingValues = true)]
            partial class Schema
            {
                public First Root { get; set; } = new();
            }
            class First
            {
                public Second Next { get; set; } = new();
            }
            class Second
            {
                public First? Previous { get; set; }
            }
            """;

        var result = GeneratorTestHarness.Run(new GeneratorTestHarness.SourceFile("Schema.cs", source));

        await AssertRecursiveDiagnostic(result).ConfigureAwait(false);
    }

    [Test]
    public async Task SiblingGroupsMayReuseTheSameType()
    {
        const string source = """
            using Plank.Schema;

            [ParquetSchema(AllowAllocatingValues = true)]
            partial class Schema
            {
                public Address Billing { get; set; } = new();
                public Address Shipping { get; set; } = new();
            }
            class Address
            {
                public int Zip { get; set; }
            }
            """;

        var result = GeneratorTestHarness.Run(new GeneratorTestHarness.SourceFile("Schema.cs", source));

        await Assert.That(result.GeneratorExceptions).IsEmpty();
        await Assert.That(result.GeneratorDiagnostics).IsEmpty();
        await Assert.That(result.CompilationDiagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)).IsEmpty();
        await Assert.That(result.GeneratedSources).HasSingleItem();
    }

    static async Task AssertRecursiveDiagnostic(GeneratorTestHarness.RunResult result)
    {
        await Assert.That(result.GeneratorExceptions).IsEmpty();
        await Assert.That(result.GeneratorDiagnostics).HasSingleItem();
        await Assert.That(result.GeneratorDiagnostics[0].Id).IsEqualTo("PLANKGEN003");
        await Assert.That(result.GeneratorDiagnostics[0].Severity).IsEqualTo(DiagnosticSeverity.Error);
        await Assert.That(result.GeneratorDiagnostics[0].GetMessage(System.Globalization.CultureInfo.InvariantCulture)
            .Contains("recursive", StringComparison.OrdinalIgnoreCase)).IsTrue();
        await Assert.That(result.GeneratedSources).IsEmpty();
    }
}
