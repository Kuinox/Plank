namespace Plank.Reading;

public interface IParquetReadSource
{
    ulong Length { get; }

    void ReadExactly(ulong offset, Span<byte> destination);
}
