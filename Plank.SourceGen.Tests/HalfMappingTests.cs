using System.Globalization;
using Microsoft.CodeAnalysis;

namespace Plank.SourceGen.Tests;

internal sealed class HalfMappingTests
{
    [Test]
    [Arguments("Half", false)]
    [Arguments("Half?", false)]
    [Arguments("Half[]", true)]
    public async Task HalfReportsUnsupportedScalarMapping(string propertyType, bool allowAllocatingValues)
    {
        var source = $$"""
            using System;
            using Plank.Schema;

            [ParquetSchema(AllowAllocatingValues = {{(allowAllocatingValues ? "true" : "false")}})]
            partial class Data
            {
                public {{propertyType}} Value { get; set; }
            }
            """;
        var result = GeneratorTestHarness.Run(new GeneratorTestHarness.SourceFile("Data.cs", source));

        await Assert.That(result.GeneratorExceptions).IsEmpty();
        await Assert.That(result.CompilationDiagnostics.Where(diagnostic =>
            diagnostic.Severity == DiagnosticSeverity.Error)).IsEmpty();
        await Assert.That(result.GeneratedSources).IsEmpty();
        await Assert.That(result.GeneratorDiagnostics).HasSingleItem();
        var diagnostic = result.GeneratorDiagnostics.Single();
        await Assert.That(diagnostic.GetMessage(CultureInfo.InvariantCulture)).Contains("FLOAT16");
    }
}
