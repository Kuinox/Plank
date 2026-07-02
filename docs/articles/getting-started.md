# Getting started

Plank targets modern .NET and currently builds from source. Reference the `Plank` project from your application while the library is in development.

```xml
<ProjectReference Include="..\Plank\Plank.csproj" />
```

Define a row model with `[ParquetSchema]`. The source generator creates schema helpers and row writer types for the model.

```csharp
using Plank.Schema;

[ParquetSchema]
public sealed partial class EventSchema
{
    public int Id { get; init; }

    public string Name { get; init; } = string.Empty;

    public DateTimeOffset OccurredAt { get; init; }
}
```

Write rows through the generated row API:

```csharp
using var stream = File.Create("events.parquet");
var rowWriter = EventSchema.CreateRowWriter(stream);

var row = rowWriter.GetRow();
row.Id = 1;
row.Name = "created";
row.OccurredAt = DateTimeOffset.UtcNow;
rowWriter.Next();

rowWriter.Complete();
```

The repository sample project contains complete row and column API examples:

```pwsh
dotnet run --project .\Samples\Plank.Sample\Plank.Sample.csproj
```

## Build the docs locally

Restore the repo-local DocFX tool and build the site:

```pwsh
dotnet tool restore
dotnet docfx .\docs\docfx.json --serve
```

DocFX writes the static site to `docs/_site`.
