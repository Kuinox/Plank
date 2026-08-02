using System.Collections.Immutable;
using Apache.Arrow;
using Apache.Arrow.Types;
using Plank.Schema;
using ArrowTimeUnit = Apache.Arrow.Types.TimeUnit;
using ParquetTimeUnit = Plank.Schema.TimeUnit;

namespace Plank.Arrow;

/// <summary>Converts flat Apache Arrow schemas to and from Plank schemas.</summary>
public static class ArrowSchemaConverter
{
    /// <summary>Creates a flat Plank schema for the supported fields in an Apache Arrow schema.</summary>
    /// <exception cref="NotSupportedException">A field uses an Arrow type that the adapter cannot represent.</exception>
    public static ParquetSchema ToParquetSchema(Apache.Arrow.Schema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);

        var definitions = ImmutableArray.CreateBuilder<ColumnDefinition>(schema.FieldsList.Count);
        for (var i = 0; i < schema.FieldsList.Count; i++)
            definitions.Add(ToParquetColumn(schema.FieldsList[i]));
        return new ParquetSchema(definitions.MoveToImmutable());
    }

    /// <summary>Creates an Apache Arrow schema for the supported flat leaves in a Plank schema.</summary>
    /// <remarks>Nested paths are flattened to their dot-separated Plank leaf paths.</remarks>
    /// <exception cref="NotSupportedException">A column uses repetition or a type that the adapter cannot represent.</exception>
    public static Apache.Arrow.Schema ToArrowSchema(ParquetSchema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);

        var fields = new Field[schema.LeafColumns.Length];
        for (var i = 0; i < fields.Length; i++)
        {
            var leaf = schema.LeafColumns[i];
            if (leaf.Options.Repetition == ParquetRepetition.Repeated)
                throw Unsupported(leaf.Path, "repeated columns");

            fields[i] = new Field(leaf.Path, ToArrowType(leaf),
                leaf.Options.Repetition == ParquetRepetition.Optional);
        }

        return new Apache.Arrow.Schema(fields, metadata: null);
    }

    internal static void EnsureEquivalent(Apache.Arrow.Schema expected, Apache.Arrow.Schema actual)
    {
        if (actual.FieldsList.Count != expected.FieldsList.Count)
            throw new ArgumentException(
                $"Arrow schema has {actual.FieldsList.Count} fields; expected {expected.FieldsList.Count}.",
                nameof(actual));

        for (var i = 0; i < expected.FieldsList.Count; i++)
        {
            var expectedField = expected.FieldsList[i];
            var actualField = actual.FieldsList[i];
            if (!string.Equals(expectedField.Name, actualField.Name, StringComparison.Ordinal))
                throw new ArgumentException(
                    $"Arrow field {i} is named '{actualField.Name}'; expected '{expectedField.Name}'.",
                    nameof(actual));
            if (expectedField.IsNullable != actualField.IsNullable)
                throw new ArgumentException(
                    $"Arrow field '{actualField.Name}' has nullable={actualField.IsNullable}; expected nullable={expectedField.IsNullable}.",
                    nameof(actual));
            if (!TypesEqual(expectedField.DataType, actualField.DataType))
                throw new ArgumentException(
                    $"Arrow field '{actualField.Name}' has type '{Describe(actualField.DataType)}'; expected '{Describe(expectedField.DataType)}'.",
                    nameof(actual));
        }
    }

    static ColumnDefinition ToParquetColumn(Field field)
    {
        var physicalType = ParquetPhysicalType.Int32;
        LogicalType? logicalType = null;
        uint typeLength = 0;

        switch (field.DataType)
        {
            case BooleanType:
                physicalType = ParquetPhysicalType.Boolean;
                break;
            case Int8Type:
                logicalType = new LogicalType.Int(8, isSigned: true);
                break;
            case Int16Type:
                logicalType = new LogicalType.Int(16, isSigned: true);
                break;
            case Int32Type:
                logicalType = new LogicalType.Int(32, isSigned: true);
                break;
            case Int64Type:
                physicalType = ParquetPhysicalType.Int64;
                logicalType = new LogicalType.Int(64, isSigned: true);
                break;
            case UInt8Type:
                logicalType = new LogicalType.Int(8, isSigned: false);
                break;
            case UInt16Type:
                logicalType = new LogicalType.Int(16, isSigned: false);
                break;
            case UInt32Type:
                logicalType = new LogicalType.Int(32, isSigned: false);
                break;
            case UInt64Type:
                physicalType = ParquetPhysicalType.Int64;
                logicalType = new LogicalType.Int(64, isSigned: false);
                break;
            case FloatType:
                physicalType = ParquetPhysicalType.Float;
                break;
            case DoubleType:
                physicalType = ParquetPhysicalType.Double;
                break;
            case StringType:
                physicalType = ParquetPhysicalType.ByteArray;
                logicalType = new LogicalType.String();
                break;
            case BinaryType:
                physicalType = ParquetPhysicalType.ByteArray;
                break;
            case FixedSizeBinaryType fixedBinary:
                physicalType = ParquetPhysicalType.FixedLenByteArray;
                typeLength = checked((uint)fixedBinary.ByteWidth);
                break;
            case GuidType:
                physicalType = ParquetPhysicalType.FixedLenByteArray;
                logicalType = new LogicalType.Uuid();
                typeLength = 16;
                break;
            case Date32Type:
                logicalType = new LogicalType.Date();
                break;
            case Time32Type time32 when time32.Unit == ArrowTimeUnit.Millisecond:
                logicalType = new LogicalType.Time(ParquetTimeUnit.Millis, IsAdjustedToUtc: false);
                break;
            case Time64Type time64 when time64.Unit == ArrowTimeUnit.Microsecond:
                physicalType = ParquetPhysicalType.Int64;
                logicalType = new LogicalType.Time(ParquetTimeUnit.Micros, IsAdjustedToUtc: false);
                break;
            case Time64Type time64 when time64.Unit == ArrowTimeUnit.Nanosecond:
                physicalType = ParquetPhysicalType.Int64;
                logicalType = new LogicalType.Time(ParquetTimeUnit.Nanos, IsAdjustedToUtc: false);
                break;
            case TimestampType timestamp:
                physicalType = ParquetPhysicalType.Int64;
                logicalType = new LogicalType.Timestamp(ToParquetTimeUnit(field.Name, timestamp.Unit),
                    timestamp.IsTimeZoneAware);
                break;
            default:
                throw Unsupported(field.Name, $"Arrow type '{Describe(field.DataType)}'");
        }

        var repetition = field.IsNullable ? ParquetRepetition.Optional : ParquetRepetition.Required;
        var options = new ColumnOptions(repetition, typeLength: typeLength);
        return field.IsNullable
            ? ColumnDefinition.OptionalLeaf(field.Name, physicalType, options, logicalType)
            : ColumnDefinition.RequiredLeaf(field.Name, physicalType, options, logicalType);
    }

    static IArrowType ToArrowType(LeafColumn leaf)
        => leaf.PhysicalType switch
        {
            ParquetPhysicalType.Boolean when leaf.LogicalType is null => BooleanType.Default,
            ParquetPhysicalType.Int32 => ToInt32ArrowType(leaf),
            ParquetPhysicalType.Int64 => ToInt64ArrowType(leaf),
            ParquetPhysicalType.Float when leaf.LogicalType is null => FloatType.Default,
            ParquetPhysicalType.Double when leaf.LogicalType is null => DoubleType.Default,
            ParquetPhysicalType.ByteArray when leaf.LogicalType is null => BinaryType.Default,
            ParquetPhysicalType.ByteArray when leaf.LogicalType is LogicalType.String => StringType.Default,
            ParquetPhysicalType.FixedLenByteArray when leaf.LogicalType is null && leaf.Options.TypeLength > 0 =>
                new FixedSizeBinaryType(checked((int)leaf.Options.TypeLength)),
            ParquetPhysicalType.FixedLenByteArray when leaf.LogicalType is LogicalType.Uuid &&
                                                       leaf.Options.TypeLength == 16 => GuidType.Default,
            _ => throw Unsupported(leaf.Path,
                $"physical type '{leaf.PhysicalType}' with logical type '{leaf.LogicalType?.GetType().Name ?? "none"}'")
        };

    static IArrowType ToInt32ArrowType(LeafColumn leaf)
        => leaf.LogicalType switch
        {
            null => Int32Type.Default,
            LogicalType.Int { BitWidth: 8, IsSigned: true } => Int8Type.Default,
            LogicalType.Int { BitWidth: 16, IsSigned: true } => Int16Type.Default,
            LogicalType.Int { BitWidth: 32, IsSigned: true } => Int32Type.Default,
            LogicalType.Int { BitWidth: 8, IsSigned: false } => UInt8Type.Default,
            LogicalType.Int { BitWidth: 16, IsSigned: false } => UInt16Type.Default,
            LogicalType.Int { BitWidth: 32, IsSigned: false } => UInt32Type.Default,
            LogicalType.Date => Date32Type.Default,
            LogicalType.Time { Unit: ParquetTimeUnit.Millis, IsAdjustedToUtc: false } =>
                new Time32Type(ArrowTimeUnit.Millisecond),
            _ => throw Unsupported(leaf.Path, $"Int32 logical type '{leaf.LogicalType.GetType().Name}'")
        };

    static IArrowType ToInt64ArrowType(LeafColumn leaf)
        => leaf.LogicalType switch
        {
            null => Int64Type.Default,
            LogicalType.Int { BitWidth: 64, IsSigned: true } => Int64Type.Default,
            LogicalType.Int { BitWidth: 64, IsSigned: false } => UInt64Type.Default,
            LogicalType.Time { Unit: ParquetTimeUnit.Micros, IsAdjustedToUtc: false } =>
                new Time64Type(ArrowTimeUnit.Microsecond),
            LogicalType.Time { Unit: ParquetTimeUnit.Nanos, IsAdjustedToUtc: false } =>
                new Time64Type(ArrowTimeUnit.Nanosecond),
            LogicalType.Timestamp timestamp => new TimestampType(ToArrowTimeUnit(timestamp.Unit),
                timestamp.IsAdjustedToUtc ? "UTC" : null),
            _ => throw Unsupported(leaf.Path, $"Int64 logical type '{leaf.LogicalType.GetType().Name}'")
        };

    static ParquetTimeUnit ToParquetTimeUnit(string name, ArrowTimeUnit unit)
        => unit switch
        {
            ArrowTimeUnit.Millisecond => ParquetTimeUnit.Millis,
            ArrowTimeUnit.Microsecond => ParquetTimeUnit.Micros,
            ArrowTimeUnit.Nanosecond => ParquetTimeUnit.Nanos,
            _ => throw Unsupported(name, $"timestamp unit '{unit}'")
        };

    static ArrowTimeUnit ToArrowTimeUnit(ParquetTimeUnit unit)
        => unit switch
        {
            ParquetTimeUnit.Millis => ArrowTimeUnit.Millisecond,
            ParquetTimeUnit.Micros => ArrowTimeUnit.Microsecond,
            ParquetTimeUnit.Nanos => ArrowTimeUnit.Nanosecond,
            _ => throw new ArgumentOutOfRangeException(nameof(unit), unit, "Unknown Parquet time unit.")
        };

    static bool TypesEqual(IArrowType left, IArrowType right)
    {
        if (left.GetType() != right.GetType())
            return false;
        return (left, right) switch
        {
            (FixedSizeBinaryType x, FixedSizeBinaryType y) => x.ByteWidth == y.ByteWidth,
            (TimestampType x, TimestampType y) => x.Unit == y.Unit &&
                                                   string.Equals(x.Timezone, y.Timezone, StringComparison.Ordinal),
            (Time32Type x, Time32Type y) => x.Unit == y.Unit,
            (Time64Type x, Time64Type y) => x.Unit == y.Unit,
            _ => true
        };
    }

    static string Describe(IArrowType type)
        => type switch
        {
            FixedSizeBinaryType fixedBinary => $"{type.Name}[{fixedBinary.ByteWidth}]",
            TimestampType timestamp => $"{type.Name}[{timestamp.Unit}, {timestamp.Timezone ?? "no timezone"}]",
            Time32Type time32 => $"{type.Name}[{time32.Unit}]",
            Time64Type time64 => $"{type.Name}[{time64.Unit}]",
            _ => type.Name
        };

    static NotSupportedException Unsupported(string name, string detail)
        => new($"Arrow adapter does not support {detail} for field or column '{name}'.");
}
