using Plank.Reading;

namespace Plank.Writing;

static class ParquetSourceIdentity
{
    internal static bool AreSame(IParquetReadSource source, IParquetWriteSource destination)
        => ReferenceEquals(source, destination) ||
            destination is StreamParquetSource { Stream: { } destinationStream } &&
            ReferenceEquals(source switch
            {
                StreamReadSource reader => reader.Stream,
                StreamParquetSource writer => writer.Stream,
                _ => null
            }, destinationStream);
}
