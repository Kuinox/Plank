using System.Buffers;

namespace Plank;

public interface IBufferPool
{
    void Register(string name, int bufferLength, int initialCount);

    IMemoryOwner<byte> Rent(string name, int minimumLength);
}
