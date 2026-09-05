using Microsoft.CodeAnalysis;

namespace Plank.SourceGen.Tests;

internal sealed class NestedSetterAccessibilityTests
{
    [Test]
    [Arguments("private set", "Point", "new()")]
    [Arguments("protected set", "Point", "new()")]
    [Arguments("private protected set", "Point", "new()")]
    [Arguments("private init", "Point", "new()")]
    [Arguments("private set", "Point[]", "[]")]
    public async Task InaccessibleNestedSetterProducesDiagnostic(string setter, string propertyType,
        string initializer)
    {
        var result = Run(setter, propertyType, initializer);

        await Assert.That(result.GeneratorExceptions).IsEmpty();
        await Assert.That(result.GeneratedSources).IsEmpty();
        await Assert.That(result.GeneratorDiagnostics).HasSingleItem();
        var diagnostic = result.GeneratorDiagnostics.Single();
        await Assert.That(diagnostic.Id).IsEqualTo("PLANKGEN003");
        await Assert.That(diagnostic.Severity).IsEqualTo(DiagnosticSeverity.Error);
        await Assert.That(diagnostic.GetMessage(System.Globalization.CultureInfo.InvariantCulture))
            .Contains("Point.X");
        await Assert.That(diagnostic.GetMessage(System.Globalization.CultureInfo.InvariantCulture))
            .Contains("accessible from schema type 'Data'");
        await Assert.That(result.CompilationDiagnostics.Where(static diagnostic =>
            diagnostic.Severity == DiagnosticSeverity.Error)).IsEmpty();
    }

    [Test]
    [Arguments("set", "Point", "new()")]
    [Arguments("init", "Point", "new()")]
    [Arguments("internal set", "Point", "new()")]
    [Arguments("protected internal set", "Point", "new()")]
    [Arguments("internal init", "Point[]", "[]")]
    public async Task AccessibleNestedSettersStillCompile(string setter, string propertyType, string initializer)
    {
        var result = Run(setter, propertyType, initializer);

        await Assert.That(result.GeneratorExceptions).IsEmpty();
        await Assert.That(result.GeneratorDiagnostics).IsEmpty();
        await Assert.That(result.GeneratedSources).HasSingleItem();
        await Assert.That(result.CompilationDiagnostics.Where(static diagnostic =>
            diagnostic.Severity == DiagnosticSeverity.Error)).IsEmpty();
    }

    static GeneratorTestHarness.RunResult Run(string setter, string propertyType, string initializer)
    {
        var source = $$"""
            using Plank.Schema;

            [ParquetSchema(AllowAllocatingValues = true)]
            public partial class Data
            {
                public {{propertyType}} Position { get; set; } = {{initializer}};
            }

            public class Point
            {
                public int X { get; {{setter}}; }
                public int Y { get; set; }
            }
            """;

        return GeneratorTestHarness.Run(new GeneratorTestHarness.SourceFile("NestedSetter.cs", source));
    }
}
