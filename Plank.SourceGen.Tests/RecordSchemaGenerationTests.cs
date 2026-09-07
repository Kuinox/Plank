using Microsoft.CodeAnalysis;

namespace Plank.SourceGen.Tests;

internal sealed class RecordSchemaGenerationTests
{
    [Test]
    [Arguments("public sealed partial record Data", "public int Value { get; init; }")]
    [Arguments("public sealed partial record class Data", "public int Value { get; init; }")]
    [Arguments("public sealed partial record Data(int Value)", "")]
    public async Task RecordClassSchemasGenerateCompilableApis(string declaration, string members)
    {
        var source = $$"""
            using Plank.Schema;

            namespace Regression;

            [ParquetSchema]
            {{declaration}}
            {
                {{members}}
            }
            """;

        var result = GeneratorTestHarness.Run(new GeneratorTestHarness.SourceFile("RecordSchema.cs", source));

        await Assert.That(result.GeneratorDiagnostics).IsEmpty();
        await Assert.That(result.GeneratorExceptions).IsEmpty();
        await Assert.That(result.GeneratedSources).HasSingleItem();
        await Assert.That(result.GeneratedSources.Single().Text).Contains("partial record class Data");
        await Assert.That(result.CompilationDiagnostics.Where(static diagnostic =>
            diagnostic.Severity == DiagnosticSeverity.Error)).IsEmpty();
    }

    [Test]
    public async Task RecordClassSchemasWithNestedValuesGenerateCompilableApis()
    {
        const string source = """
            using Plank.Schema;

            namespace Regression;

            public struct Position
            {
                public int X { get; init; }
                public int? Y { get; init; }
            }

            [ParquetSchema(AllowAllocatingValues = true)]
            public sealed partial record Data
            {
                public Position Position { get; init; }
                public int[] Values { get; init; } = [];
            }
            """;

        var result = GeneratorTestHarness.Run(new GeneratorTestHarness.SourceFile("NestedRecordSchema.cs", source));

        await Assert.That(result.GeneratorDiagnostics).IsEmpty();
        await Assert.That(result.GeneratorExceptions).IsEmpty();
        await Assert.That(result.GeneratedSources).HasSingleItem();
        await Assert.That(result.GeneratedSources.Single().Text).Contains("partial record class Data");
        await Assert.That(result.CompilationDiagnostics.Where(static diagnostic =>
            diagnostic.Severity == DiagnosticSeverity.Error)).IsEmpty();
    }
}
