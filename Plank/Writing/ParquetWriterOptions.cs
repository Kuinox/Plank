using Plank;

namespace Plank.Writing;

public sealed class ParquetWriterOptions
{
    public static readonly ParquetWriterOptions Default = new();

    public IParquetLog Log { get; init; } = ParquetLog.None;

    public uint? ExpectedRowGroupCount { get; init; }

    public uint? RowGroupRowCountHint { get; init; }

    public RowGroupOptions RowGroupOptions { get; init; } = RowGroupOptions.Default;

    public int FooterBufferBytes { get; init; } = 64 * 1024;

    public IBufferPool? BufferPool { get; init; }

    public CompressionKind Compression { get; init; } = CompressionKind.None;

    public DateTimeKindHandling DateTimeKindHandling { get; init; } = DateTimeKindHandling.RequireUtc;
}
