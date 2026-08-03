using Microsoft.CodeAnalysis;

namespace Plank.SourceGen.Tests;

internal sealed class NestedRowGenerationTests
{
    [Test]
    public async Task CollectionsRequireAllocatingValueOptIn()
    {
        const string source = """
            using Plank.Schema;

            [ParquetSchema]
            partial class CollectionSchema
            {
                public int[] Values { get; set; } = [];
            }
            """;

        var result = GeneratorTestHarness.Run(new GeneratorTestHarness.SourceFile("CollectionSchema.cs", source));

        await Assert.That(result.GeneratorDiagnostics.Any(static diagnostic =>
            diagnostic.Id == "PLANKGEN016" && diagnostic.Severity == DiagnosticSeverity.Error)).IsTrue();
        await Assert.That(result.GeneratedSources).IsEmpty();
    }

    [Test]
    public async Task ArraysMapsAndNestedRecordsGenerateCompilableRowApis()
    {
        const string source = """
            using System;
            using System.Collections.Generic;
            using Plank.Schema;

            [ParquetSchema(AllowAllocatingValues = true)]
            partial class NestedSchema
            {
                public int?[][]? Values { get; set; }
                public Dictionary<int, int?>? Scores { get; set; }
                public Address? Location { get; set; }
                public List<Entry>? Items { get; set; }
                public List<string?>? Names { get; set; }
                public List<byte[]?>? Payloads { get; set; }
                public List<ReadOnlyMemory<byte>?>? Memories { get; set; }
                public List<Guid?>? Identifiers { get; set; }
                public List<DateOnly?>? Dates { get; set; }
                public List<TimeOnly?>? Times { get; set; }
                public List<DateTime?>? Timestamps { get; set; }
                public List<DateTimeOffset?>? Instants { get; set; }
                public byte Priority { get; set; }
                public string Label { get; set; } = string.Empty;
                public Guid CorrelationId { get; set; }
                public DateOnly Date { get; set; }
            }

            sealed record Address
            {
                public int Zip { get; init; }
                public byte Rank { get; init; }
                public string Street { get; init; } = string.Empty;
                public Guid Token { get; init; }
            }

            sealed record Entry
            {
                public int Id { get; init; }
                public long Amount { get; init; }
            }
            """;

        var result = GeneratorTestHarness.Run(new GeneratorTestHarness.SourceFile("NestedSchema.cs", source));
        var errors = result.CompilationDiagnostics
            .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .Select(static diagnostic => diagnostic.ToString())
            .ToArray();
        await Assert.That(result.GeneratorExceptions).IsEmpty();
        await Assert.That(result.GeneratorDiagnostics.Where(static diagnostic =>
            diagnostic.Severity == DiagnosticSeverity.Error)).IsEmpty();
        await Assert.That(errors).IsEmpty();
        await Assert.That(result.GeneratedSources).HasSingleItem();
    }

    [Test]
    public async Task OptionalInnerListsReportUnsupportedShape()
    {
        const string source = """
            using Plank.Schema;

            [ParquetSchema(AllowAllocatingValues = true)]
            partial class NestedSchema
            {
                public int[]?[] Values { get; set; } = [];
            }
            """;

        var result = GeneratorTestHarness.Run(new GeneratorTestHarness.SourceFile("NestedSchema.cs", source));

        await Assert.That(result.GeneratorDiagnostics.Any(static diagnostic =>
            diagnostic.Id == "PLANKGEN003" && diagnostic.Severity == DiagnosticSeverity.Error &&
            diagnostic.GetMessage(System.Globalization.CultureInfo.InvariantCulture)
                .Contains("non-null inner lists", StringComparison.Ordinal))).IsTrue();
        await Assert.That(result.GeneratedSources).IsEmpty();
    }
}
