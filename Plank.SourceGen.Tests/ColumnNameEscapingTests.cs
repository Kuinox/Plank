using Microsoft.CodeAnalysis;

namespace Plank.SourceGen.Tests;

internal sealed class ColumnNameEscapingTests
{
    [Test]
    public async Task ControlCharactersInColumnNameRemainValidGeneratedSource()
    {
        const string source = """
            using Plank.Schema;

            namespace Regression;

            [ParquetSchema]
            partial class EscapedColumnNameSchema
            {
                [ParquetColumn("first\r\nsecond\t\u0001")]
                public int Value { get; set; }
            }
            """;

        var result = GeneratorTestHarness.Run(
            new GeneratorTestHarness.SourceFile("EscapedColumnNameSchema.cs", source));
        var errors = result.CompilationDiagnostics
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .Select(diagnostic => diagnostic.ToString())
            .ToArray();

        await Assert.That(errors).IsEmpty();
    }
}
