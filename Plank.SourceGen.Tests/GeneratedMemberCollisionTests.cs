using Microsoft.CodeAnalysis;

namespace Plank.SourceGen.Tests;

internal sealed class GeneratedMemberCollisionTests
{
    [Test]
    public async Task SchemaPropertiesMayUseGeneratedApiMemberNames()
    {
        const string source = """
            using Plank.Schema;

            namespace Regression;

            [ParquetSchema]
            partial class GeneratedMemberCollisionSchema
            {
                public int Schema { get; set; }

                public int Writer { get; set; }

                public int Reader { get; set; }
            }
            """;

        var result = GeneratorTestHarness.Run(
            new GeneratorTestHarness.SourceFile("GeneratedMemberCollisionSchema.cs", source));
        var errors = result.CompilationDiagnostics
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .Select(diagnostic => diagnostic.ToString())
            .ToArray();

        await Assert.That(errors).IsEmpty();
    }
}
