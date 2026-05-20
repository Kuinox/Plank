using SharpFuzz;

namespace Plank.Fuzzing.Reader.Target;

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
            AflPersistentHarness.Run(data => PlankReaderFuzzTarget.Execute(data));
        }
    }
}
