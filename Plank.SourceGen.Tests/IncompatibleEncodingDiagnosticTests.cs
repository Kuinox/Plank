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

    [Test]
    public async Task AlpAcceptsFloatAndRejectsIntegerColumns()
    {
        const string source = """
            using Plank.Schema;

            namespace Regression;

            [ParquetSchema]
            partial class AlpSchema
            {
                [ParquetColumn("measurement", Encodings = [EncodingKind.Alp])]
                public double Measurement { get; set; }

                [ParquetColumn("counter", Encodings = [EncodingKind.Alp])]
                public long Counter { get; set; }
            }
            """;

        var result = GeneratorTestHarness.Run(
            new GeneratorTestHarness.SourceFile("AlpSchema.cs", source));
        var encodingErrors = result.GeneratorDiagnostics
            .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .Select(static diagnostic => diagnostic.GetMessage(System.Globalization.CultureInfo.InvariantCulture))
            .Where(static message => message.Contains("Alp", StringComparison.Ordinal))
            .ToArray();

        await Assert.That(encodingErrors).Count().IsEqualTo(1);
        await Assert.That(encodingErrors[0]).Contains("counter");
    }
}
