namespace Plank.Benchmarks.EncodingRegression;

/// <summary>Identifies one encoder path: a physical type, an encoding, and a repetition.</summary>
public readonly record struct EncodingRegressionCase(string DataType, string Encoding, string Repetition)
{
    public string Id
        => $"{DataType}/{Encoding}/{Repetition}";

    public override string ToString()
        => Id;
}
