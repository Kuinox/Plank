using ConsoleAppFramework;
using Plank.Reading;
using Plank.Reading.Logical;

namespace Plank.Tool;

internal static class MergeCommand
{
    /// <summary>Merge matching Parquet files by copying their encoded row groups.</summary>
    /// <param name="output">Destination Parquet path.</param>
    /// <param name="files">Input Parquet paths.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static int Run([Argument] string output, CancellationToken cancellationToken, [Argument] params string[] files)
    {
        string? temporary = null;
        try
        {
            if (files.Length < 2) throw new ArgumentException("Pass at least two input Parquet files.");
            files = files.Select(CommandFiles.Input).ToArray();
            output = CommandFiles.Output(output, files[0], ".merged.parquet");
            temporary = output + $".{Guid.NewGuid():N}.tmp";
            using var reader = new ParquetReader();
            reader.Reset(File.OpenRead(files[0]));
            using var source = new StreamReadSource(File.OpenRead(files[0]));
            using var destination = new ToolWriteSource(new FileStream(temporary, FileMode.CreateNew));
            var merger = reader.Schema.CreateMerger(source, destination);
            foreach (var file in files.Skip(1))
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var next = new StreamReadSource(File.OpenRead(file));
                merger.AppendFile(next);
            }
            merger.CloseFile();
            destination.Close();
            CommandFiles.Publish(temporary, output);
            Console.WriteLine(output);
            return 0;
        }
        catch (Exception error) { return CommandFiles.Error(error); }
        finally { if (temporary is not null) File.Delete(temporary); }
    }
}
