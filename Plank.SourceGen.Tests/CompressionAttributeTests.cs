namespace Plank.SourceGen.Tests;

internal sealed class CompressionAttributeTests
{
    [Test]
    public async Task FlatColumnAttributeEmitsCompressionConfiguration()
    {
        const string source = """
            using Plank.Schema;

            namespace Regression;

            [ParquetSchema]
            partial class CompressionSchema
            {
                [ParquetColumn(Compression = CompressionKind.Gzip, CompressionLevel = 6, BloomFilter = true)]
                public int Value { get; set; }
            }
            """;

        var result = GeneratorTestHarness.Run(new GeneratorTestHarness.SourceFile("CompressionSchema.cs", source));
        var generated = string.Join('\n', result.GeneratedSources.Select(static generated => generated.Text));

        await Assert.That(result.GeneratorExceptions).IsEmpty();
        await Assert.That(result.CompilationDiagnostics.Where(static diagnostic =>
            diagnostic.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error)).IsEmpty();
        await Assert.That(generated).Contains(
            "compression: global::Plank.Schema.CompressionKind.Gzip, compressionLevel: 6");
    }

    [Test]
    public async Task NestedColumnAttributeEmitsCompressionConfiguration()
    {
        const string source = """
            using Plank.Schema;

            namespace Regression;

            [ParquetSchema(AllowAllocatingValues = true)]
            partial class CompressionSchema
            {
                public Payload Data { get; set; } = new();
            }

            sealed class Payload
            {
                [ParquetColumn(Compression = CompressionKind.Brotli, CompressionLevel = 4)]
                public byte[] Value { get; set; } = [];
            }
            """;

        var result = GeneratorTestHarness.Run(new GeneratorTestHarness.SourceFile("CompressionSchema.cs", source));
        var generated = string.Join('\n', result.GeneratedSources.Select(static generated => generated.Text));

        await Assert.That(result.GeneratorExceptions).IsEmpty();
        await Assert.That(result.CompilationDiagnostics.Where(static diagnostic =>
            diagnostic.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error)).IsEmpty();
        await Assert.That(generated).Contains(
            "compression: global::Plank.Schema.CompressionKind.Brotli, compressionLevel: 4");
    }
}
