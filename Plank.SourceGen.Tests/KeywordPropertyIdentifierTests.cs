using Microsoft.CodeAnalysis;

namespace Plank.SourceGen.Tests;

internal sealed class KeywordPropertyIdentifierTests
{
    [Test]
    public async Task EscapedKeywordSchemaAndPropertyRemainValidGeneratedSource()
    {
        const string source = """
            using Plank.Schema;

            namespace Regression;

            [ParquetSchema]
            partial class @class
            {
                public int @event { get; set; }
            }
            """;

        var result = GeneratorTestHarness.Run(
            new GeneratorTestHarness.SourceFile("KeywordIdentifiers.cs", source));
        var errors = result.CompilationDiagnostics
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .Select(diagnostic => diagnostic.ToString())
            .ToArray();

        await Assert.That(result.GeneratorExceptions).IsEmpty();
        await Assert.That(errors).IsEmpty();
    }

    [Test]
    public async Task EscapedContextualKeywordSchemaAndNamespaceRemainValidGeneratedSource()
    {
        const string source = """
            using Plank.Schema;

            namespace @global.@file;

            public readonly record struct Id(int Value);

            public sealed class @record : ParquetValueConverter<Id, int>
            {
                public override int ConvertToPhysical(Id value) => value.Value;
                public override Id ConvertFromPhysical(int value) => new(value);
            }

            [ParquetSchema]
            public partial class @required
            {
                [ParquetColumn(Converter = typeof(@record))]
                public Id @field { get; set; }
            }
            """;

        var result = GeneratorTestHarness.Run(
            new GeneratorTestHarness.SourceFile("ContextualKeywordIdentifiers.cs", source));
        var errors = result.CompilationDiagnostics
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .Select(diagnostic => diagnostic.ToString())
            .ToArray();

        await Assert.That(result.GeneratorExceptions).IsEmpty();
        await Assert.That(result.GeneratorDiagnostics).IsEmpty();
        await Assert.That(errors).IsEmpty();
        await Assert.That(result.GeneratedSources.Single().Text).Contains("namespace @global.@file;");
        await Assert.That(result.GeneratedSources.Single().Text).Contains("partial class @required");
        await Assert.That(result.GeneratedSources.Single().Text).Contains("global::@global.@file.@required");
        await Assert.That(result.GeneratedSources.Single().Text).Contains("new global::global.file.@record()");
    }

    [Test]
    public async Task NestedEmitterEscapesContextualKeywordSchemaAndNamespace()
    {
        const string source = """
            using Plank.Schema;

            namespace @global.@file;

            [ParquetSchema(AllowAllocatingValues = true)]
            public partial class @required
            {
                public @record @field { get; set; } = new();
            }

            public sealed class @record
            {
                public int Value { get; set; }
            }
            """;

        var result = GeneratorTestHarness.Run(
            new GeneratorTestHarness.SourceFile("NestedContextualKeywordIdentifiers.cs", source));
        var errors = result.CompilationDiagnostics
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .Select(diagnostic => diagnostic.ToString())
            .ToArray();

        await Assert.That(result.GeneratorExceptions).IsEmpty();
        await Assert.That(result.GeneratorDiagnostics).IsEmpty();
        await Assert.That(errors).IsEmpty();
        await Assert.That(result.GeneratedSources.Single().Text).Contains("namespace @global.@file;");
        await Assert.That(result.GeneratedSources.Single().Text).Contains("partial class @required");
        await Assert.That(result.GeneratedSources.Single().Text).Contains("global::@global.@file.@required");
    }
}
