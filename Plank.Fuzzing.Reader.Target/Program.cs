using SharpFuzz;

namespace Plank.Fuzzing.Reader.Target;

static class Program
{
    static void Main()
    {
        var action = (Stream stream) =>
        {
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            PlankReaderFuzzTarget.Execute(buffer.ToArray());
        };

        if (Environment.GetEnvironmentVariable("FUZZ_OOP") == "1")
            Fuzzer.OutOfProcess.Run(action);
        else
            Fuzzer.Run(action);
    }
}
