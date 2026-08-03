using System.Globalization;

namespace Plank.SourceGen.Tests;

internal sealed class DecimalMappingTests
{
    [Test]
    public async Task DecimalPrecisionAndScaleGenerateFixedLengthSchema()
    {
        const string source = """
            using Plank.Schema;

            namespace Regression;

            [ParquetSchema]
            partial class DecimalSchema
            {
                [ParquetColumn(Precision = 29, Scale = 8)]
                public decimal Value { get; set; }

                [ParquetColumn(ParquetPhysicalType.Int64, Precision = 18, Scale = 4)]
                public decimal? Small { get; set; }
            }
            """;

        var result = GeneratorTestHarness.Run(
            new GeneratorTestHarness.SourceFile("DecimalSchema.cs", source));

        await Assert.That(result.GeneratorExceptions).IsEmpty();
        await Assert.That(result.GeneratorDiagnostics.Where(static diagnostic =>
            diagnostic.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error)).IsEmpty();
        await Assert.That(result.CompilationDiagnostics.Where(static diagnostic =>
            diagnostic.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error)).IsEmpty();
        var generated = result.GeneratedSources.Single().Text;
        await Assert.That(generated).Contains("LogicalType.Decimal(29, 8)");
        await Assert.That(generated).Contains("FixedLenByteArray");
        await Assert.That(generated).Contains(", 13)");
        await Assert.That(generated).Contains("LogicalType.Decimal(18, 4)");
    }

    [Test]
    public async Task DecimalWithoutPrecisionReportsDiagnostic()
    {
        const string source = """
            using Plank.Schema;

            [ParquetSchema]
            partial class MissingPrecisionSchema
            {
                public decimal Value { get; set; }
            }
            """;

        var result = GeneratorTestHarness.Run(
            new GeneratorTestHarness.SourceFile("MissingPrecisionSchema.cs", source));
        var diagnostics = result.GeneratorDiagnostics
            .Where(static diagnostic => diagnostic.Id == "PLANKGEN014")
            .Select(diagnostic => diagnostic.GetMessage(CultureInfo.InvariantCulture))
            .ToArray();

        await Assert.That(diagnostics).HasSingleItem();
        await Assert.That(diagnostics[0]).Contains("precision must be positive");
    }

    [Test]
    public async Task DecimalOutsideClrRangeReportsDiagnostic()
    {
        const string source = """
            using Plank.Schema;

            [ParquetSchema]
            partial class WideDecimalSchema
            {
                [ParquetColumn(Precision = 30, Scale = 29)]
                public decimal Value { get; set; }
            }
            """;

        var result = GeneratorTestHarness.Run(
            new GeneratorTestHarness.SourceFile("WideDecimalSchema.cs", source));
        var diagnostics = result.GeneratorDiagnostics
            .Where(static diagnostic => diagnostic.Id == "PLANKGEN014")
            .Select(diagnostic => diagnostic.GetMessage(CultureInfo.InvariantCulture))
            .ToArray();

        await Assert.That(diagnostics.Length).IsEqualTo(2);
    }
}
