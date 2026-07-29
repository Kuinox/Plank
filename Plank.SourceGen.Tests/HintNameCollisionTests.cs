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
