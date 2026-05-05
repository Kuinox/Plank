namespace Plank.Benchmarks;

public readonly record struct EncodingBenchmarkCase(string DataType, string EncodingName)
{
    public override string ToString()
        => $"{DataType}/{EncodingName}";
}
