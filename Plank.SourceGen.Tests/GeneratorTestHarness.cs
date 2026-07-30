using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Plank.Schema;

namespace Plank.SourceGen.Tests;

internal static class GeneratorTestHarness
{
    static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Preview);
    static readonly ImmutableArray<MetadataReference> MetadataReferences = CreateMetadataReferences();

    internal static RunResult Run(params SourceFile[] sources)
    {
        var syntaxTrees = sources.Select(source =>
            CSharpSyntaxTree.ParseText(source.Text, ParseOptions, source.Path, Encoding.UTF8));
        var compilation = CSharpCompilation.Create(
            "GeneratorTests",
            syntaxTrees,
            MetadataReferences,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                allowUnsafe: true,
                nullableContextOptions: NullableContextOptions.Enable));
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            [new global::Plank.SourceGen.ParquetRowGenerator().AsSourceGenerator()],
            parseOptions: ParseOptions);

        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var outputCompilation, out _);
        var runResult = driver.GetRunResult();

        return new RunResult(
            runResult.Diagnostics,
            outputCompilation.GetDiagnostics(),
            runResult.Results
                .SelectMany(result => result.GeneratedSources)
                .Select(source => new GeneratedSource(source.HintName, source.SourceText.ToString()))
                .ToImmutableArray(),
            runResult.Results
                .Where(result => result.Exception is not null)
                .Select(result => result.Exception!)
                .ToImmutableArray());
    }

    static ImmutableArray<MetadataReference> CreateMetadataReferences()
    {
        var trustedPlatformAssemblies = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")
            ?? throw new InvalidOperationException("The trusted platform assembly list is unavailable.");
        var paths = trustedPlatformAssemblies
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Append(typeof(ParquetSchemaAttribute).Assembly.Location)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase);

        return paths
            .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
            .ToImmutableArray();
    }

    internal readonly record struct SourceFile(string Path, string Text);

    internal readonly record struct GeneratedSource(string HintName, string Text);

    internal sealed class RunResult(
        ImmutableArray<Diagnostic> generatorDiagnostics,
        ImmutableArray<Diagnostic> compilationDiagnostics,
        ImmutableArray<GeneratedSource> generatedSources,
        ImmutableArray<Exception> generatorExceptions)
    {
        internal readonly ImmutableArray<Diagnostic> GeneratorDiagnostics = generatorDiagnostics;
        internal readonly ImmutableArray<Diagnostic> CompilationDiagnostics = compilationDiagnostics;
        internal readonly ImmutableArray<GeneratedSource> GeneratedSources = generatedSources;
        internal readonly ImmutableArray<Exception> GeneratorExceptions = generatorExceptions;
    }
}
