using SharpFuzz;

namespace Plank.Fuzzing.Target;

static class Program
{
    static void Main()
        => Fuzzer.OutOfProcess.Run(stream =>
        {
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            PlankWriterFuzzTarget.Execute(buffer.ToArray());
        });
}
