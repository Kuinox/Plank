namespace Plank.SourceGen.Tests;

internal sealed class HintNameCollisionTests
{
    [Test]
    public async Task NestedSchemasWithUnderscoresInDifferentSegmentsUseDistinctHints()
    {
        const string source = """
            using Plank.Schema;
            namespace A_B
            {
                [ParquetSchema(AllowAllocatingValues = true)]
                partial class C { public int[] First { get; set; } = []; }
            }
            namespace A
            {
                [ParquetSchema(AllowAllocatingValues = true)]
                partial class B_C { public int[] Second { get; set; } = []; }
            }
            """;

        var result = GeneratorTestHarness.Run(new GeneratorTestHarness.SourceFile("NestedSchemas.cs", source));
        await Assert.That(result.GeneratorExceptions).IsEmpty();
        await Assert.That(result.GeneratorDiagnostics).IsEmpty();
        await Assert.That(result.GeneratedSources.Length).IsEqualTo(2);
        await Assert.That(result.GeneratedSources.Select(generated => generated.HintName).Distinct().Count())
            .IsEqualTo(2);
        await Assert.That(result.CompilationDiagnostics.Where(static diagnostic =>
            diagnostic.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error)).IsEmpty();
    }

    [Test]
    public async Task SameSimpleSchemaNameInDifferentNamespacesUsesDistinctHints()
    {
        const string source = """
            using Plank.Schema;

            namespace Alpha
            {
                [ParquetSchema]
                partial class Duplicate
                {
                    public int AlphaValue { get; set; }
                }
            }

            namespace Beta
            {
                [ParquetSchema]
                partial class Duplicate
                {
                    public int BetaValue { get; set; }
                }
            }

            namespace AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA
            {
                [ParquetSchema]
                partial class LongNamespaceSchema
                {
                    public int Value { get; set; }
                }
            }
            """;

        var result = GeneratorTestHarness.Run(
            new GeneratorTestHarness.SourceFile("DuplicateSchemas.cs", source));

        await Assert.That(result.GeneratorExceptions).IsEmpty();
        await Assert.That(result.GeneratedSources.Length).IsEqualTo(3);
        await Assert.That(result.GeneratedSources.Select(generated => generated.HintName).Distinct().Count())
            .IsEqualTo(3);
        await Assert.That(result.GeneratedSources.All(generated => generated.HintName.Length <= 128)).IsTrue();
    }
}
