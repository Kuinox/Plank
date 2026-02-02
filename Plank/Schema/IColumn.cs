namespace Plank;

public interface IColumn
{
    string Name { get; }

    int Ordinal { get; }

    ParquetPhysicalType PhysicalType { get; }

    ParquetRepetition Repetition { get; }

    EncodingKind[] Encodings { get; }

    Type ClrType { get; }
}
