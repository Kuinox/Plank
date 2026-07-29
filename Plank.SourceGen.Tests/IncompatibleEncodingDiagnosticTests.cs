using Microsoft.CodeAnalysis;

namespace Plank.SourceGen.Tests;

internal sealed class IncompatibleEncodingDiagnosticTests
{
    [Test]
    public async Task GuidWithDeltaBinaryPackedReportsGeneratorDiagnostic()
    {
        const string source = """
            using System;
            using Plank.Schema;

            namespace Regression;

            [ParquetSchema]
            partial class IncompatibleEncodingSchema
            {
                [ParquetColumn("id", Encodings = [EncodingKind.DeltaBinaryPacked])]
                public Guid Id { get; set; }
            }
            """;

        var result = GeneratorTestHarness.Run(
            new GeneratorTestHarness.SourceFile("IncompatibleEncodingSchema.cs", source));
        var hasEncodingError = result.GeneratorDiagnostics.Any(diagnostic =>
            diagnostic.Severity == DiagnosticSeverity.Error
            && diagnostic.GetMessage(System.Globalization.CultureInfo.InvariantCulture)
                .Contains(nameof(Plank.Schema.EncodingKind.DeltaBinaryPacked),
                StringComparison.Ordinal));

        await Assert.That(hasEncodingError).IsTrue();
    }
}
