using Microsoft.CodeAnalysis;

namespace Plank.SourceGen.Tests;

internal sealed class CustomValueConverterTests
{
    [Test]
    public async Task ValidConverterGeneratesTypedSchemaAndRowApis()
    {
        const string source = """
            using Plank.Schema;

            namespace Regression;

            readonly record struct OrderId(int Value);

            sealed class OrderIdConverter : ParquetValueConverter<OrderId, int>
            {
                public override int ConvertToPhysical(OrderId value) => value.Value;
                public override OrderId ConvertFromPhysical(int value) => new(value);
            }

            [ParquetSchema]
            partial class OrderSchema
            {
                [ParquetColumn("id", Converter = typeof(OrderIdConverter),
                    Encodings = [EncodingKind.DeltaBinaryPacked])]
                public OrderId Id { get; set; }

                [ParquetColumn("parent_id", Converter = typeof(OrderIdConverter))]
                public OrderId? ParentId { get; set; }
            }
            """;

        var result = GeneratorTestHarness.Run(
            new GeneratorTestHarness.SourceFile("OrderSchema.cs", source));
        var errors = result.GeneratorDiagnostics.Concat(result.CompilationDiagnostics)
            .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .Select(static diagnostic => diagnostic.ToString())
            .ToArray();
        var generated = string.Join(Environment.NewLine,
            result.GeneratedSources.Select(static generatedSource => generatedSource.Text));

        await Assert.That(errors).IsEmpty();
        await Assert.That(generated).Contains("converter: new global::Regression.OrderIdConverter()");
        await Assert.That(generated).Contains("SerializedColumn<global::Regression.OrderId>");
        await Assert.That(generated).Contains("RowGroupColumn<global::Regression.OrderId?>");
    }

    [Test]
    public async Task ConverterForDifferentValueTypeReportsDiagnostic()
    {
        const string source = """
            using Plank.Schema;

            namespace Regression;

            readonly record struct OrderId(int Value);
            readonly record struct CustomerId(int Value);

            sealed class CustomerIdConverter : ParquetValueConverter<CustomerId, int>
            {
                public override int ConvertToPhysical(CustomerId value) => value.Value;
                public override CustomerId ConvertFromPhysical(int value) => new(value);
            }

            [ParquetSchema]
            partial class InvalidSchema
            {
                [ParquetColumn(Converter = typeof(CustomerIdConverter))]
                public OrderId Id { get; set; }
            }
            """;

        var result = GeneratorTestHarness.Run(
            new GeneratorTestHarness.SourceFile("InvalidSchema.cs", source));
        var diagnostic = result.GeneratorDiagnostics.FirstOrDefault(static candidate =>
            candidate.Id == "PLANKGEN004" &&
            candidate.GetMessage(System.Globalization.CultureInfo.InvariantCulture)
                .Contains("not property type", StringComparison.Ordinal));

        await Assert.That(diagnostic).IsNotNull();
    }

    [Test]
    public async Task ConverterWithConflictingPhysicalOverrideReportsDiagnostic()
    {
        const string source = """
            using Plank.Schema;

            namespace Regression;

            readonly record struct OrderId(int Value);

            sealed class OrderIdConverter : ParquetValueConverter<OrderId, int>
            {
                public override int ConvertToPhysical(OrderId value) => value.Value;
                public override OrderId ConvertFromPhysical(int value) => new(value);
            }

            [ParquetSchema]
            partial class InvalidSchema
            {
                [ParquetColumn(ParquetPhysicalType.Int64, Converter = typeof(OrderIdConverter))]
                public OrderId Id { get; set; }
            }
            """;

        var result = GeneratorTestHarness.Run(
            new GeneratorTestHarness.SourceFile("InvalidPhysicalSchema.cs", source));
        var diagnostic = result.GeneratorDiagnostics.FirstOrDefault(static candidate =>
            candidate.Id == "PLANKGEN004" &&
            candidate.GetMessage(System.Globalization.CultureInfo.InvariantCulture)
                .Contains("not declared physical type", StringComparison.Ordinal));

        await Assert.That(diagnostic).IsNotNull();
    }

    [Test]
    public async Task DecimalConverterGeneratesDecimalLogicalType()
    {
        const string source = """
            using Plank.Schema;

            namespace Regression;

            sealed class ScaledDecimalConverter : ParquetValueConverter<decimal, long>
            {
                public override long ConvertToPhysical(decimal value) => decimal.ToInt64(value * 100m);
                public override decimal ConvertFromPhysical(long value) => value / 100m;
            }

            [ParquetSchema]
            partial class PriceSchema
            {
                [ParquetColumn(Converter = typeof(ScaledDecimalConverter),
                    LogicalType = LogicalTypeKind.Decimal, DecimalPrecision = 18, DecimalScale = 2)]
                public decimal Price { get; set; }
            }
            """;

        var result = GeneratorTestHarness.Run(
            new GeneratorTestHarness.SourceFile("PriceSchema.cs", source));
        var errors = result.GeneratorDiagnostics.Concat(result.CompilationDiagnostics)
            .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .Select(static diagnostic => diagnostic.ToString())
            .ToArray();
        var generated = string.Join(Environment.NewLine,
            result.GeneratedSources.Select(static generatedSource => generatedSource.Text));

        await Assert.That(errors).IsEmpty();
        await Assert.That(generated).Contains("new global::Plank.Schema.LogicalType.Decimal(18, 2)");
        await Assert.That(generated).Contains("SerializedColumn<decimal>");
    }
}
