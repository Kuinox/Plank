using Plank.Fuzzing.Harness;
using SharpFuzz;

namespace Plank.Fuzzing.Reader.Target;

static class Program
{
    static void Main(string[] args)
    {
        // Seed generation lives behind the target rather than in its own project
        // so it is built and versioned with the code it has to stay in step with.
        if (args is ["--generate-corpus", var directory])
        {
            // Generation runs through the instrumented assemblies like everything
            // else in this process, so the coverage bitmap has to exist first.
            AflPersistentHarness.PinDummyCoverageBuffer();
            var count = CorpusGenerator.Generate(directory);
            Console.WriteLine($"wrote {count} seeds to {directory}");
            return;
        }

        if (Environment.GetEnvironmentVariable("FUZZ_OOP") == "1")
        {
            Fuzzer.OutOfProcess.Run(stream =>
            {
                using var buffer = new MemoryStream();
                stream.CopyTo(buffer);
                PlankReaderFuzzTarget.Execute(buffer.ToArray());
            });
        }
        else if (Environment.GetEnvironmentVariable("FUZZ_SINGLE") == "1")
        {
            Fuzzer.RunOnce(stream =>
            {
                using var buffer = new MemoryStream();
                stream.CopyTo(buffer);
                PlankReaderFuzzTarget.Execute(buffer.ToArray());
            });
        }
        else
        {
            AflPersistentHarness.Run("reader", data => PlankReaderFuzzTarget.Execute(data));
        }
    }
}
