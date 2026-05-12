namespace Plank.Reading;

public interface IParquetReadSource
{
    long Length { get; }

    void ReadExactly(long offset, Span<byte> destination);
}
