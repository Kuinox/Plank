using Microsoft.CodeAnalysis;

namespace Plank.SourceGen.Tests;

internal sealed class KeywordPropertyIdentifierTests
{
    [Test]
    public async Task EscapedKeywordPropertyRemainsValidGeneratedSource()
    {
        const string source = """
            using Plank.Schema;

            namespace Regression;

            [ParquetSchema]
            partial class KeywordPropertySchema
            {
                public int @event { get; set; }
            }
            """;

        var result = GeneratorTestHarness.Run(
            new GeneratorTestHarness.SourceFile("KeywordPropertySchema.cs", source));
        var errors = result.CompilationDiagnostics
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .Select(diagnostic => diagnostic.ToString())
            .ToArray();

        await Assert.That(errors).IsEmpty();
    }
}
