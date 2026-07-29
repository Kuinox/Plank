using Microsoft.CodeAnalysis;

namespace Plank.SourceGen.Tests;

internal sealed class KeywordPropertyIdentifierTests
{
    [Test]
    public async Task EscapedKeywordSchemaAndPropertyRemainValidGeneratedSource()
    {
        const string source = """
            using Plank.Schema;

            namespace Regression;

            [ParquetSchema]
            partial class @class
            {
                public int @event { get; set; }
            }
            """;

        var result = GeneratorTestHarness.Run(
            new GeneratorTestHarness.SourceFile("KeywordIdentifiers.cs", source));
        var errors = result.CompilationDiagnostics
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .Select(diagnostic => diagnostic.ToString())
            .ToArray();

        await Assert.That(result.GeneratorExceptions).IsEmpty();
        await Assert.That(errors).IsEmpty();
    }
}
