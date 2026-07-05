using Plank.Reading;

namespace Plank.Reading.Physical;

public readonly ref struct ParquetPage
{
    internal ParquetPage(PageHeader header, ReadOnlySpan<byte> payload)
    {
        Header = header;
        Payload = payload;
    }

    public PageHeader Header { get; }

    public ReadOnlySpan<byte> Payload { get; }
}
