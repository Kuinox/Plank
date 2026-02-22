namespace Plank.Benchmarks;

public readonly record struct SingleColumnScenario(string DataType, string EncodingName)
{
    public override string ToString()
        => $"{DataType}|{EncodingName}";

    public static bool TryParse(string value, out SingleColumnScenario scenario)
    {
        var separator = value.IndexOf('|');
        if (separator <= 0 || separator >= value.Length - 1)
        {
            scenario = default;
            return false;
        }

        var dataType = value[..separator].Trim();
        var encodingName = value[(separator + 1)..].Trim();
        if (dataType.Length == 0 || encodingName.Length == 0)
        {
            scenario = default;
            return false;
        }

        scenario = new SingleColumnScenario(dataType, encodingName);
        return true;
    }
}
