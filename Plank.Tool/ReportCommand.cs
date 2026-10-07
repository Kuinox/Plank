using ConsoleAppFramework;
using Plank.Reading.Logical;
using Plank.Reading.Physical;
using System.Net;
using Plank.Schema;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace Plank.Tool;

internal static class ReportCommand
{
    /// <summary>Generate a self-contained interactive HTML encoding report.</summary>
    /// <param name="file">Encoding-profile Parquet file.</param>
    /// <param name="output">HTML destination; omitted means INPUT.html.</param>
    /// <param name="title">Optional report title.</param>
    /// <param name="source">Optional original Parquet file to supply physical types for older profiles.</param>
    public static int Run([Argument] string file, [Argument] string? output = null, string? title = null, string? source = null)
    {
        string? temporary = null;
        try
        {
            file = CommandFiles.Input(file); output = CommandFiles.Output(output, file, ".html");
            var rows = ReadRows(file);
            if (source is not null)
            {
                using var original = new ParquetReader(); original.Reset(File.OpenRead(CommandFiles.Input(source)));
                var types = original.Schema.LeafColumns.ToDictionary(c => c.Path, c => c.PhysicalType.ToString());
                foreach (var row in rows)
                    if ((!row.TryGetValue("physical_type", out var value) || value is null) &&
                        row.TryGetValue("column_path", out var path) && path is string name && types.TryGetValue(name, out var type))
                        row["physical_type"] = type;
            }
            string[] required = ["column_path", "encoding", "compression", "original_size_bytes", "compressed_size_bytes", "write_time_ns", "read_time_ns"];
            if (rows.Count == 0 || required.Any(f => !rows[0].ContainsKey(f))) throw new ArgumentException("Expected a non-empty encoding-profile Parquet file.");
            using var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("Plank.Tool.ReportViewer.html")!;
            using var reader = new StreamReader(resource); var html = reader.ReadToEnd();
            using var physical = new ParquetFileReader(); physical.Reset(File.OpenRead(file));
            var metadata = new Dictionary<string, string>();
            for (var i = 0; i < physical.Metadata.KeyValueMetadataCount; i++)
                metadata[Encoding.UTF8.GetString(physical.Metadata.KeyValueMetadataKeyUtf8(i))] = Encoding.UTF8.GetString(physical.Metadata.KeyValueMetadataValueUtf8(i));
            if (metadata.TryGetValue("plank.profile.timing", out var timing))
            {
                var details = "<details class=\"run-details\"><summary>Benchmark details</summary><p>" + WebUtility.HtmlEncode(timing);
                if (metadata.TryGetValue("plank.profile.iterations", out var iterations)) details += " · Measured iterations: " + WebUtility.HtmlEncode(iterations);
                if (metadata.TryGetValue("plank.profile.warmup", out var warmup)) details += " · Warmups: " + WebUtility.HtmlEncode(warmup);
                details += "</p></details>";
                html = html.Replace("<section id=\"drop\"", details + "<section id=\"drop\"");
            }
            var data = JsonSerializer.Serialize(new { name = title ?? Path.GetFileNameWithoutExtension(file), rows });
            html = html.Replace("<script type=\"module\">", "<script id=\"profile-data\" type=\"application/json\">" + data + "</script><script type=\"module\">");
            if (!html.Contains("id=\"profile-data\"")) throw new InvalidOperationException("Viewer template did not accept profile data.");
            temporary = output + $".{Guid.NewGuid():N}.tmp";
            File.WriteAllText(temporary, html);
            CommandFiles.Publish(temporary, output); Console.WriteLine(output); return 0;
        }
        catch (Exception error) { return CommandFiles.Error(error); }
        finally { if (temporary is not null) File.Delete(temporary); }
    }

    internal static List<Dictionary<string, object?>> ReadRows(string file)
    {
        using var reader = new ParquetReader(); reader.Reset(File.OpenRead(file));
        var result = new List<Dictionary<string, object?>>();
        foreach (var group in reader.RowGroups)
        {
            var rows = Enumerable.Range(0, checked((int)group.RowCount)).Select(_ => new Dictionary<string, object?>()).ToArray();
            foreach (var column in reader.Schema.LeafColumns)
            {
                var ordinal = 0;
                if (column.PhysicalType is ParquetPhysicalType.ByteArray or ParquetPhysicalType.FixedLenByteArray)
                    foreach (var buffer in group.Column<byte>(column))
                        for (var i = 0; i < buffer.Count; i++) rows[ordinal++][column.Path] = buffer.IsNull(i) ? null : Encoding.UTF8.GetString(buffer.GetValue(i));
                else
                {
                    object?[] values = column.PhysicalType switch
                    {
                        ParquetPhysicalType.Int64 => column.MaxDefinitionLevel > 0 ? Numeric<long?>(group, column) : Numeric<long>(group, column),
                        ParquetPhysicalType.Int32 => column.MaxDefinitionLevel > 0 ? Numeric<int?>(group, column) : Numeric<int>(group, column),
                        ParquetPhysicalType.Double => column.MaxDefinitionLevel > 0 ? Numeric<double?>(group, column) : Numeric<double>(group, column),
                        _ => throw new NotSupportedException($"Unsupported report field type {column.PhysicalType}")
                    };
                    foreach (var value in values) rows[ordinal++][column.Path] = value;
                }
                if (ordinal != rows.Length) throw new InvalidDataException("Report column has an inconsistent row count.");
            }
            result.AddRange(rows);
        }
        return result;
    }
    static object?[] Numeric<T>(RowGroup group, LeafColumn column)
    {
        var values = new List<object?>();
        foreach (var buffer in group.Column<T>(column)) foreach (var value in buffer.Values) values.Add(value);
        return values.ToArray();
    }
}
