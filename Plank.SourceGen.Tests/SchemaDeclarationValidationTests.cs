using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Plank.SourceGen.Tests;

internal sealed class SchemaDeclarationValidationTests
{
    [Test]
    [Arguments("file partial class Data", "file-local")]
    [Arguments("internal class Data", "partial")]
    public async Task UnsupportedTopLevelDeclarationReportsFocusedDiagnostic(string declaration, string reason)
    {
        var source = $$"""
            using Plank.Schema;
            [ParquetSchema]
            {{declaration}} { public int Value { get; set; } }
            """;
        await AssertDiagnostic(GeneratorTestHarness.Run(new GeneratorTestHarness.SourceFile("Data.cs", source)), reason);
    }

    [Test]
    public async Task OldLanguageVersionReportsFocusedDiagnostic()
    {
        const string source = """
            using Plank.Schema;
            [ParquetSchema]
            internal partial class Data { public int Value { get; set; } }
            """;
        await AssertDiagnostic(GeneratorTestHarness.RunWithLanguageVersion(LanguageVersion.CSharp11, new GeneratorTestHarness.SourceFile("Data.cs", source)), "C# 12");
    }

    [Test]
    public async Task CSharp12AndDisabledNullableAnnotationsStillCompile()
    {
        const string source = """
            #nullable disable
            using Plank.Schema;
            namespace Regression;
            [ParquetSchema(AllowAllocatingValues = true)]
            internal partial record Data(int Id, string Name, int[] Values);
            """;
        var result = GeneratorTestHarness.RunWithLanguageVersion(LanguageVersion.CSharp12, new GeneratorTestHarness.SourceFile("Data.cs", source));
        await Assert.That(result.GeneratorDiagnostics).IsEmpty();
        await Assert.That(result.GeneratedSources).HasSingleItem();
        await Assert.That(result.CompilationDiagnostics.Where(static d => d.Severity == DiagnosticSeverity.Error)).IsEmpty();
    }

    static async Task AssertDiagnostic(GeneratorTestHarness.RunResult result, string message)
    {
        await Assert.That(result.GeneratorDiagnostics).HasSingleItem();
        var diagnostic = result.GeneratorDiagnostics.Single();
        await Assert.That(diagnostic.Id).IsEqualTo("PLANKGEN003");
        await Assert.That(diagnostic.GetMessage(CultureInfo.InvariantCulture)).Contains(message);
        await Assert.That(result.GeneratedSources).IsEmpty();
        await Assert.That(result.CompilationDiagnostics.Where(static d => d.Severity == DiagnosticSeverity.Error)).IsEmpty();
    }
}
