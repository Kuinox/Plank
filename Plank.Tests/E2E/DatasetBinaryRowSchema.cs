using Plank.Schema;

namespace Plank.Tests.E2E;

[ParquetSchema]
internal sealed partial class DatasetBinaryRowSchema
{
    internal byte[] Path = [];
    internal bool ThrowOnTail;

    public int Id { get; set; }
    public byte[] Payload { get; set; } = [];
    public byte[]? OptionalPayload { get; set; }
    public ReadOnlyMemory<byte> Memory { get; set; }
    public ReadOnlyMemory<byte>? OptionalMemory { get; set; }

    public int Tail
    {
        get => ThrowOnTail ? throw new InvalidOperationException("Test getter failure.") : 0;
        set { }
    }
}
