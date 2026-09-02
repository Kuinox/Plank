namespace Plank.SourceGen.Tests;

internal sealed class BinaryRowReaderGenerationTests
{
    [Test]
    public async Task FlatBinaryRowsExposeRetainableValues()
    {
        const string source = """
            using Plank.Schema;

            [ParquetSchema]
            partial class BinarySchema
            {
                public byte[]? Payload { get; set; }
            }
            """;

        var result = GeneratorTestHarness.Run(new GeneratorTestHarness.SourceFile("BinarySchema.cs", source));

        await Assert.That(result.GeneratorExceptions).IsEmpty();
        await Assert.That(result.CompilationDiagnostics.Where(static diagnostic =>
            diagnostic.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error)).IsEmpty();
        var generated = result.GeneratedSources.Single().Text;
        await Assert.That(generated).Contains(
            "public global::Plank.RowApi.RowReaderBinaryValue Payload");
        await Assert.That(generated).Contains(
            "=> _core.GetCurrentBinary(s_PayloadRowApiColumn);");
        await Assert.That(generated).DoesNotContain("RetainPayload()");
        await Assert.That(generated).DoesNotContain("PayloadIsNull");
    }

    [Test]
    public async Task NestedEmitterDoesNotCopyFlatBinaryRows()
    {
        const string source = """
            using Plank.Schema;

            [ParquetSchema(AllowAllocatingValues = true)]
            partial class BinarySchema
            {
                public byte[] Payload { get; set; } = [];
                public int[] Values { get; set; } = [];
            }
            """;

        var result = GeneratorTestHarness.Run(new GeneratorTestHarness.SourceFile("BinarySchema.cs", source));

        await Assert.That(result.GeneratorExceptions).IsEmpty();
        await Assert.That(result.CompilationDiagnostics.Where(static diagnostic =>
            diagnostic.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error)).IsEmpty();
        var generated = result.GeneratedSources.Single().Text;
        await Assert.That(generated).Contains(
            "public global::Plank.RowApi.RowReaderBinaryValue Payload");
        await Assert.That(generated).Contains(
            "=> _core.GetCurrentBinary(s_PayloadRowApiColumn);");
        await Assert.That(generated).DoesNotContain("RetainPayload()");
        await Assert.That(generated).DoesNotContain("PayloadIsNull");
        await Assert.That(generated).DoesNotContain("return value.Value.ToArray();");
    }
}
