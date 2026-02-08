using ParquetSharp;
using Plank.Schema;
using SchemaColumn = Plank.Schema.Column;

namespace Plank.Reading;

public sealed class ParquetReader : IDisposable
{
    Stream _stream;
    ParquetFileReader _reader;
    readonly ParquetSchema _schema;
    readonly Dictionary<SchemaColumn, int> _columnOrdinals;

    ParquetReader(Stream stream, ParquetSchema schema)
    {
        _stream = stream;
        _schema = schema;
        _reader = new ParquetFileReader(stream);
        _columnOrdinals = BuildColumnOrdinals(schema);
        ValidateSchemaCompatibility();
    }

    public static ParquetReader Create(Stream stream, ParquetSchema schema)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(schema);
        return new ParquetReader(stream, schema);
    }

    public int RowGroupCount
        => checked((int)_reader.FileMetaData.NumRowGroups);

    public RowGroupReader StartRowGroup(int rowGroupIndex)
    {
        if ((uint)rowGroupIndex >= (uint)RowGroupCount)
            throw new ArgumentOutOfRangeException(nameof(rowGroupIndex), rowGroupIndex, "Row group index is out of range.");

        return new RowGroupReader(this, _reader.RowGroup(rowGroupIndex));
    }

    public void Reset(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        _reader.Dispose();
        _stream = stream;
        _reader = new ParquetFileReader(stream);
        ValidateSchemaCompatibility();
    }

    public void Dispose()
    {
        _reader.Dispose();
        _stream.Dispose();
    }

    internal int ResolveOrdinal(SchemaColumn column)
    {
        ArgumentNullException.ThrowIfNull(column);
        if (!_columnOrdinals.TryGetValue(column, out var ordinal))
            throw new ArgumentException("Column does not belong to this schema.", nameof(column));

        return ordinal;
    }

    static Dictionary<SchemaColumn, int> BuildColumnOrdinals(ParquetSchema schema)
    {
        if (schema.Columns.IsDefault || schema.Columns.Length == 0)
            return new Dictionary<SchemaColumn, int>(ReferenceEqualityComparer.Instance);

        var ordinals = new Dictionary<SchemaColumn, int>(schema.Columns.Length, ReferenceEqualityComparer.Instance);
        for (var i = 0; i < schema.Columns.Length; i++)
            ordinals[schema.Columns[i]] = i;

        return ordinals;
    }

    void ValidateSchemaCompatibility()
    {
        var fileColumnCount = _reader.FileMetaData.Schema.NumColumns;
        var schemaColumnCount = _schema.Columns.IsDefault ? 0 : _schema.Columns.Length;
        if (fileColumnCount != schemaColumnCount)
            throw new InvalidOperationException($"Schema column count mismatch. Expected {schemaColumnCount}, file has {fileColumnCount}.");
    }
}
