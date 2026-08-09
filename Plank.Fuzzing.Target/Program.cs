using SharpFuzz;

namespace Plank.Fuzzing.Target;

static class Program
{
    static void Main()
    {
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
            AflPersistentHarness.Run(data => PlankWriterFuzzTarget.Execute(data));
        }
    }
}
