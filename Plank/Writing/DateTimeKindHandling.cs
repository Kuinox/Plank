namespace Plank.Writing;

[Flags]
public enum DateTimeKindHandling
{
    None = 0,
    RequireUtc = 1 << 0,
    ConvertLocalToUtc = 1 << 1,
    AssumeUnspecifiedAsUtc = 1 << 2,
    PreserveClockTime = 1 << 3
}
