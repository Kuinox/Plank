using SharpFuzz;

namespace Plank.Fuzzing.Reader.Target;

static class Program
{
    static void Main()
        => Fuzzer.OutOfProcess.Run(stream =>
        {
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            PlankReaderFuzzTarget.Execute(buffer.ToArray());
        });
}
