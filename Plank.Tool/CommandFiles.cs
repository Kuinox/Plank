using System.Text;
using Plank.Writing;

namespace Plank.Tool;

internal static class CommandFiles
{
    internal static string Input(string path)
    {
        path = Path.GetFullPath(path);
        if (!File.Exists(path)) throw new FileNotFoundException("Input file not found.", path);
        return path;
    }
    internal static string Output(string? output, string input, string suffix)
    {
        var path = Path.GetFullPath(output ?? Path.ChangeExtension(input, suffix));
        if (File.Exists(path) || Directory.Exists(path)) throw new IOException($"Output already exists: {path}");
        if (!Directory.Exists(Path.GetDirectoryName(path))) throw new DirectoryNotFoundException("Output parent directory does not exist.");
        return path;
    }
    internal static int Error(Exception error)
    {
        if (error is System.Reflection.TargetInvocationException { InnerException: { } inner }) error = inner;
        Console.Error.WriteLine(error.Message);
        return error is OperationCanceledException ? 130 : 1;
    }
    internal static void Publish(string temporary, string output) => File.Move(temporary, output, overwrite: false);
}

internal sealed class ToolWriteSource(Stream stream, bool leaveOpen = false) : IParquetWriteSource
{
    public void Open(ReadOnlySpan<byte> path, FileMode mode) => throw new NotSupportedException();
    public void Close() { if (!leaveOpen) stream.Dispose(); }
    public void Dispose() => Close();
    public void Write(ulong offset, ReadOnlySpan<byte> bytes) { stream.Position = checked((long)offset); stream.Write(bytes); }
    public void SetLength(ulong length) => stream.SetLength(checked((long)length));
    public void Flush() => stream.Flush();
}
