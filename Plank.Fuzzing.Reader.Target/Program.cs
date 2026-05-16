using SharpFuzz;

namespace Plank.Fuzzing.Reader.Target;

static class Program
{
    static void Main()
        => Fuzzer.Run(stream =>
        {
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            PlankReaderFuzzTarget.Execute(buffer.ToArray());
        });
}
