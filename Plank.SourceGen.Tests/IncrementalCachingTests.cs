using Microsoft.CodeAnalysis;

namespace Plank.SourceGen.Tests;

internal sealed class IncrementalCachingTests
{
    [Test]
    public async Task EditingOneSchemaKeepsTheOtherSchemaOutputCached()
    {
        const string initialEditedSchema = """
            using Plank.Schema;

            [ParquetSchema]
            partial class EditedSchema
            {
                public int Value { get; set; }
            }
            """;
        const string updatedEditedSchema = """
            using Plank.Schema;

            [ParquetSchema]
            partial class EditedSchema
            {
                public long Value { get; set; }
            }
            """;
        const string unchangedSchema = """
            using Plank.Schema;

            [ParquetSchema]
            partial class UnchangedSchema
            {
                public int Value { get; set; }
            }
            """;

        var result = GeneratorTestHarness.RunAfterSchemaEdit(
            new GeneratorTestHarness.SourceFile("EditedSchema.cs", initialEditedSchema),
            new GeneratorTestHarness.SourceFile("EditedSchema.cs", updatedEditedSchema),
            new GeneratorTestHarness.SourceFile("UnchangedSchema.cs", unchangedSchema));
        var outputReasons = result.Results.Single().TrackedOutputSteps.Values
            .SelectMany(steps => steps)
            .SelectMany(step => step.Outputs)
            .Select(output => output.Reason)
            .ToArray();

        await Assert.That(outputReasons.Length).IsEqualTo(2);
        await Assert.That(outputReasons[0]).IsEqualTo(IncrementalStepRunReason.Modified);
        await Assert.That(outputReasons[1]).IsEqualTo(IncrementalStepRunReason.Cached);
    }

    [Test]
    public async Task UnrelatedEditLeavesEachSchemaOutputUnchanged()
    {
        const string firstSchema = """
            using Plank.Schema;

            [ParquetSchema]
            partial class FirstSchema
            {
                public int Value { get; set; }
            }
            """;
        const string secondSchema = """
            using Plank.Schema;

            [ParquetSchema]
            partial class SecondSchema
            {
                public long Value { get; set; }
            }
            """;

        const string invalidSchema = """
            using Plank.Schema;

            [ParquetSchema]
            partial class InvalidSchema
            {
            }
            """;

        var result = GeneratorTestHarness.RunAfterUnrelatedEdit(
            new GeneratorTestHarness.SourceFile("FirstSchema.cs", firstSchema),
            new GeneratorTestHarness.SourceFile("SecondSchema.cs", secondSchema),
            new GeneratorTestHarness.SourceFile("InvalidSchema.cs", invalidSchema));
        var generatorResult = result.Results.Single();
        var outputReasons = generatorResult.TrackedOutputSteps.Values
            .SelectMany(steps => steps)
            .SelectMany(step => step.Outputs)
            .Select(output => output.Reason)
            .ToArray();

        await Assert.That(outputReasons.Length).IsEqualTo(3);
        await Assert.That(outputReasons.All(reason => reason == IncrementalStepRunReason.Cached)).IsTrue();
        await Assert.That(generatorResult.GeneratedSources.Length).IsEqualTo(2);
        var diagnostic = result.Diagnostics.Single(diagnostic => diagnostic.Id == "PLANKGEN003");
        await Assert.That(diagnostic.Location.GetLineSpan().Path).IsEqualTo("InvalidSchema.cs");
        await Assert.That(diagnostic.Location.SourceTree).IsNull();
    }
}
