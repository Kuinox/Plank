using System.Collections.Immutable;
using Plank.Reading;
using Plank.Reading.Row;
using Plank.Reading.Typed;
using Plank.Schema;

namespace Plank.Fuzzing;

public static class PlankReaderFuzzTarget
{
    static readonly ParquetSchema[] Schemas = BuildSchemas();

    public static void Execute(ReadOnlySpan<byte> data)
    {
        var schemaIndex = data.IsEmpty ? 0 : data[0] % Schemas.Length;
        var fileBytes = data.IsEmpty ? [] : data[1..].ToArray();

        var schema = Schemas[schemaIndex];
        var source = new MemoryReadSource(fileBytes);

        try
        {
            using var reader = schema.CreateReader(source);
            foreach (var token in reader.EnumerateRowGroups())
            {
                using var rowGroup = reader.OpenRowGroup(token);
                foreach (var column in reader.Schema.Columns)
                    DrainColumn(rowGroup, column);
            }
        }
        catch (Exception ex) when (ex is CorruptParquetException or NotSupportedException or InvalidOperationException) { }
    }

    public static Exception? GetHandledException(byte[] data)
    {
        var schemaIndex = data.Length == 0 ? 0 : data[0] % Schemas.Length;
        var fileBytes = data.Length == 0 ? [] : data[1..];
        var schema = Schemas[schemaIndex];
        var source = new MemoryReadSource(fileBytes);
        try
        {
            using var reader = schema.CreateReader(source);
            foreach (var token in reader.EnumerateRowGroups())
            {
                using var rowGroup = reader.OpenRowGroup(token);
                foreach (var column in reader.Schema.Columns)
                    DrainColumn(rowGroup, column);
            }
            return null;
        }
        catch (Exception ex) when (ex is CorruptParquetException or NotSupportedException or InvalidOperationException)
        {
            return ex;
        }
    }

    static void DrainColumn(RowGroupReader rowGroup, Column column)
    {
        switch (column.PhysicalType)
        {
            case ParquetPhysicalType.Boolean:
                DrainPages(rowGroup.Column<bool>(column).Pages);
                break;
            case ParquetPhysicalType.Int32:
                DrainPages(rowGroup.Column<int>(column).Pages);
                break;
            case ParquetPhysicalType.Int64:
                DrainPages(rowGroup.Column<long>(column).Pages);
                break;
            case ParquetPhysicalType.Double:
                DrainPages(rowGroup.Column<double>(column).Pages);
                break;
            case ParquetPhysicalType.ByteArray:
                DrainPages(rowGroup.Column<byte[]>(column).Pages);
                break;
        }
    }

    static void DrainPages<T>(ColumnPageEnumerable<T> pages)
    {
        foreach (var page in pages)
        {
            var span = page.Values.Span;
            for (var i = 0; i < span.Length; i++)
                _ = span[i];
        }
    }

    static ParquetSchema[] BuildSchemas()
        =>
        [
            // 0: single int32 plain
            Schema(Col("c0", ParquetPhysicalType.Int32, EncodingKind.Plain)),
            // 1: single byte[] plain
            Schema(Col("c0", ParquetPhysicalType.ByteArray, EncodingKind.Plain)),
            // 2: int32 + bool
            Schema(Col("c0", ParquetPhysicalType.Int32, EncodingKind.DeltaBinaryPacked),
                   Col("c1", ParquetPhysicalType.Boolean, EncodingKind.Plain)),
            // 3: int64 + double
            Schema(Col("c0", ParquetPhysicalType.Int64, EncodingKind.Plain),
                   Col("c1", ParquetPhysicalType.Double, EncodingKind.Plain)),
            // 4: int32 rle-dict
            Schema(Col("c0", ParquetPhysicalType.Int32, EncodingKind.RleDictionary)),
            // 5: byte[] delta-length
            Schema(Col("c0", ParquetPhysicalType.ByteArray, EncodingKind.DeltaLengthByteArray)),
            // 6: all five types, plain
            Schema(Col("c0", ParquetPhysicalType.Boolean, EncodingKind.Plain),
                   Col("c1", ParquetPhysicalType.Int32, EncodingKind.Plain),
                   Col("c2", ParquetPhysicalType.Int64, EncodingKind.Plain),
                   Col("c3", ParquetPhysicalType.Double, EncodingKind.Plain),
                   Col("c4", ParquetPhysicalType.ByteArray, EncodingKind.Plain)),
            // 7: byte[] delta-byte-array
            Schema(Col("c0", ParquetPhysicalType.ByteArray, EncodingKind.DeltaByteArray)),
        ];

    static ParquetSchema Schema(params Column[] columns)
        => new(columns.ToImmutableArray());

    static Column Col(string name, ParquetPhysicalType type, EncodingKind encoding)
        => new(name, type, new ColumnOptions(encodings: ImmutableArray.Create(encoding)));
}
