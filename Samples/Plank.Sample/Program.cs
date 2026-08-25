namespace Plank.Sample;

static class Program
{
    static void Main()
    {
        var columnPath = ColumnApiSample.Run();
        Console.WriteLine($"Column API sample wrote: {columnPath}");

        var rowPath = RowApiSample.Run();
        Console.WriteLine($"Row API sample wrote: {rowPath}");
    }
}
