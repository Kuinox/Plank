using System.Reflection;

namespace Plank.Tests.Reading.ParquetTesting;

/// <summary>
/// Locates the apache/parquet-testing submodule and enumerates the files in it.
/// </summary>
/// <remarks>
/// Every other reader fixture in this repository was written by Plank's own writer or
/// minimised out of its own fuzzer, so the whole suite only ever proved that Plank can
/// read what Plank wrote. parquet-testing is the format's shared conformance corpus:
/// files produced by parquet-mr, Impala, Spark, arrow-cpp and arrow-rs, carrying
/// encodings, codecs and logical types Plank's writer cannot emit at all.
/// </remarks>
static class ParquetTestingCorpus
{
    /// <summary>Absolute path of the submodule checkout, baked in at build time.</summary>
    public static string Root { get; } =
        typeof(ParquetTestingCorpus).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(attribute => attribute.Key == "ParquetTestingRoot")
            .Value!;

    /// <summary>
    /// False when the submodule has not been checked out. The clone leaves the directory
    /// present but empty, so the marker is a file inside it rather than the directory.
    /// </summary>
    public static bool IsAvailable => File.Exists(Path.Combine(Root, "README.md"));

    public const string MissingMessage =
        "The apache/parquet-testing submodule is not checked out. Run: git submodule update --init third_party/parquet-testing";

    /// <summary>Every Parquet file in the corpus, including the encrypted and malformed ones.</summary>
    public static string[] AllFiles()
        => Enumerate(".", "*.parquet").Concat(Enumerate(".", "*.parquet.encrypted")).Order(StringComparer.Ordinal).ToArray();

    /// <summary>The well-formed sample files. Excludes the encrypted ones, which Plank cannot decrypt.</summary>
    public static string[] DataFiles()
        => Enumerate("data", "*.parquet");

    /// <summary>Reproducers for files that broke another implementation.</summary>
    public static string[] BadDataFiles()
        => Enumerate("bad_data", "*.parquet");

    /// <summary>Resolves a corpus-relative path to an absolute one.</summary>
    public static string Resolve(string relativePath)
        => Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));

    public static byte[] ReadAllBytes(string relativePath)
        => File.ReadAllBytes(Resolve(relativePath));

    // Relative paths with forward slashes, so a test name reads the same on every
    // platform and matches how the corpus documents its own files.
    static string[] Enumerate(string directory, string pattern)
    {
        var root = Path.Combine(Root, directory);
        if (!Directory.Exists(root))
            return [];

        return Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(Root, path).Replace(Path.DirectorySeparatorChar, '/'))
            .Order(StringComparer.Ordinal)
            .ToArray();
    }
}
