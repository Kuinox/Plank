namespace Plank.SourceGen.Tests;

internal sealed class HintNameCollisionTests
{
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
            """;

        var result = GeneratorTestHarness.Run(
            new GeneratorTestHarness.SourceFile("DuplicateSchemas.cs", source));

        await Assert.That(result.GeneratorExceptions).IsEmpty();
        await Assert.That(result.GeneratedSources.Length).IsEqualTo(2);
        await Assert.That(result.GeneratedSources.Select(generated => generated.HintName).Distinct().Count())
            .IsEqualTo(2);
    }
}
