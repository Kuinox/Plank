using Microsoft.CodeAnalysis;

namespace Plank.SourceGen.Tests;

internal sealed class UnicodePropertyIdentifierTests
{
    [Test]
    [Arguments("a\u0301")]
    [Arguments("a\u203f")]
    public async Task ScalarPropertyIdentifiersRemainIntact(string identifier)
    {
        var source = $$"""
            using Plank.Schema;

            [ParquetSchema]
            public partial class Data
            {
                public int {{identifier}} { get; set; }
                public int a_ { get; set; }
            }
            """;

        var result = GeneratorTestHarness.Run(new GeneratorTestHarness.SourceFile("UnicodeScalar.cs", source));

        await AssertCompiles(result).ConfigureAwait(false);
        await Assert.That(result.GeneratedSources.Single().Text).Contains("row." + identifier);
        await Assert.That(result.GeneratedSources.Single().Text).Contains("row.a_");
    }

    [Test]
    public async Task NestedPropertiesKeepSourceIdentifiersDespiteSanitizedHelperNames()
    {
        const string source = """
            using Plank.Schema;

            [ParquetSchema(AllowAllocatingValues = true)]
            public partial class Data
            {
                public Point po\u0301int { get; set; } = new();
                public Point po_int { get; set; } = new();
            }

            public class Point
            {
                public int a\u0301 { get; set; }
                public int a_ { get; set; }
            }
            """;

        var result = GeneratorTestHarness.Run(new GeneratorTestHarness.SourceFile("UnicodeNested.cs", source));

        await AssertCompiles(result).ConfigureAwait(false);
        await Assert.That(result.GeneratedSources.Single().Text).Contains("row.po\u0301int");
        await Assert.That(result.GeneratedSources.Single().Text).Contains(".a\u0301");
    }

    static async Task AssertCompiles(GeneratorTestHarness.RunResult result)
    {
        await Assert.That(result.GeneratorExceptions).IsEmpty();
        await Assert.That(result.GeneratorDiagnostics).IsEmpty();
        await Assert.That(result.GeneratedSources).HasSingleItem();
        await Assert.That(result.CompilationDiagnostics.Where(static diagnostic =>
            diagnostic.Severity == DiagnosticSeverity.Error)).IsEmpty();
    }
}
