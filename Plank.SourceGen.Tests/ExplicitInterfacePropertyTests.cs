using System.Globalization;
using Microsoft.CodeAnalysis;

namespace Plank.SourceGen.Tests;

internal sealed class ExplicitInterfacePropertyTests
{
    [Test]
    [Arguments("")]
    [Arguments("public int[] Values { get; set; } = [];")]
    public async Task ExplicitInterfacePropertyProducesDiagnostic(string additionalProperty)
    {
        var source = $$"""
            using Plank.Schema;

            interface IValue { int Value { get; set; } }

            [ParquetSchema(AllowAllocatingValues = true)]
            partial class Data : IValue
            {
                int IValue.Value { get => 42; set { } }
                {{additionalProperty}}
            }
            """;

        var result = GeneratorTestHarness.Run(new GeneratorTestHarness.SourceFile("Data.cs", source));

        await Assert.That(result.GeneratorExceptions).IsEmpty();
        await Assert.That(result.CompilationDiagnostics.Where(diagnostic =>
            diagnostic.Severity == DiagnosticSeverity.Error)).IsEmpty();
        await Assert.That(result.GeneratedSources).IsEmpty();
        await Assert.That(result.GeneratorDiagnostics).HasSingleItem();
        var diagnostic = result.GeneratorDiagnostics.Single();
        await Assert.That(diagnostic.Id).IsEqualTo("PLANKGEN003");
        await Assert.That(diagnostic.GetMessage(CultureInfo.InvariantCulture)).Contains("explicitly implements");
    }
}
