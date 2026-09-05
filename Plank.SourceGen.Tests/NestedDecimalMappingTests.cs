using Microsoft.CodeAnalysis;

namespace Plank.SourceGen.Tests;

internal sealed class NestedDecimalMappingTests
{
    [Test]
    [Arguments("Int32", 9, 2)]
    [Arguments("Int32", 9, 9)]
    [Arguments("Int64", 18, 2)]
    [Arguments("ByteArray", 29, 2)]
    [Arguments("FixedLenByteArray", 29, 2)]
    [Arguments("FixedLenByteArray", 29, 28)]
    public async Task DecimalColumnsCompileBesideCollectionsAndInsideGroups(string physicalType, int precision, int scale)
    {
        var result = Run($$"""
            [ParquetColumn(ParquetPhysicalType.{{physicalType}}, Precision = {{precision}}, Scale = {{scale}})]
            public decimal Amount { get; set; }
            [ParquetColumn(ParquetPhysicalType.{{physicalType}}, Precision = {{precision}}, Scale = {{scale}})]
            public decimal? OptionalAmount { get; set; }
            public DecimalDetails? Details { get; set; }
            """, """
            public sealed class DecimalDetails
            {
                [ParquetColumn(Precision = 18, Scale = 4)]
                public decimal Amount { get; set; }
            }
            """);
        await Assert.That(result.GeneratorExceptions).IsEmpty();
        await Assert.That(result.GeneratorDiagnostics.Where(static diagnostic =>
            diagnostic.Severity == DiagnosticSeverity.Error)).IsEmpty();
        await Assert.That(result.CompilationDiagnostics.Where(static diagnostic =>
            diagnostic.Severity == DiagnosticSeverity.Error)).IsEmpty();
        await Assert.That(result.GeneratedSources.Single().Text).Contains($"LogicalType.Decimal({precision}, {scale})");
    }

    [Test]
    [Arguments("", "PLANKGEN014")]
    [Arguments("Precision = 0", "PLANKGEN014")]
    [Arguments("Precision = -1", "PLANKGEN014")]
    [Arguments("Precision = 30", "PLANKGEN014")]
    [Arguments("Precision = 9, Scale = -1", "PLANKGEN014")]
    [Arguments("Precision = 9, Scale = 10", "PLANKGEN014")]
    [Arguments("Precision = 29, Scale = 29", "PLANKGEN014")]
    [Arguments("ParquetPhysicalType.Int32, Precision = 10", "PLANKGEN015")]
    [Arguments("ParquetPhysicalType.Int64, Precision = 19", "PLANKGEN015")]
    public async Task InvalidDecimalDefinitionsUseSharedDiagnostics(string arguments, string diagnosticId)
    {
        var result = Run($"[ParquetColumn({arguments})] public decimal? Amount {{ get; set; }}");
        await Assert.That(result.GeneratorExceptions).IsEmpty();
        await Assert.That(result.GeneratorDiagnostics.Any(diagnostic =>
            diagnostic.Id == diagnosticId && diagnostic.Severity == DiagnosticSeverity.Error)).IsTrue();
        await Assert.That(result.GeneratedSources).IsEmpty();
    }

    [Test]
    [Arguments("DeltaByteArray")]
    [Arguments("DeltaLengthByteArray")]
    public async Task UnsupportedDecimalEncodingIsDiagnosed(string encoding)
    {
        var result = Run($$"""
            [ParquetColumn(ParquetPhysicalType.ByteArray, Precision = 9, Scale = 2,
                Encodings = new[] { EncodingKind.{{encoding}} })]
            public decimal? Amount { get; set; }
            """);
        await Assert.That(result.GeneratorExceptions).IsEmpty();
        await Assert.That(result.GeneratorDiagnostics.Any(static diagnostic =>
            diagnostic.Id == "PLANKGEN017" && diagnostic.Severity == DiagnosticSeverity.Error)).IsTrue();
        await Assert.That(result.GeneratedSources).IsEmpty();
    }

    [Test]
    public async Task DecimalCollectionsRemainAnExplicitUnsupportedShape()
    {
        var result = Run("[ParquetColumn(Precision = 9, Scale = 2)] public decimal[] Amounts { get; set; } = [];");
        await Assert.That(result.GeneratorExceptions).IsEmpty();
        await Assert.That(result.GeneratorDiagnostics.Any(static diagnostic =>
            diagnostic.GetMessage(System.Globalization.CultureInfo.InvariantCulture)
                .Contains("not supported by generated repeated storage", StringComparison.Ordinal))).IsTrue();
        await Assert.That(result.GeneratedSources).IsEmpty();
    }

    static GeneratorTestHarness.RunResult Run(string properties, string extra = "")
        => GeneratorTestHarness.Run(new GeneratorTestHarness.SourceFile("NestedDecimalSchema.cs", $$"""
            using Plank.Schema;
            [ParquetSchema(AllowAllocatingValues = true)]
            public partial class NestedDecimalSchema
            {
                public int[] Values { get; set; } = [];
                {{properties}}
            }
            {{extra}}
            """));
}
