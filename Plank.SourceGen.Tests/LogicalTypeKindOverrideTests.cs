using System.Globalization;

namespace Plank.SourceGen.Tests;

internal sealed class LogicalTypeKindOverrideTests
{
    [Test]
    public async Task DeclaredLogicalTypeKindsAreAccepted()
    {
        const string source = """
            using System;
            using Plank.Schema;

            namespace Regression;

            [ParquetSchema]
            partial class DateOverrideSchema
            {
                [ParquetColumn(LogicalType = LogicalTypeKind.Date)]
                public DateOnly Value { get; set; }
            }

            [ParquetSchema]
            partial class TimeOverrideSchema
            {
                [ParquetColumn(LogicalType = LogicalTypeKind.Time)]
                public TimeOnly Value { get; set; }
            }

            [ParquetSchema]
            partial class TimestampOverrideSchema
            {
                [ParquetColumn(LogicalType = LogicalTypeKind.Timestamp)]
                public DateTime Value { get; set; }
            }

            [ParquetSchema]
            partial class IntegerOverrideSchema
            {
                [ParquetColumn(LogicalType = LogicalTypeKind.Integer)]
                public uint Value { get; set; }
            }
            """;

        var result = GeneratorTestHarness.Run(
            new GeneratorTestHarness.SourceFile("LogicalTypeKindOverrides.cs", source));
        var invalidOverrides = result.GeneratorDiagnostics
            .Where(diagnostic => diagnostic.Id == "PLANKGEN004"
                && diagnostic.GetMessage(CultureInfo.InvariantCulture)
                    .Contains("invalid LogicalTypeKind override", StringComparison.Ordinal))
            .Select(diagnostic => diagnostic.ToString())
            .ToArray();

        await Assert.That(invalidOverrides).IsEmpty();
    }
}
