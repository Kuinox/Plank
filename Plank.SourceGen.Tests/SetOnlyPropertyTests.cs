using System.Globalization;
using Microsoft.CodeAnalysis;

namespace Plank.SourceGen.Tests;

internal sealed class SetOnlyPropertyTests
{
    [Test]
    [Arguments("public int Value { set { } }", "")]
    [Arguments("public int[] Value { set { } }", "")]
    [Arguments("public Part Item { get; set; } = new();", "class Part { public int Value { set { } } }")]
    public async Task SetOnlyPropertyProducesDiagnostic(string member, string nestedType)
    {
        var source = $$"""
            using Plank.Schema;

            [ParquetSchema(AllowAllocatingValues = true)]
            partial class Data { {{member}} }
            {{nestedType}}
            """;

        var result = GeneratorTestHarness.Run(new GeneratorTestHarness.SourceFile("Data.cs", source));

        await Assert.That(result.GeneratorExceptions).IsEmpty();
        await Assert.That(result.CompilationDiagnostics.Where(diagnostic =>
            diagnostic.Severity == DiagnosticSeverity.Error)).IsEmpty();
        await Assert.That(result.GeneratedSources).IsEmpty();
        await Assert.That(result.GeneratorDiagnostics).HasSingleItem();
        var diagnostic = result.GeneratorDiagnostics.Single();
        await Assert.That(diagnostic.Id).IsEqualTo("PLANKGEN003");
        await Assert.That(diagnostic.GetMessage(CultureInfo.InvariantCulture)).Contains("getter");
    }
}
