using Microsoft.CodeAnalysis;

namespace Plank.SourceGen.Tests;

internal sealed class NullableStructGroupGenerationTests
{
    [Test]
    public async Task NullableStructGroupsGenerateCompilableRowApis()
    {
        const string source = """
            using Plank.Schema;

            [ParquetSchema(AllowAllocatingValues = true)]
            partial class StructSchema
            {
                public Point? Position { get; set; }
                public Container Details { get; set; }
            }

            struct Point
            {
                [ParquetColumn("x_coordinate")]
                public int X { get; set; }
                public int Y { get; init; }
            }

            struct Container
            {
                public Point? Position { get; set; }
                public int Id { get; set; }
            }
            """;

        var result = GeneratorTestHarness.Run(new GeneratorTestHarness.SourceFile("StructSchema.cs", source));

        await Assert.That(result.GeneratorExceptions).IsEmpty();
        await Assert.That(result.GeneratorDiagnostics.Where(static diagnostic =>
            diagnostic.Severity == DiagnosticSeverity.Error)).IsEmpty();
        await Assert.That(result.CompilationDiagnostics.Where(static diagnostic =>
            diagnostic.Severity == DiagnosticSeverity.Error)).IsEmpty();
        await Assert.That(result.GeneratedSources).HasSingleItem();
    }

    [Test]
    public async Task NullableStructWithOptionalChildReportsUnsupportedOptionalBoundaries()
    {
        const string source = """
            using Plank.Schema;

            [ParquetSchema(AllowAllocatingValues = true)]
            partial class StructSchema
            {
                public Point? Position { get; set; }
            }

            struct Point
            {
                public int X { get; set; }
                public int? Y { get; set; }
            }
            """;

        var result = GeneratorTestHarness.Run(new GeneratorTestHarness.SourceFile("StructSchema.cs", source));

        await Assert.That(result.GeneratorExceptions).IsEmpty();
        await Assert.That(result.GeneratorDiagnostics.Any(static diagnostic =>
            diagnostic.Id == "PLANKGEN003" && diagnostic.Severity == DiagnosticSeverity.Error &&
            diagnostic.GetMessage(System.Globalization.CultureInfo.InvariantCulture)
                .Contains("more than one optional boundary", StringComparison.Ordinal))).IsTrue();
        await Assert.That(result.GeneratedSources).IsEmpty();
    }
}
