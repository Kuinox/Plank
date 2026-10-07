using ConsoleAppFramework;
using Plank.Tool;

var app = ConsoleApp.Create();
app.Add("profile rowgroups", RowGroupProfileCommand.Run);
await app.RunAsync(args);
