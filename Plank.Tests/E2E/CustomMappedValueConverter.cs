using Plank.Schema;

namespace Plank.Tests.E2E;

internal sealed class CustomMappedValueConverter : ParquetValueConverter<CustomMappedValue, int>
{
    public override int ConvertToPhysical(CustomMappedValue value)
        => value.Value;

    public override CustomMappedValue ConvertFromPhysical(int value)
        => new(value);
}
