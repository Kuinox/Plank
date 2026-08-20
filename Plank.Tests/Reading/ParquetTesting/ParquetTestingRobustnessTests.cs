using Plank.Fuzzing;

namespace Plank.Tests.Reading.ParquetTesting;

/// <summary>
/// Drives every Parquet file in the apache/parquet-testing corpus through the reader fuzz
/// target and requires it to either read the file or fail with an exception the reader
/// documents.
/// </summary>
/// <remarks>
/// This is the broad sweep, and it is deliberately outcome-blind: unlike
/// <see cref="ParquetTestingCompatibilityTests"/> it does not care whether a file reads,
/// only that failing to read it is not a crash. That lets it cover the parts of the corpus
/// there is no point recording a per-file expectation for -- the 100-odd shredded variant
/// cases, the encrypted files Plank has no key for, and bad_data, whose whole purpose is to
/// be rejected.
///
/// Selector byte 0 binds the file's own schema, which is the only way these files can be
/// read at all: they carry types and encodings no fixed requested schema names.
/// </remarks>
internal sealed class ParquetTestingRobustnessTests
{
    [Test]
    [MethodDataSource(nameof(AllFiles))]
    public void CorpusFile_FailsCleanlyOrReads(string relativePath)
    {
        var file = ParquetTestingCorpus.ReadAllBytes(relativePath);

        // A file whose footer promises gigabytes is not something to decode four times on
        // every test run. The footer parse is the part worth sweeping here anyway -- it is
        // the code that has to survive the declared size without overflowing -- and
        // ParquetTestingCompatibilityTests covers that file specifically.
        if (ParquetTestingProbe.IsStressCase(file))
        {
            ParquetTestingProbe.Open(file);
            return;
        }

        // Every selector the target understands for a file input, so one corpus file
        // covers the columnar reader, the row API, CRC verification and the streaming
        // read source rather than only the default path.
        foreach (var selector in Selectors)
        {
            var input = new byte[file.Length + 1];
            input[0] = selector;
            file.CopyTo(input, 1);

            // Execute swallows exactly the exception set a malformed file is allowed to
            // produce; anything else -- an IndexOutOfRangeException, an overflow, a
            // NullReferenceException -- escapes and fails the test.
            PlankReaderFuzzTarget.Execute(input);
        }
    }

    // 0x00 columnar, 0x02 row API, 0x10 CRC verification on, 0x40 stream read source.
    // Bit 0 would swap in a fixed requested schema and bit 5 relaxes strict binding;
    // neither is about the file's own content, which is what the corpus is for.
    static readonly byte[] Selectors = [0x00, 0x02, 0x10, 0x40];

    public static string[] AllFiles()
        => ParquetTestingCorpus.IsAvailable ? ParquetTestingCorpus.AllFiles() : [];
}
