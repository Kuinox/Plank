using System.Globalization;
using Microsoft.CodeAnalysis;

namespace Plank.SourceGen.Tests;

internal sealed class NestedDtoVisibilityTests
{
    [Test]
    [Arguments("public", "internal", "Item", true)]
    [Arguments("internal", "private", "Item", true)]
    [Arguments("internal", "private", "Item[]", true)]
    [Arguments("internal", "internal", "Item", false)]
    public async Task NestedDtoVisibilityIsChecked(string schemaVisibility, string itemVisibility,
        string propertyType, bool rejected)
    {
        var source = $$"""
            using Plank.Schema;
            [ParquetSchema(AllowAllocatingValues = true)]
            {{schemaVisibility}} partial class Data
            {
                private {{propertyType}} Value { get; set; } = default!;
                {{itemVisibility}} class Item { public int Number { get; set; } }
            }
            """;
        var result = GeneratorTestHarness.Run(new GeneratorTestHarness.SourceFile("Data.cs", source));
        await Assert.That(result.GeneratorExceptions).IsEmpty();
        if (rejected)
        {
            await Assert.That(result.GeneratorDiagnostics).HasSingleItem();
            var diagnostic = result.GeneratorDiagnostics.Single();
            await Assert.That(diagnostic.Id).IsEqualTo("PLANKGEN003");
            await Assert.That(diagnostic.GetMessage(CultureInfo.InvariantCulture)).Contains("less accessible");
            await Assert.That(result.GeneratedSources).IsEmpty();
        }
        else
        {
            await Assert.That(result.GeneratorDiagnostics).IsEmpty();
            await Assert.That(result.GeneratedSources).HasSingleItem();
        }
        await Assert.That(result.CompilationDiagnostics.Where(static d => d.Severity == DiagnosticSeverity.Error)).IsEmpty();
    }
}
