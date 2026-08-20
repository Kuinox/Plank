using Plank.Fuzzing.Harness;
using SharpFuzz;

namespace Plank.Fuzzing.Target;

static class Program
{
    static void Main(string[] args)
    {
        // Seed generation lives behind the target, the same way the reader
        // target's does, so it is built and versioned with the decoder it has to
        // stay in step with.
        if (args is ["--generate-corpus", var directory])
        {
            // Generation runs through the instrumented assemblies like everything
            // else in this process, so the coverage bitmap has to exist first.
            AflPersistentHarness.PinDummyCoverageBuffer();
            var count = CrossWriterCorpusGenerator.Generate(directory);
            Console.WriteLine($"wrote {count} cross-writer seeds to {directory}");
            return;
        }

        if (Environment.GetEnvironmentVariable("FUZZ_OOP") == "1")
        {
            Fuzzer.OutOfProcess.Run(stream =>
            {
                using var buffer = new MemoryStream();
                stream.CopyTo(buffer);
                PlankWriterFuzzTarget.Execute(buffer.ToArray());
            });
        }
        else
        {
            AflPersistentHarness.Run("writer", data => PlankWriterFuzzTarget.Execute(data));
        }
    }
}
