using System.Diagnostics;

namespace Plank.Fuzzing.Replay;

// Replays fuzz inputs through the targets in-process, without AFL and without
// SharpFuzz instrumentation.
//
// Why this exists: the fleet reports AFL's own bitmap coverage, which says how
// much of the *instrumented edge space* it has hit — a number that keeps
// climbing while entire decoders sit at zero. The only way to tell which Plank
// code a corpus actually reaches is to run a coverage profiler over it, and a
// profiler cannot attach to the AFL harness (it is a fork server, and the
// assemblies are already rewritten by SharpFuzz). So this project references
// the same targets from a plain console app that a profiler can wrap:
//
//     coverlet <replay dll> --target dotnet --targetargs "<replay dll> reader <corpus>" \
//         --format cobertura --include "[Plank]*"
//
// Exceptions are swallowed by design: a corpus is mostly malformed files, and
// the point is which code ran, not whether it liked the input.
static class Program
{
    static int Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("usage: Plank.Fuzzing.Replay <reader|writer|both> <dir> [<dir>...]");
            return 2;
        }

        var target = args[0];
        var files = new List<string>();
        foreach (var directory in args[1..])
        {
            if (File.Exists(directory))
            {
                files.Add(directory);
                continue;
            }

            if (!Directory.Exists(directory))
            {
                Console.Error.WriteLine($"no such input: {directory}");
                return 2;
            }

            // AFL queues nest one directory per worker, so recurse; skip the
            // bookkeeping files AFL writes alongside the inputs.
            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                var name = Path.GetFileName(file);
                if (name.StartsWith('.') || name is "fuzzer_stats" or "fuzz_bitmap" or "plot_data" or "cmdline")
                    continue;
                files.Add(file);
            }
        }

        Console.WriteLine($"replaying {files.Count} inputs through '{target}'");
        var stopwatch = Stopwatch.StartNew();
        var faulted = 0;
        for (var i = 0; i < files.Count; i++)
        {
            byte[] data;
            try
            {
                data = File.ReadAllBytes(files[i]);
            }
            catch (IOException)
            {
                // A live fleet rewrites its queue underneath us; a vanished
                // input is not worth aborting a measurement run over.
                continue;
            }

            if (!Replay(target, data))
                faulted++;

            if ((i + 1) % 2000 == 0)
                Console.WriteLine($"  {i + 1}/{files.Count} ({stopwatch.Elapsed.TotalSeconds:F0}s)");
        }

        Console.WriteLine($"done: {files.Count} inputs, {faulted} threw outside the expected set, "
            + $"{stopwatch.Elapsed.TotalSeconds:F0}s");
        return 0;
    }

    static bool Replay(string target, byte[] data)
    {
        try
        {
            switch (target)
            {
                case "reader":
                    PlankReaderFuzzTarget.Execute(data);
                    break;
                case "writer":
                    PlankWriterFuzzTarget.Execute(data);
                    break;
                case "both":
                    PlankReaderFuzzTarget.Execute(data);
                    PlankWriterFuzzTarget.Execute(data);
                    break;
                default:
                    throw new ArgumentException($"unknown target '{target}'", nameof(target));
            }

            return true;
        }
        catch (ArgumentException)
        {
            throw;
        }
        catch (Exception)
        {
            // Everything else is the corpus doing its job.
            return false;
        }
    }
}
