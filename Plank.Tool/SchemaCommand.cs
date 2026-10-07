using ConsoleAppFramework;
using Plank.Reading.Logical;
using System.Text.Json;

namespace Plank.Tool;

internal static class SchemaCommand
{
    /// <summary>Inspect a schema. Omit the format to list supported formats.</summary>
    /// <param name="format">json, tsv, or lines (one column path per line).</param>
    /// <param name="file">Input Parquet file.</param>
    public static int Run([Argument] string? format = null, [Argument] string? file = null)
    {
        try
        {
            if (format is null) { Console.WriteLine("Supported formats: json, tsv, lines\nUsage: plank schema <format> <file>"); return 0; }
            format = format.ToLowerInvariant();
            if (format is not ("json" or "tsv" or "lines" or "line")) throw new ArgumentException("Supported formats: json, tsv, lines");
            if (file is null) throw new ArgumentException("A Parquet file is required after the format.");
            using var reader = new ParquetReader();
            reader.Reset(File.OpenRead(CommandFiles.Input(file)));
            var rows = reader.Schema.LeafColumns.Select(c => new { id = c.Ordinal, column_path = c.Path,
                physical_type = c.PhysicalType.ToString(), logical_type = c.LogicalType?.ToString(),
                nullable = c.MaxDefinitionLevel > 0, definition_level = c.MaxDefinitionLevel,
                repetition_level = c.MaxRepetitionLevel, field_id = c.FieldId }).ToArray();
            if (format == "json") Console.WriteLine(JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true }));
            else if (format is "lines" or "line") foreach (var row in rows) Console.WriteLine(row.column_path);
            else
            {
                static string Escape(object? value) => (value?.ToString() ?? "").Replace("\\", "\\\\").Replace("\t", "\\t").Replace("\r", "\\r").Replace("\n", "\\n");
                Console.WriteLine("id\tcolumn_path\tphysical_type\tlogical_type\tnullable\tdefinition_level\trepetition_level\tfield_id");
                foreach (var row in rows) Console.WriteLine(string.Join('\t', new object?[] { row.id, row.column_path, row.physical_type, row.logical_type, row.nullable, row.definition_level, row.repetition_level, row.field_id }.Select(Escape)));
            }
            return 0;
        }
        catch (Exception error) { return CommandFiles.Error(error); }
    }
}
