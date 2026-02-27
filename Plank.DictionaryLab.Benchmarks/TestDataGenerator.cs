using System.Text;

namespace Plank.DictionaryLab.Benchmarks;

public static class TestDataGenerator
{
    public static string[] CreateShuffledValues(int rows, int uniqueCount)
    {
        var uniques = new string[uniqueCount];
        for (var i = 0; i < uniqueCount; i++)
            uniques[i] = $"value-{i}";

        var values = new string[rows];
        for (var i = 0; i < rows; i++)
            values[i] = uniques[i % uniqueCount];

        var random = new Random(42);
        for (var i = values.Length - 1; i > 0; i--)
        {
            var j = random.Next(i + 1);
            (values[i], values[j]) = (values[j], values[i]);
        }

        return values;
    }

    public static ReadOnlyMemory<byte>[] CreateShuffledUtf8Values(int rows, int uniqueCount)
    {
        var uniques = new ReadOnlyMemory<byte>[uniqueCount];
        for (var i = 0; i < uniqueCount; i++)
            uniques[i] = Encoding.UTF8.GetBytes($"value-{i}");

        var values = new ReadOnlyMemory<byte>[rows];
        for (var i = 0; i < rows; i++)
            values[i] = uniques[i % uniqueCount];

        var random = new Random(42);
        for (var i = values.Length - 1; i > 0; i--)
        {
            var j = random.Next(i + 1);
            (values[i], values[j]) = (values[j], values[i]);
        }

        return values;
    }
}
