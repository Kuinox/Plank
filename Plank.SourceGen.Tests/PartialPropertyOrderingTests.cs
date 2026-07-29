namespace Plank.SourceGen.Tests;

internal sealed class PartialPropertyOrderingTests
{
    [Test]
    public async Task GeneratedOutputIsIndependentOfPartialDeclarationInputOrder()
    {
        const string alpha = """
            namespace Regression;

            partial class OrderedSchema
            {
                public int Alpha { get; set; }
            }
            """;
        const string bravo = """
            namespace Regression;

            partial class OrderedSchema
            {
                public int Bravo { get; set; }
            }
            """;
        const string marker = """
            using Plank.Schema;

            namespace Regression;

            [ParquetSchema]
            partial class OrderedSchema
            {
            }
            """;

        var alphaFirst = GeneratorTestHarness.Run(
            new("Alpha.cs", alpha),
            new("Bravo.cs", bravo),
            new("Marker.cs", marker));
        var bravoFirst = GeneratorTestHarness.Run(
            new("Bravo.cs", bravo),
            new("Alpha.cs", alpha),
            new("Marker.cs", marker));

        await Assert.That(alphaFirst.GeneratedSources).HasSingleItem();
        await Assert.That(bravoFirst.GeneratedSources).HasSingleItem();
        await Assert.That(alphaFirst.GeneratedSources[0].Text)
            .IsEqualTo(bravoFirst.GeneratedSources[0].Text);
    }
}
