using ConsoleAppFramework;
using Plank.Tool;

var app = ConsoleApp.Create();
app.Add("schema", SchemaCommand.Run);
app.Add("merge", MergeCommand.Run);
app.Add("profile encoding", EncodingProfileCommand.Run);
app.Add("profile query", QueryProfileCommand.Run);
app.Add("report", ReportCommand.Run);
app.Add("profile rowgroups", RowGroupProfileCommand.Run);
await app.RunAsync(args);
