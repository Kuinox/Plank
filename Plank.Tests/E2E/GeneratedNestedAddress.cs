namespace Plank.Tests.E2E;

internal sealed record GeneratedNestedAddress
{
    public int Zip { get; init; }

    public byte Rank { get; init; }

    public string City { get; init; } = string.Empty;

    public Guid Token { get; init; }
}
