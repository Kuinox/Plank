using Microsoft.CodeAnalysis;

namespace Plank.SourceGen.Tests;

internal sealed class InheritedPropertyGenerationTests
{
    [Test]
    public async Task InheritedOnlySchemaReservesGeneratedMemberNames()
    {
        const string source = """
            using Plank.Schema;

            class Base
            {
                public int Schema { get; set; }
                public int All { get; set; }
                public int GetRow { get; set; }
            }
            [ParquetSchema]
            partial class Derived : Base { }
            """;

        var result = GeneratorTestHarness.Run(new GeneratorTestHarness.SourceFile("Derived.cs", source));

        await Assert.That(result.GeneratorExceptions).IsEmpty();
        await Assert.That(result.GeneratorDiagnostics).IsEmpty();
        await Assert.That(result.CompilationDiagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)).IsEmpty();
        await Assert.That(result.GeneratedSources).HasSingleItem();
    }
}
