namespace Plank.SourceGen.Tests;

internal sealed class BloomFilterAttributeTests
{
    [Test]
    public async Task ColumnAttributeEmitsBloomFilterConfiguration()
    {
        const string source = """
            using Plank.Schema;

            namespace Regression;

            [ParquetSchema]
            partial class BloomSchema
            {
                [ParquetColumn(BloomFilter = true, BloomFilterFalsePositiveProbability = 0.001,
                    BloomFilterExpectedDistinctValueCount = 123, BloomFilterMaximumBytes = 4096)]
                public int Value { get; set; }
            }
            """;

        var result = GeneratorTestHarness.Run(new GeneratorTestHarness.SourceFile("BloomSchema.cs", source));
        var generated = string.Join('\n', result.GeneratedSources.Select(static generated => generated.Text));

        await Assert.That(result.GeneratorExceptions).IsEmpty();
        await Assert.That(result.CompilationDiagnostics.Where(static diagnostic =>
            diagnostic.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error)).IsEmpty();
        await Assert.That(generated).Contains("new global::Plank.Schema.ParquetBloomFilterOptions");
        await Assert.That(generated).Contains("FalsePositiveProbability = 0.001");
        await Assert.That(generated).Contains("ExpectedDistinctValueCount = 123");
        await Assert.That(generated).Contains("MaximumBytes = 4096");
    }
}
