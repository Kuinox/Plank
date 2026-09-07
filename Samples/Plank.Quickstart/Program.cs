using System.Text;
using Plank.Schema;

var path = Path.Combine(Path.GetTempPath(), $"plank-quickstart-{Guid.NewGuid():N}.parquet");
try
{
    using (var stream = File.Create(path))
    using (var writer = EventSchema.CreateRowWriter(stream))
    {
        for (var id = 1; id <= 3; id++)
        {
            var row = writer.GetRow();
            row.Id = id;
            row.Name = id switch { 1 => "created"u8.ToArray(), 2 => null, _ => [] };
        }
        writer.Complete();
    }

    using var input = File.OpenRead(path);
    using var reader = EventSchema.CreateRowReader(input);
    var count = 0;
    foreach (var row in reader)
    {
        string? name = row.Name.IsNull ? null : Encoding.UTF8.GetString(row.Name.Value);
        Console.WriteLine($"{row.Id}: {name ?? "<null>"}");
        count++;
        if (row.Id != count || name != (count switch { 1 => "created", 2 => null, _ => "" }))
            throw new InvalidOperationException("The round-trip changed a row.");
    }
    if (count != 3)
        throw new InvalidOperationException($"Expected 3 rows, read {count}.");
}
finally
{
    File.Delete(path);
}

[ParquetSchema]
public sealed partial class EventSchema
{
    public int Id { get; init; }
    public byte[]? Name { get; init; }
}
