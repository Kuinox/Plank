using System.Text;
using Plank.Reading;
using Plank.Reading.Logical;
using Plank.Schema;

namespace Plank.Untyped;

/// <summary>Materializes unknown Parquet schemas as dictionary-based rows.</summary>
public sealed class ParquetUntypedReader : IDisposable
{
    readonly ParquetReader _reader;
    UntypedSchemaPlan? _plan;

    public ParquetUntypedReader(ParquetReaderOptions? options = null)
    {
        _reader = new ParquetReader(options);
    }

    public ParquetSchema Schema
        => _reader.Schema;

    public void Reset(Stream stream)
    {
        _reader.Reset(stream);
        _plan = new UntypedSchemaPlan(_reader.Schema);
    }

    public void Reset(IParquetReadSource source)
    {
        _reader.Reset(source);
        _plan = new UntypedSchemaPlan(_reader.Schema);
    }

    public IReadOnlyList<IReadOnlyDictionary<string, object?>> ReadAll()
    {
        var plan = GetPlan();
        var rowGroups = _reader.RowGroups;
        var result = new List<IReadOnlyDictionary<string, object?>>();
        for (var i = 0; i < rowGroups.Count; i++)
            result.AddRange(ReadRowGroup(plan, rowGroups[i]));
        return result;
    }

    public IReadOnlyList<IReadOnlyDictionary<string, object?>> ReadRowGroup(int rowGroupIndex)
    {
        var plan = GetPlan();
        var rowGroups = _reader.RowGroups;
        if ((uint)rowGroupIndex >= (uint)rowGroups.Count)
            throw new ArgumentOutOfRangeException(nameof(rowGroupIndex), rowGroupIndex,
                "Row group index is outside the file.");
        return ReadRowGroup(plan, rowGroups[rowGroupIndex]);
    }

    public void Dispose()
        => _reader.Dispose();

    static IReadOnlyList<IReadOnlyDictionary<string, object?>> ReadRowGroup(UntypedSchemaPlan plan,
        RowGroup rowGroup)
    {
        var rowCount = checked((int)rowGroup.RowCount);
        var rows = new Dictionary<string, object?>[rowCount];
        for (var i = 0; i < rows.Length; i++)
            rows[i] = new Dictionary<string, object?>(plan.Schema.Definitions.Length, StringComparer.Ordinal);

        var leaves = plan.Leaves;
        for (var i = 0; i < leaves.Length; i++)
            ReadLeaf(plan, rowGroup, leaves[i], rows);

        var result = new IReadOnlyDictionary<string, object?>[rows.Length];
        for (var i = 0; i < rows.Length; i++)
        {
            NormalizeDictionary(rows[i]);
            result[i] = rows[i];
        }
        return result;
    }

    static void ReadLeaf(UntypedSchemaPlan plan, RowGroup rowGroup, UntypedSchemaPlan.LeafPlan leaf,
        Dictionary<string, object?>[] rows)
    {
        var type = GetMaterializedType(leaf.Column);
        if (type == typeof(bool))
            ReadPrimitive<bool>(plan, rowGroup, leaf, rows);
        else if (type == typeof(byte))
            ReadPrimitive<byte>(plan, rowGroup, leaf, rows);
        else if (type == typeof(ushort))
            ReadPrimitive<ushort>(plan, rowGroup, leaf, rows);
        else if (type == typeof(int))
            ReadPrimitive<int>(plan, rowGroup, leaf, rows);
        else if (type == typeof(uint))
            ReadPrimitive<uint>(plan, rowGroup, leaf, rows);
        else if (type == typeof(long))
            ReadPrimitive<long>(plan, rowGroup, leaf, rows);
        else if (type == typeof(ulong))
            ReadPrimitive<ulong>(plan, rowGroup, leaf, rows);
        else if (type == typeof(float))
            ReadPrimitive<float>(plan, rowGroup, leaf, rows);
        else if (type == typeof(double))
            ReadPrimitive<double>(plan, rowGroup, leaf, rows);
        else if (type == typeof(DateOnly))
            ReadPrimitive<DateOnly>(plan, rowGroup, leaf, rows);
        else if (type == typeof(TimeOnly))
            ReadPrimitive<TimeOnly>(plan, rowGroup, leaf, rows);
        else if (type == typeof(DateTime))
            ReadPrimitive<DateTime>(plan, rowGroup, leaf, rows);
        else if (type == typeof(DateTimeOffset))
            ReadPrimitive<DateTimeOffset>(plan, rowGroup, leaf, rows);
        else if (type == typeof(byte[]))
            ReadBytes(plan, rowGroup, leaf, rows);
        else
            throw new NotSupportedException(
                $"Column '{leaf.Column.Path}' cannot be materialized by the untyped adapter as '{type}'.");
    }

    static void ReadPrimitive<T>(UntypedSchemaPlan plan, RowGroup rowGroup, UntypedSchemaPlan.LeafPlan leaf,
        Dictionary<string, object?>[] rows) where T : struct
    {
        var repeatedIndexes = new int[leaf.RepeatedDepth + 1];
        Array.Fill(repeatedIndexes, -1);
        var rowIndex = -1;
        foreach (var buffer in rowGroup.NestedColumn<T>(leaf.Column))
        {
            var repetitions = buffer.RepetitionLevels;
            var definitions = buffer.DefinitionLevels;
            var values = buffer.Values.Values;
            var valueIndex = 0;
            for (var i = 0; i < repetitions.Length; i++)
            {
                AdvanceEntry(leaf, repetitions[i], definitions[i], repeatedIndexes, rows.Length, ref rowIndex);
                object? value = definitions[i] == leaf.Column.MaxDefinitionLevel
                    ? values[valueIndex++]
                    : null;
                plan.ApplyLeaf(leaf, rows[rowIndex], repeatedIndexes, definitions[i], value);
            }
            if (valueIndex != values.Length)
                throw new CorruptParquetException(
                    $"Column '{leaf.Column.Path}' contains values without matching definition levels.");
        }
        ValidateReadRowCount(leaf, rows.Length, rowIndex);
    }

    static void ReadBytes(UntypedSchemaPlan plan, RowGroup rowGroup, UntypedSchemaPlan.LeafPlan leaf,
        Dictionary<string, object?>[] rows)
    {
        var repeatedIndexes = new int[leaf.RepeatedDepth + 1];
        Array.Fill(repeatedIndexes, -1);
        var rowIndex = -1;
        foreach (var buffer in rowGroup.NestedColumn<byte>(leaf.Column))
        {
            var repetitions = buffer.RepetitionLevels;
            var definitions = buffer.DefinitionLevels;
            var values = buffer.Values;
            var valueIndex = 0;
            for (var i = 0; i < repetitions.Length; i++)
            {
                AdvanceEntry(leaf, repetitions[i], definitions[i], repeatedIndexes, rows.Length, ref rowIndex);
                object? value = definitions[i] == leaf.Column.MaxDefinitionLevel
                    ? ConvertBytes(leaf.Column, values.GetValue(valueIndex++))
                    : null;
                plan.ApplyLeaf(leaf, rows[rowIndex], repeatedIndexes, definitions[i], value);
            }
            if (valueIndex != values.Count)
                throw new CorruptParquetException(
                    $"Column '{leaf.Column.Path}' contains values without matching definition levels.");
        }
        ValidateReadRowCount(leaf, rows.Length, rowIndex);
    }

    static void AdvanceEntry(UntypedSchemaPlan.LeafPlan leaf, int repetitionLevel, int definitionLevel,
        Span<int> repeatedIndexes, int rowCount, ref int rowIndex)
    {
        if ((uint)repetitionLevel > (uint)leaf.RepeatedDepth)
            throw new CorruptParquetException(
                $"Column '{leaf.Column.Path}' contains repetition level {repetitionLevel}, above its maximum {leaf.RepeatedDepth}.");
        if ((uint)definitionLevel > (uint)leaf.Column.MaxDefinitionLevel)
            throw new CorruptParquetException(
                $"Column '{leaf.Column.Path}' contains definition level {definitionLevel}, above its maximum {leaf.Column.MaxDefinitionLevel}.");

        if (repetitionLevel == 0)
        {
            rowIndex++;
            repeatedIndexes.Fill(-1);
        }
        else if (rowIndex < 0)
            throw new CorruptParquetException(
                $"Column '{leaf.Column.Path}' starts with a continuation before its first row.");
        if ((uint)rowIndex >= (uint)rowCount)
            throw new CorruptParquetException(
                $"Column '{leaf.Column.Path}' contains more rows than its row-group metadata declares.");

        for (var depth = 1; depth <= leaf.RepeatedDepth; depth++)
        {
            if (definitionLevel < leaf.EntryDefinitionLevels[depth])
            {
                for (var reset = depth; reset <= leaf.RepeatedDepth; reset++)
                    repeatedIndexes[reset] = -1;
                break;
            }
            if (repetitionLevel > depth)
                continue;
            repeatedIndexes[depth] = repeatedIndexes[depth] < 0 ? 0 : repeatedIndexes[depth] + 1;
            for (var reset = depth + 1; reset <= leaf.RepeatedDepth; reset++)
                repeatedIndexes[reset] = -1;
        }
    }

    static void ValidateReadRowCount(UntypedSchemaPlan.LeafPlan leaf, int expected, int lastRowIndex)
    {
        if (lastRowIndex + 1 != expected)
            throw new CorruptParquetException(
                $"Column '{leaf.Column.Path}' materialized {lastRowIndex + 1} rows; expected {expected}.");
    }

    static Type GetMaterializedType(LeafColumn column)
    {
        if (column.PhysicalType is ParquetPhysicalType.ByteArray
            or ParquetPhysicalType.FixedLenByteArray
            or ParquetPhysicalType.Int96)
            return typeof(byte[]);
        if (column.LogicalType is LogicalType.Date)
            return typeof(DateOnly);
        if (column.LogicalType is LogicalType.Time)
            return typeof(TimeOnly);
        if (column.LogicalType is LogicalType.Timestamp timestamp)
            return timestamp.IsAdjustedToUtc ? typeof(DateTimeOffset) : typeof(DateTime);
        if (column.LogicalType is LogicalType.Int integer && !integer.IsSigned)
            return integer.BitWidth switch
            {
                8 => typeof(byte),
                16 => typeof(ushort),
                32 => typeof(uint),
                64 => typeof(ulong),
                _ => throw new NotSupportedException(
                    $"Column '{column.Path}' uses unsupported integer width {integer.BitWidth}.")
            };
        return column.PhysicalType switch
        {
            ParquetPhysicalType.Boolean => typeof(bool),
            ParquetPhysicalType.Int32 => typeof(int),
            ParquetPhysicalType.Int64 => typeof(long),
            ParquetPhysicalType.Float => typeof(float),
            ParquetPhysicalType.Double => typeof(double),
            _ => throw new NotSupportedException(
                $"Column '{column.Path}' uses unsupported physical type '{column.PhysicalType}'.")
        };
    }

    static object ConvertBytes(LeafColumn column, ReadOnlySpan<byte> value)
    {
        if (column.LogicalType is LogicalType.String or LogicalType.Json)
            return Encoding.UTF8.GetString(value);
        if (column.LogicalType is LogicalType.Uuid)
        {
            if (value.Length != 16)
                throw new CorruptParquetException(
                    $"UUID column '{column.Path}' contains {value.Length} bytes instead of 16.");
            return new Guid(value, bigEndian: true);
        }
        return value.ToArray();
    }

    static void NormalizeDictionary(Dictionary<string, object?> dictionary)
    {
        foreach (var name in dictionary.Keys.ToArray())
            dictionary[name] = NormalizeValue(dictionary[name]);
    }

    static object? NormalizeValue(object? value)
    {
        switch (value)
        {
            case Dictionary<string, object?> dictionary:
                NormalizeDictionary(dictionary);
                return dictionary;
            case List<object?> list:
                for (var i = 0; i < list.Count; i++)
                    list[i] = NormalizeValue(list[i]);
                return list;
            case UntypedMap map:
            {
                var result = new Dictionary<object, object?>(map.Entries.Count);
                for (var i = 0; i < map.Entries.Count; i++)
                {
                    var entry = map.Entries[i];
                    var key = NormalizeValue(entry.Key)
                        ?? throw new CorruptParquetException("Map entry contains a null key.");
                    if (!result.TryAdd(key, NormalizeValue(entry.Value)))
                        throw new CorruptParquetException($"Map contains duplicate key '{key}'.");
                }
                return result;
            }
            default:
                return value;
        }
    }

    UntypedSchemaPlan GetPlan()
        => _plan ?? throw new InvalidOperationException("Call Reset before reading rows.");
}
