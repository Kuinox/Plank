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

    internal static GeneratorDriverRunResult RunAfterUnrelatedEdit(params SourceFile[] sources)
    {
        var syntaxTrees = sources.Select(source =>
                CSharpSyntaxTree.ParseText(source.Text, ParseOptions, source.Path, Encoding.UTF8))
            .ToList();
        var unrelatedTree = CSharpSyntaxTree.ParseText(
            "internal static class Unrelated { internal const int Value = 1; }",
            ParseOptions, "Unrelated.cs", Encoding.UTF8);
        syntaxTrees.Add(unrelatedTree);
        var compilation = CSharpCompilation.Create(
            "GeneratorIncrementalTests",
            syntaxTrees,
            MetadataReferences,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                allowUnsafe: true,
                nullableContextOptions: NullableContextOptions.Enable));
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            [new global::Plank.SourceGen.ParquetRowGenerator().AsSourceGenerator()],
            parseOptions: ParseOptions,
            driverOptions: new GeneratorDriverOptions(
                IncrementalGeneratorOutputKind.None, trackIncrementalGeneratorSteps: true));

        driver = driver.RunGenerators(compilation);
        var editedTree = CSharpSyntaxTree.ParseText(
            "internal static class Unrelated { internal const int Value = 2; }",
            ParseOptions, "Unrelated.cs", Encoding.UTF8);
        compilation = compilation.ReplaceSyntaxTree(unrelatedTree, editedTree);
        driver = driver.RunGenerators(compilation);
        return driver.GetRunResult();
    }

    internal static GeneratorDriverRunResult RunAfterSchemaEdit(SourceFile initialSource,
        SourceFile updatedSource, params SourceFile[] unchangedSources)
    {
        var initialTree = CSharpSyntaxTree.ParseText(
            initialSource.Text, ParseOptions, initialSource.Path, Encoding.UTF8);
        var syntaxTrees = unchangedSources
            .Select(source => CSharpSyntaxTree.ParseText(source.Text, ParseOptions, source.Path, Encoding.UTF8))
            .Prepend(initialTree);
        var compilation = CSharpCompilation.Create(
            "GeneratorIncrementalTests",
            syntaxTrees,
            MetadataReferences,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                allowUnsafe: true,
                nullableContextOptions: NullableContextOptions.Enable));
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            [new global::Plank.SourceGen.ParquetRowGenerator().AsSourceGenerator()],
            parseOptions: ParseOptions,
            driverOptions: new GeneratorDriverOptions(
                IncrementalGeneratorOutputKind.None, trackIncrementalGeneratorSteps: true));

        driver = driver.RunGenerators(compilation);
        var updatedTree = CSharpSyntaxTree.ParseText(
            updatedSource.Text, ParseOptions, updatedSource.Path, Encoding.UTF8);
        compilation = compilation.ReplaceSyntaxTree(initialTree, updatedTree);
        driver = driver.RunGenerators(compilation);
        return driver.GetRunResult();
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
