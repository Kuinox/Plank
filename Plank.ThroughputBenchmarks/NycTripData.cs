namespace Plank.Benchmarks;

public sealed class NycTripData
{
    public required int?[] VendorId { get; init; }

    public required DateTime?[] PickupDateTime { get; init; }

    public required DateTime?[] DropoffDateTime { get; init; }

    public required long?[] PassengerCount { get; init; }

    public required double?[] TripDistance { get; init; }

    public required long?[] RatecodeId { get; init; }

    public required string?[] StoreAndFwdFlag { get; init; }

    public required int?[] PuLocationId { get; init; }

    public required int?[] DoLocationId { get; init; }

    public required long?[] PaymentType { get; init; }

    public required double?[] FareAmount { get; init; }

    public required double?[] Extra { get; init; }

    public required double?[] MtaTax { get; init; }

    public required double?[] TipAmount { get; init; }

    public required double?[] TollsAmount { get; init; }

    public required double?[] ImprovementSurcharge { get; init; }

    public required double?[] TotalAmount { get; init; }

    public required double?[] CongestionSurcharge { get; init; }

    public required double?[] AirportFee { get; init; }

    public int RowCount => VendorId.Length;
}
