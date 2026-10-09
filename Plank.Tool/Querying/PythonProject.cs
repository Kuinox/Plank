using System.Diagnostics;
using System.Reflection;
using System.Text.Json;

namespace Plank.Tool.Querying;

internal sealed class PythonProject(string directory, string python)
{
    internal static readonly IReadOnlyDictionary<string, string> Packages = new Dictionary<string, string>
    {
        ["duckdb"] = "duckdb", ["polars"] = "polars", ["datafusion"] = "datafusion",
        ["clickhouse"] = "chdb", ["spark"] = "pyspark"
    };

    internal static string[] Engines(string selection)
    {
        var names = selection.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.ToLowerInvariant()).Distinct().ToArray();
        if (names.Length == 0 || names.Any(x => !Packages.ContainsKey(x)))
            throw new ArgumentException("Choose engines from duckdb, polars, datafusion, clickhouse, spark.");
        return names;
    }

    internal static async Task<PythonProject> Ensure(string? workerDirectory, string[] engines, CancellationToken token)
    {
        if (OperatingSystem.IsWindows() && engines.Contains("clickhouse"))
            throw new InvalidOperationException("Embedded ClickHouse (chDB) requires Linux or macOS.");
        if (engines.Contains("spark"))
        {
            try { await Process("java", ["-version"], token); }
            catch (Exception error) when (error is not OperationCanceledException)
            { throw new InvalidOperationException("Spark requires an installed Java runtime on PATH.", error); }
        }
        var directory = Path.GetFullPath(workerDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Plank", "query-worker-v1"));
        Directory.CreateDirectory(directory);
        await using var setupLock = await Lock(Path.Combine(directory, ".setup.lock"), token);
        var python = Path.Combine(directory, ".venv", OperatingSystem.IsWindows() ? "Scripts/python.exe" : "bin/python");
        if (!File.Exists(python))
        {
            var found = false;
            foreach (var command in OperatingSystem.IsWindows() ? new[] { "py", "python", "python3" } : new[] { "python3", "python" })
            {
                var prefix = command == "py" ? new[] { "-3" } : Array.Empty<string>();
                try
                {
                    await Process(command, [..prefix, "-c", "import sys,venv; assert sys.version_info >= (3,10), 'Python 3.10+ required'"], token);
                    await Process(command, [..prefix, "-m", "venv", Path.Combine(directory, ".venv")], token, echo: true);
                    found = true;
                    break;
                }
                catch (Exception error) when (error is not OperationCanceledException)
                { Console.Error.WriteLine($"Python candidate {command}: {error.Message}"); }
            }
            if (!found) throw new InvalidOperationException("Python 3.10+ with venv must be available on PATH.");
        }
        try { await Process(python, ["-m", "pip", "--version"], token); }
        catch (Exception error) when (error is not OperationCanceledException)
        { await Process(python, ["-m", "ensurepip", "--upgrade"], token, echo: true); }
        var packages = new[] { "fastparquet", "pyarrow" }.Concat(engines.Select(x => Packages[x])).Distinct().ToArray();
        var missing = new List<string>();
        foreach (var package in packages)
        {
            try { await Process(python, ["-c", "import importlib.metadata,sys; print(importlib.metadata.version(sys.argv[1]))", package], token); }
            catch (Exception error) when (error is not OperationCanceledException) { missing.Add(package); }
        }
        if (missing.Count > 0)
        {
            Console.Error.WriteLine($"Installing Python packages: {string.Join(", ", missing)}");
            await Process(python, ["-m", "pip", "install", "--disable-pip-version-check", ..missing], token, echo: true);
        }
        var frozen = await Process(python, ["-m", "pip", "freeze", "--disable-pip-version-check"], token);
        await File.WriteAllTextAsync(Path.Combine(directory, "requirements.lock.txt"), frozen, token);
        var dependencies = frozen.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        await File.WriteAllTextAsync(Path.Combine(directory, "pyproject.toml"),
            "[project]\nname = \"plank-query-worker\"\nversion = \"1.0.0\"\nrequires-python = \">=3.10\"\ndependencies = [\n" +
            string.Join(",\n", dependencies.Select(x => "  " + JsonSerializer.Serialize(x))) + "\n]\n", token);
        var assembly = Assembly.GetExecutingAssembly();
        foreach (var name in new[] { "worker.py", "repack.py", "query.py", "mini_s3.py", "network.py", "request_report.html" })
        {
            using var resource = assembly.GetManifestResourceStream($"Plank.Tool.Querying.{name}")!;
            using var reader = new StreamReader(resource);
            var contents = await reader.ReadToEndAsync(token);
            var path = Path.Combine(directory, name);
            if (!File.Exists(path) || await File.ReadAllTextAsync(path, token) != contents)
            {
                var temp = path + ".tmp";
                await File.WriteAllTextAsync(temp, contents, token);
                File.Move(temp, path, overwrite: true);
            }
        }
        return new PythonProject(directory, python);
    }

    internal async Task<string> Run(object request, CancellationToken token)
    {
        // Keep the setup lock while executing so another tool cannot replace a running worker's modules.
        await using var workerLock = await Lock(Path.Combine(directory, ".setup.lock"), token);
        var path = Path.Combine(directory, $"request-{Guid.NewGuid():N}.json");
        try
        {
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(request), token);
            return await Process(python, [Path.Combine(directory, "worker.py"), path], token, streamError: true);
        }
        finally { File.Delete(path); }
    }

    static async Task<FileStream> Lock(string path, CancellationToken token)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) { await Task.Delay(200, token); }
        }
    }

    internal static async Task<string> Process(string executable, string[] arguments, CancellationToken token,
        bool echo = false, bool streamError = false)
    {
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        info.Environment.Remove("PYTHONPATH");
        info.Environment.Remove("PYTHONHOME");
        info.Environment["PYTHONNOUSERSITE"] = "1";
        info.Environment["PYTHONUNBUFFERED"] = "1";
        using var process = System.Diagnostics.Process.Start(info) ?? throw new InvalidOperationException($"Unable to start {executable}.");
        using var registration = token.Register(() => { try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } });
        async Task<string> Read(StreamReader reader, bool display)
        {
            var text = new System.Text.StringBuilder();
            while (await reader.ReadLineAsync(token) is { } line)
            {
                text.AppendLine(line);
                if (display) Console.Error.WriteLine(line);
            }
            return text.ToString();
        }
        var output = Read(process.StandardOutput, echo);
        var error = Read(process.StandardError, echo || streamError);
        try { await System.Threading.Tasks.Task.WhenAll(output, error, process.WaitForExitAsync(token)); }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            await process.WaitForExitAsync();
            throw;
        }
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"{Path.GetFileName(executable)} exited with code {process.ExitCode}: {(await error).Trim()}");
        return await output;
    }
}
