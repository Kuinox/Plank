using Microsoft.CodeAnalysis;

namespace Plank.SourceGen.Tests;

internal sealed class NullableArrayGenerationTests
{
    [Test]
    public async Task NullableReferenceAndValueArrayElementsGenerateCompilableRowApis()
    {
        const string source = """
            using System;
            using System.Collections.Generic;
            using Plank.Schema;

            [ParquetSchema(AllowAllocatingValues = true)]
            partial class NullableArrays
            {
                public string?[]? Names { get; set; }
                public string?[][]? NameGroups { get; set; }
                public List<string?>[]? NameLists { get; set; }
                public byte[]?[]? Payloads { get; set; }
                public int?[]? Numbers { get; set; }
                public Guid?[]? Identifiers { get; set; }
                public DateOnly?[]? Dates { get; set; }
            }
            """;

        var result = GeneratorTestHarness.Run(new GeneratorTestHarness.SourceFile("NullableArrays.cs", source));

        await Assert.That(result.GeneratorExceptions).IsEmpty();
        await Assert.That(result.GeneratorDiagnostics.Where(static diagnostic =>
            diagnostic.Severity == DiagnosticSeverity.Error)).IsEmpty();
        await Assert.That(result.CompilationDiagnostics.Where(static diagnostic =>
            diagnostic.Severity == DiagnosticSeverity.Error)).IsEmpty();
        await Assert.That(result.GeneratedSources).HasSingleItem();
    }
}
