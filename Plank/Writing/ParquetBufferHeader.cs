namespace Plank.Writing;

unsafe struct ParquetBufferHeader
{
    internal int ReferenceCount;
    internal int Capacity;
    internal int BucketIndex;
    internal int AllocationByteLength;
    internal nint Allocation;
    internal ParquetBufferHeader* NextFree;
}
