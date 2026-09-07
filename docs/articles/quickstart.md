# First round-trip

Install the .NET 10 SDK, then create an application:

```sh
dotnet new console --framework net10.0 --name PlankDemo
cd PlankDemo
dotnet add package Plank
```

Replace the entire `Program.cs` with the following code. It includes every required
import and the schema declaration; no sample-project global imports are needed.

[!code-csharp[](../../Samples/Plank.Quickstart/Program.cs)]

Run `dotnet run`. The output is:

```text
1: created
2: <null>
3:
```

The example verifies three rows, including null and empty binary values, and removes
its temporary file. `Complete()` finishes the file before the reader opens it.
The `using` scopes release both readers and writers on failure.

Continue with [schema declarations](schema.md), [row writing](writing/rows.md),
and [row reading](reading/rows.md). Those articles show fragments to use inside
your application; the example above is a complete application.
