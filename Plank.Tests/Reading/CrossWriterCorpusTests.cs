using Plank.Fuzzing;

namespace Plank.Tests.Reading;

/// <summary>
/// Runs the cross-writer fuzzer's seed corpus: Apache Arrow writes the file,
/// Plank has to read it back value for value.
/// </summary>
/// <remarks>
/// The seeds are a fuzzer's starting corpus, but they are worth a test in their
/// own right. Everything else in the suite that reads a generated file reads one
/// Plank wrote, so the reader is only ever checked against its own writer's
/// habits — and the parquet-testing corpus showed what that misses. These cover
/// the corners of Arrow's writer options that decide a file's shape: page index
/// present or absent, statistics and their truncation, dictionary, each codec,
/// both data page versions, forced encodings, pages small enough to split a
/// column across several.
///
/// A failure here means Plank cannot read a file a mainstream writer produces,
/// which is a defect however the values were chosen. Keeping it green is also
/// what keeps the fuzzer usable: AFL refuses a seed that already fails, so a
/// seed that stops reading takes the whole target off the fleet.
/// </remarks>
internal sealed class CrossWriterCorpusTests
{
    [Test]
    [MethodDataSource(nameof(Seeds))]
    public void Seed_ReadsBackWhatArrowWrote(string name, byte[] seed)
    {
        _ = name;
        PlankWriterFuzzTarget.Execute(seed);
    }

    /// <remarks>
    /// A seed is a list of choice indexes, so a new choice added ahead of an
    /// existing one shifts every index after it and quietly turns the whole
    /// corpus into different files. Checking each plan still says what it was
    /// written to say is what makes that a failure rather than a silent loss of
    /// coverage.
    /// </remarks>
    [Test]
    [MethodDataSource(nameof(Seeds))]
    public async Task Seed_DecodesToThePlanItWasWrittenFor(string name, byte[] seed)
    {
        var expected = CrossWriterCorpusGenerator.BuildCases().Single(c => c.Name == name).Expected;
        var description = PlankCrossWriterFuzzTarget.Decode(seed.AsSpan(1)).Describe();

        foreach (var fragment in expected)
            await Assert.That(description).Contains(fragment);
    }

    public static IEnumerable<(string Name, byte[] Seed)> Seeds()
        => CrossWriterCorpusGenerator.BuildCases().Select(static c => (c.Name, c.Seed));
}
