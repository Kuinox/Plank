namespace Plank.SourceGen.Tests;

internal sealed class GeneratedRowReaderAccessTests
{
    [Test]
    public async Task FlatReadPropertiesUseValidatedSchemaOrdinals()
    {
        const string source = """
            using Plank.Schema;

            namespace Regression;

            [ParquetSchema]
            partial class RowSchema
            {
                public int Value { get; set; }

                public long? Optional { get; set; }
            }
            """;

        var result = GeneratorTestHarness.Run(
            new GeneratorTestHarness.SourceFile("RowSchema.cs", source));

        await Assert.That(result.GeneratorExceptions).IsEmpty();
        await Assert.That(result.CompilationDiagnostics.Where(static diagnostic =>
            diagnostic.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error)).IsEmpty();
        var generated = result.GeneratedSources.Single().Text;
        await Assert.That(generated).Contains("_core.GetCurrent<int>(0)");
        await Assert.That(generated).Contains("_core.GetCurrent<long?>(1)");
    }
}
