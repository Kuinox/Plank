using Plank.Fuzzing;

namespace Plank.Tests.Fuzzing;

internal sealed class PlankReaderFuzzTargetTests
{
    static string FixturesDir
        => Path.Combine(AppContext.BaseDirectory, "Fuzzing", "Fixtures");

    public static IEnumerable<string> FuzzFixtures()
        => Directory.GetFiles(FixturesDir, "*.parquet")
                    .Select(Path.GetFileNameWithoutExtension)!;

    [Test]
    [MethodDataSource(nameof(FuzzFixtures))]
    public void DoesNotCrash(string fixture)
        => PlankReaderFuzzTarget.Execute(
            File.ReadAllBytes(Path.Combine(FixturesDir, fixture + ".parquet")));

    [Test]
    public void EmptyInputDoesNotCrash()
        => PlankReaderFuzzTarget.Execute([]);

    [Test]
    public void AllZeroInputDoesNotCrash()
        => PlankReaderFuzzTarget.Execute(new byte[64]);

    [Test]
    public void TruncatedMagicDoesNotCrash()
        => PlankReaderFuzzTarget.Execute([0x00, 0x50, 0x41, 0x52, 0x31]);

    [Test]
    public void AllGeneratedSeedsAreReadableWithoutCrash()
    {
        foreach (var (name, bytes) in ReaderSeedGenerator.AllSeeds())
        {
            try
            {
                PlankReaderFuzzTarget.Execute(bytes);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Reader seed '{name}' caused an unexpected crash.", ex);
            }
        }
    }

    [Test]
    [Explicit]
    public void WriteReaderCorpusSeeds()
        => ReaderSeedGenerator.WriteSeeds();
}
