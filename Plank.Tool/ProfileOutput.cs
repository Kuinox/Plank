using Plank.Schema;
using Plank.Writing;

namespace Plank.Tool;

internal static class ProfileOutput
{
    internal sealed record Field(string Name, ParquetPhysicalType Type, bool Optional = false);
    internal static void Write(string output, IReadOnlyList<Dictionary<string, object?>> rows, Field[] fields, string kind, string version, string timing, IReadOnlyList<ParquetKeyValueMetadata>? extraMetadata = null)
    {
        var schema = new ParquetSchema([..fields.Select(f => f.Optional
            ? ColumnDefinition.OptionalLeaf(f.Name, f.Type, logicalType: f.Type == ParquetPhysicalType.ByteArray ? new LogicalType.String() : null)
            : ColumnDefinition.RequiredLeaf(f.Name, f.Type, logicalType: f.Type == ParquetPhysicalType.ByteArray ? new LogicalType.String() : null))]);
        var temporary = output + $".{Guid.NewGuid():N}.tmp";
        try
        {
            using (var writer = schema.CreateWriter(new FileStream(temporary, FileMode.CreateNew), new ParquetWriterOptions
            {
                Compression = CompressionKind.Zstd,
                KeyValueMetadata = [new("plank.profile.kind", kind), new("plank.profile.version", version), new("plank.profile.timing", timing), ..extraMetadata ?? []]
            }))
            {
                if (rows.Count > 0)
                {
                    var group = writer.StartRowGroup();
                    for (var i = 0; i < fields.Length; i++)
                    {
                        var f = fields[i]; var column = schema.LeafColumns[i];
                        if (f.Type == ParquetPhysicalType.ByteArray)
                        {
                            var serialized = group.CreateSerializedColumn<string>(column);
                            serialized.Serialize(rows.Select(r => r[f.Name]?.ToString()!).ToArray()); group.Write(serialized);
                        }
                        else if (f.Type == ParquetPhysicalType.Double)
                        {
                            var serialized = group.CreateSerializedColumn<double>(column);
                            serialized.Serialize(rows.Select(r => Convert.ToDouble(r[f.Name])).ToArray()); group.Write(serialized);
                        }
                        else if (f.Type == ParquetPhysicalType.Int32)
                        {
                            if (f.Optional)
                            {
                                var serialized = group.CreateSerializedColumn<int?>(column);
                                serialized.Serialize(rows.Select(r => r[f.Name] is null ? (int?)null : Convert.ToInt32(r[f.Name])).ToArray()); group.Write(serialized);
                            }
                            else
                            {
                                var serialized = group.CreateSerializedColumn<int>(column);
                                serialized.Serialize(rows.Select(r => Convert.ToInt32(r[f.Name])).ToArray()); group.Write(serialized);
                            }
                        }
                        else if (f.Optional)
                        {
                            var serialized = group.CreateSerializedColumn<long?>(column);
                            serialized.Serialize(rows.Select(r => r[f.Name] is null ? (long?)null : Convert.ToInt64(r[f.Name])).ToArray()); group.Write(serialized);
                        }
                        else
                        {
                            var serialized = group.CreateSerializedColumn<long>(column);
                            serialized.Serialize(rows.Select(r => Convert.ToInt64(r[f.Name])).ToArray()); group.Write(serialized);
                        }
                    }
                }
                writer.CloseFile();
            }
            CommandFiles.Publish(temporary, output);
        }
        finally { File.Delete(temporary); }
    }
}
