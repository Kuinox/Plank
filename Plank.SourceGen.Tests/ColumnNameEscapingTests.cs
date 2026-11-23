using Microsoft.CodeAnalysis;

namespace Plank.SourceGen.Tests;

internal sealed class ColumnNameEscapingTests
{
    [Test]
    [Arguments("""first\r\nsecond\t\u0001""", "int")]
    [Arguments("""quote\"and\\backslash""", "int")]
    [Arguments("""quote\"and\\backslash""", "int[]")]
    public async Task ColumnNameRemainsValidGeneratedSource(string name, string type)
    {
        var source = $$"""
            using Plank.Schema;

            namespace Regression;

            [ParquetSchema(AllowAllocatingValues = true)]
            partial class EscapedColumnNameSchema
            {
                [ParquetColumn("{{name}}")]
                public {{type}} Value { get; set; }
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
