using Microsoft.CodeAnalysis;

namespace Plank.SourceGen.Tests;

internal sealed class ContainedSchemaDeclarationTests
{
    [Test]
    [Arguments("public class Outer", "public", "int", "0")]
    [Arguments("public partial class Outer", "private", "int", "0")]
    [Arguments("public partial class Outer<T>", "public", "int", "0")]
    [Arguments("public partial class Outer", "protected", "int[]", "[]")]
    [Arguments("public partial class Outer<T>", "private", "int[]", "[]")]
    public async Task ContainedSchemasReportFocusedDiagnostic(string container, string accessibility,
        string propertyType, string initializer)
    {
        var source = $$"""
            using Plank.Schema;

            namespace Regression;

            {{container}}
            {
                [ParquetSchema(AllowAllocatingValues = true)]
                {{accessibility}} partial class Data
                {
                    public {{propertyType}} Value { get; set; } = {{initializer}};
                }
            }
            """;

        var result = GeneratorTestHarness.Run(new GeneratorTestHarness.SourceFile("ContainedSchema.cs", source));

        await Assert.That(result.GeneratorExceptions).IsEmpty();
        await Assert.That(result.GeneratorDiagnostics).HasSingleItem();
        var diagnostic = result.GeneratorDiagnostics.Single();
        await Assert.That(diagnostic.Id).IsEqualTo("PLANKGEN003");
        await Assert.That(diagnostic.Severity).IsEqualTo(DiagnosticSeverity.Error);
        await Assert.That(diagnostic.GetMessage(System.Globalization.CultureInfo.InvariantCulture))
            .Contains("declared inside another type");
        await Assert.That(result.GeneratedSources).IsEmpty();
        await Assert.That(result.CompilationDiagnostics.Where(static diagnostic =>
            diagnostic.Severity == DiagnosticSeverity.Error)).IsEmpty();
    }

    [Test]
    [Arguments("")]
    [Arguments("namespace Regression;")]
    public async Task TopLevelSchemasStillCompile(string namespaceDeclaration)
    {
        var source = $$"""
            using Plank.Schema;
            {{namespaceDeclaration}}

            [ParquetSchema]
            public partial class Data
            {
                public int Value { get; set; }
            }
            """;

        var result = GeneratorTestHarness.Run(new GeneratorTestHarness.SourceFile("TopLevelSchema.cs", source));

        await Assert.That(result.GeneratorDiagnostics).IsEmpty();
        await Assert.That(result.GeneratorExceptions).IsEmpty();
        await Assert.That(result.GeneratedSources).HasSingleItem();
        await Assert.That(result.CompilationDiagnostics.Where(static diagnostic =>
            diagnostic.Severity == DiagnosticSeverity.Error)).IsEmpty();
    }
}
