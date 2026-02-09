namespace Plank.Benchmarks;

public readonly record struct EncodingBenchmarkCase(string Library, string DataType, string Encoding)
{
    public override string ToString()
        => $"{Library}/{DataType}/{Encoding}";
}
