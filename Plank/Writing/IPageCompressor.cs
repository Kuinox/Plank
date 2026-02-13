namespace Plank.Writing;

interface IPageCompressor : IDisposable
{
    bool UsesStreamingOutput { get; }
    int Compress(ReadOnlySpan<byte> source, Span<byte> destination);
    int Compress(ReadOnlySpan<byte> source, GrowableBufferWriter destination);
}
