using Microsoft.CodeAnalysis;

namespace Plank.SourceGen.Tests;

internal sealed class NestedColumnMetadataTests
{
    [Test]
    public async Task ScalarOverridesAreIdenticalWithAndWithoutCollections()
    {
        const string properties = """
                [ParquetColumn("key", FieldId = 42, BloomFilter = true,
                    BloomFilterFalsePositiveProbability = 0.002,
                    BloomFilterExpectedDistinctValueCount = 123, BloomFilterMaximumBytes = 4096,
                    Encodings = new[] { EncodingKind.DeltaBinaryPacked },
                    Compression = CompressionKind.Zstd, CompressionLevel = 3)]
                public int Key { get; set; }
                [ParquetColumn(FieldId = 43, BloomFilter = true)]
                public System.Guid Token { get; set; }
            """;
        var flat = Run(properties);
        var nested = Run(properties + "public int[] Values { get; set; } = [];");
        await AssertValid(flat).ConfigureAwait(false);
        await AssertValid(nested).ConfigureAwait(false);
        // The complete leaf definition includes every override, including defaults and fixed widths.
        foreach (var line in flat.GeneratedSources.Single().Text.Split('\n')
                     .Where(static line => line.Contains("ColumnDefinition.Leaf(", StringComparison.Ordinal)))
            await Assert.That(nested.GeneratedSources.Single().Text).Contains(line.Trim());
    }

    [Test]
    [Arguments("[ParquetColumn(BloomFilter = true)] public System.Collections.Generic.Dictionary<int, int> Data { get; set; } = [];")]
    [Arguments("[ParquetColumn(Compression = CompressionKind.Zstd)] public Entry Data { get; set; } = new();")]
    [Arguments("[ParquetColumn(Encodings = new[] { EncodingKind.Plain })] public Entry[] Data { get; set; } = [];")]
    public async Task UnsupportedContainerOptionsProduceDiagnostic(string property)
    {
        var result = Run(property, "public class Entry { public int Code { get; set; } }");
        await Assert.That(result.GeneratorExceptions).IsEmpty();
        await Assert.That(result.GeneratorDiagnostics.Any(static diagnostic =>
            diagnostic.Severity == DiagnosticSeverity.Error &&
            diagnostic.GetMessage(System.Globalization.CultureInfo.InvariantCulture).Contains("leaf column options", StringComparison.Ordinal))).IsTrue();
        await Assert.That(result.GeneratedSources).IsEmpty();
    }

    [Test]
    [Arguments("[ParquetColumn(Encodings = new[] { (EncodingKind)999 })] public int Code { get; set; }")]
    [Arguments("[ParquetColumn(Encodings = new[] { EncodingKind.DeltaBinaryPacked })] public double Code { get; set; }")]
    [Arguments("[ParquetColumn(LogicalType = LogicalTypeKind.None)] public uint Code { get; set; }")]
    [Arguments("[ParquetColumn(ParquetPhysicalType.Int64)] public int Code { get; set; }")]
    public async Task NestedMappingRejectsInvalidScalarOverrides(string property)
    {
        var result = Run(property + "public int[] Values { get; set; } = [];");
        await Assert.That(result.GeneratorExceptions).IsEmpty();
        await Assert.That(result.GeneratorDiagnostics.Any(static diagnostic =>
            diagnostic.Severity == DiagnosticSeverity.Error)).IsTrue();
        await Assert.That(result.GeneratedSources).IsEmpty();
    }

    [Test]
    [Arguments("BloomFilterFalsePositiveProbability = 0")]
    [Arguments("BloomFilterFalsePositiveProbability = 1")]
    [Arguments("BloomFilterFalsePositiveProbability = double.NaN")]
    [Arguments("BloomFilterFalsePositiveProbability = double.PositiveInfinity")]
    [Arguments("BloomFilterMaximumBytes = 16")]
    [Arguments("BloomFilterMaximumBytes = 1000")]
    public async Task InvalidBloomSettingsAreDiagnosedByBothEmitters(string option)
    {
        foreach (var collection in new[] { "", "public int[] Values { get; set; } = [];" })
        {
            var result = Run($"[ParquetColumn(BloomFilter = true, {option})] public int Code {{ get; set; }}" + collection);
            await Assert.That(result.GeneratorExceptions).IsEmpty();
            await Assert.That(result.GeneratorDiagnostics.Any(static diagnostic =>
                diagnostic.Severity == DiagnosticSeverity.Error &&
                diagnostic.GetMessage(System.Globalization.CultureInfo.InvariantCulture).Contains("Bloom filter", StringComparison.Ordinal))).IsTrue();
            await Assert.That(result.GeneratedSources).IsEmpty();
        }
    }

    [Test]
    public async Task NullEncodingOverrideUsesDefaults()
    {
        await AssertValid(Run("[ParquetColumn(Encodings = null)] public int[] Values { get; set; } = [];")).ConfigureAwait(false);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task DuplicateSiblingNamesAreDiagnosed(bool insideGroup)
    {
        const string duplicates = "[ParquetColumn(\"same\")] public int First { get; set; } " +
            "[ParquetColumn(\"same\")] public int Second { get; set; }";
        var result = insideGroup
            ? Run("public Entry Data { get; set; } = new();", "public class Entry { " + duplicates + " }")
            : Run(duplicates + "public int[] Values { get; set; } = [];");
        await Assert.That(result.GeneratorExceptions).IsEmpty();
        await Assert.That(result.GeneratorDiagnostics.Any(static diagnostic =>
            diagnostic.GetMessage(System.Globalization.CultureInfo.InvariantCulture)
                .Contains("Duplicate column name", StringComparison.Ordinal))).IsTrue();
        await Assert.That(result.GeneratedSources).IsEmpty();
    }

    [Test]
    public async Task ConverterIsDiagnosedInsteadOfSilentlyDiscarded()
    {
        var result = Run("""
            [ParquetColumn(Converter = typeof(IdentityConverter))]
            public int Code { get; set; }
            public int[] Values { get; set; } = [];
            """, """
            public class IdentityConverter : ParquetValueConverter<int, int>
            {
                public override int ConvertToPhysical(int value) => value;
                public override int ConvertFromPhysical(int value) => value;
            }
            """);
        await Assert.That(result.GeneratorExceptions).IsEmpty();
        await Assert.That(result.GeneratorDiagnostics.Any(static diagnostic =>
            diagnostic.GetMessage(System.Globalization.CultureInfo.InvariantCulture).Contains("converter", StringComparison.Ordinal))).IsTrue();
        await Assert.That(result.GeneratedSources).IsEmpty();
    }

    static GeneratorTestHarness.RunResult Run(string properties, string extra = "")
        => GeneratorTestHarness.Run(new GeneratorTestHarness.SourceFile("MetadataSchema.cs", $$"""
            using Plank.Schema;
            [ParquetSchema(AllowAllocatingValues = true)]
            public partial class MetadataSchema
            {
                {{properties}}
            }
            {{extra}}
            """));

    static async Task AssertValid(GeneratorTestHarness.RunResult result)
    {
        await Assert.That(result.GeneratorExceptions).IsEmpty();
        await Assert.That(result.GeneratorDiagnostics.Where(static diagnostic =>
            diagnostic.Severity == DiagnosticSeverity.Error)).IsEmpty();
        await Assert.That(result.CompilationDiagnostics.Where(static diagnostic =>
            diagnostic.Severity == DiagnosticSeverity.Error)).IsEmpty();
    }
}
