using System.Globalization;
using System.Text;
using Plank.Reading.Logical;
using Plank.Schema;

namespace Plank.Sample;

// This reader has no dependency on a generated schema or the sample writer.
public static class UnknownSchemaReader
{
    public static void Dump(Stream input, TextWriter output)
    {
        using var reader = new ParquetReader();
        reader.Reset(input);
        foreach (var group in reader.RowGroups)
            foreach (var column in reader.Schema.LeafColumns)
            {
                if (column.MaxRepetitionLevel != 0 || column.MaxDefinitionLevel > 1)
                    throw new NotSupportedException($"Nested column '{column.Path}' needs NestedColumn<T> and level handling.");

                output.WriteLine($"Column: {column.Path}");
                // Logical types must be considered before physical storage types:
                // reading a timestamp as long would print its encoded units.
                switch (column.LogicalType)
                {
                    case LogicalType.Date: WriteValues<DateOnly>(group, column, output); continue;
                    case LogicalType.Time: WriteValues<TimeOnly>(group, column, output); continue;
                    case LogicalType.Timestamp: WriteValues<DateTime>(group, column, output); continue;
                    case LogicalType.Decimal: WriteValues<decimal>(group, column, output); continue;
                    case LogicalType.Uuid: WriteValues<Guid>(group, column, output); continue;
                    case LogicalType.Int { IsSigned: false, BitWidth: 64 }:
                        WriteValues<ulong>(group, column, output); continue;
                    case LogicalType.Int { IsSigned: false }:
                        WriteValues<uint>(group, column, output); continue;
                }

                switch (column.PhysicalType)
                {
                    case ParquetPhysicalType.Boolean: WriteValues<bool>(group, column, output); break;
                    case ParquetPhysicalType.Int32: WriteValues<int>(group, column, output); break;
                    case ParquetPhysicalType.Int64: WriteValues<long>(group, column, output); break;
                    case ParquetPhysicalType.Float: WriteValues<float>(group, column, output); break;
                    case ParquetPhysicalType.Double: WriteValues<double>(group, column, output); break;
                    default: WriteBinary(group, column, output); break;
                }
            }
    }

    static void WriteValues<T>(RowGroup group, LeafColumn column, TextWriter output) where T : struct
    {
        // An optional ancestor can make a required leaf nullable too.
        if (column.MaxDefinitionLevel > 0)
        {
            foreach (var buffer in group.Column<T?>(column))
                foreach (var value in buffer.Values)
                    output.WriteLine(value is { } present ? Format(present) : "<null>");
        }
        else
        {
            foreach (var buffer in group.Column<T>(column))
                foreach (var value in buffer.Values)
                    output.WriteLine(Format(value));
        }
    }

    static string Format<T>(T value) where T : struct
        => value is DateTime time ? time.ToString("O", CultureInfo.InvariantCulture)
            : Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";

    static void WriteBinary(RowGroup group, LeafColumn column, TextWriter output)
    {
        foreach (var buffer in group.Column<byte>(column))
            for (var i = 0; i < buffer.Count; i++)
            {
                if (buffer.IsNull(i))
                    output.WriteLine("<null>");
                else
                    output.WriteLine(column.LogicalType is LogicalType.String or LogicalType.Json or LogicalType.Enum
                        ? Encoding.UTF8.GetString(buffer.GetValue(i))
                        : Convert.ToHexString(buffer.GetValue(i)));
            }
    }
}
