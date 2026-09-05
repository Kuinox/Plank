namespace Plank.Sample;

static class Program
{
    static void Main()
    {
        var paths = new List<string>();
        try
        {
            paths.Add(ColumnApiSample.Run());
            paths.Add(RowApiSample.Run());
            DecimalApiSample.Run();
            DatasetApiSample.Run();
            Console.WriteLine("All documentation samples passed.");
        }
        finally
        {
            foreach (var path in paths)
                File.Delete(path);
        }
    }
}
