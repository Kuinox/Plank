using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Plank.Schema;
using Plank.Writing.PageStrategy;

namespace Plank.Writing.Encoding;

static class Encoding
{
    const int DictionaryDropCheckPeriodRows = 2048;

    internal static void Encode<T>(BufferWriterFactory bufferWriters, Column column, ReadOnlySpan<T> values,
        IPageStrategy strategy, PageList pages, LeafProjectionInfo leafProjectionInfo)
        where T : notnull
    {
        ArgumentNullException.ThrowIfNull(column);
        ArgumentNullException.ThrowIfNull(strategy);
        ArgumentNullException.ThrowIfNull(pages);

        pages.Clear();
        if (values.Length == 0)
            return;

        if (column.Options.Repetition == ParquetRepetition.Repeated)
        {
            EncodeRepeatedRows(bufferWriters, column, values, pages, leafProjectionInfo);
            return;
        }

        var dataEncoding = EncodingKindResolver.GetDataEncodingKind(column);
        var dictionaryEncoding = EncodingKindResolver.GetDictionaryEncodingKind(column);
        var useDictionary = TryWriteDictionaryPage(bufferWriters, column, values, strategy, pages,
            out var dictionaryValueCount, out var dictionaryIndexesBytes);
        var dictionaryIndexes = useDictionary && dictionaryIndexesBytes is not null
            ? MemoryMarshal.Cast<byte, int>(dictionaryIndexesBytes.AsSpan(0, checked(values.Length * sizeof(int))))
            : default;
        var dictionaryBitWidth = useDictionary
            ? RleBitPackingHybridEncoding.GetBitWidthFromMaxValue(dictionaryValueCount <= 1 ? 0 : dictionaryValueCount - 1)
            : 0;

        var rowsWritten = 0;
        try
        {
            while (rowsWritten < values.Length)
            {
                var pageStart = rowsWritten;
                var pageRowCount = 0;
                while (rowsWritten < values.Length)
                {
                    rowsWritten++;
                    pageRowCount++;
                    if (rowsWritten == values.Length)
                        break;
                    if (strategy.ShouldStartNewDataPage(column, values.Length, rowsWritten, pageRowCount))
                        break;
                }

                var pageIndex = AddNewDataPage(bufferWriters, pages);
                ref var page = ref pages[pageIndex];
                var pageValues = values.Slice(pageStart, pageRowCount);
                if (useDictionary)
                {
                    if (dictionaryIndexes.IsEmpty)
                        throw new InvalidOperationException("Dictionary index buffer is missing for dictionary-encoded page.");
                    DictionaryIndexEncodingDispatcher.WriteIndexes(dictionaryEncoding,
                        dictionaryIndexes.Slice(pageStart, pageRowCount), dictionaryBitWidth, ref page.Content);
                }
                else
                    ValueEncodingDispatcher.WriteValues(dataEncoding, column, pageValues, bufferWriters, ref page.Content);

                WriteDataPageHeader(ref page, pageRowCount, pageRowCount, 0, 0, 0,
                    useDictionary ? dictionaryEncoding : dataEncoding);
            }
        }
        finally
        {
            if (dictionaryIndexesBytes is not null)
                bufferWriters.ReturnScratch(dictionaryIndexesBytes);
        }
    }

    static bool TryWriteDictionaryPage<T>(BufferWriterFactory bufferWriters, Column column, ReadOnlySpan<T> values,
        IPageStrategy strategy, PageList pages, out int dictionaryValueCount, out byte[]? dictionaryIndexesBytes)
        where T : notnull
    {
        dictionaryValueCount = 0;
        dictionaryIndexesBytes = null;

        var dictionaryMode = strategy.GetDictionaryMode(column);
        if (dictionaryMode == DictionaryMode.Disabled)
        {
            return false;
        }

        var dictionaryPageIndex = AddDictionaryPage(bufferWriters, pages);
        ref var dictionaryPage = ref pages[dictionaryPageIndex];
        var initialUniqueCapacity = dictionaryMode == DictionaryMode.Forced
            ? Math.Max(256, values.Length)
            : Math.Clamp(values.Length / 4, 256, 65_536);
        var dictionary = new Dictionary<T, int>(initialUniqueCapacity, GetDictionaryComparer<T>());
        var dictionaryValues = new List<T>(initialUniqueCapacity);
        var indexByteLength = checked(values.Length * sizeof(int));
        byte[]? rentedIndexesBytes = bufferWriters.RentScratch(checked((uint)Math.Max(indexByteLength, sizeof(int))));
        try
        {
            var indexes = MemoryMarshal.Cast<byte, int>(rentedIndexesBytes.AsSpan(0, indexByteLength));
            if (dictionaryMode == DictionaryMode.Maybe)
            {
                var nextDropCheckRow = Math.Min(DictionaryDropCheckPeriodRows, values.Length);
                for (var i = 0; i < values.Length; i++)
                {
                    var value = values[i];
                    ref var indexRef = ref CollectionsMarshal.GetValueRefOrAddDefault(dictionary, value, out var exists);
                    if (!exists)
                    {
                        indexRef = dictionaryValues.Count;
                        dictionaryValues.Add(value);
                    }

                    indexes[i] = indexRef;

                    var rowsSeen = i + 1;
                    if (rowsSeen != nextDropCheckRow && rowsSeen != values.Length)
                        continue;
                    if (strategy.ShouldDropDictionary(column, dictionary, values.Length, rowsSeen))
                    {
                        dictionaryPage.Header.Reset();
                        dictionaryPage.Content.Reset();
                        pages.RemoveLast();
                        bufferWriters.ReturnScratch(rentedIndexesBytes);
                        rentedIndexesBytes = null;
                        return false;
                    }

                    nextDropCheckRow = Math.Min(values.Length, rowsSeen + DictionaryDropCheckPeriodRows);
                }
            }
            else
            {
                for (var i = 0; i < values.Length; i++)
                {
                    var value = values[i];
                    ref var indexRef = ref CollectionsMarshal.GetValueRefOrAddDefault(dictionary, value, out var exists);
                    if (!exists)
                    {
                        indexRef = dictionaryValues.Count;
                        dictionaryValues.Add(value);
                    }

                    indexes[i] = indexRef;
                }
            }

            PlainEncoding.WriteValues(column, CollectionsMarshal.AsSpan(dictionaryValues), ref dictionaryPage.Content);

            dictionaryPage.SetDictionaryPageMetadata(dictionary.Count);
            dictionaryValueCount = dictionary.Count;
            dictionaryIndexesBytes = rentedIndexesBytes;
            rentedIndexesBytes = null;
            return true;
        }
        finally
        {
            if (rentedIndexesBytes is not null)
                bufferWriters.ReturnScratch(rentedIndexesBytes);
        }
    }

    static void EncodeRepeatedRows<T>(BufferWriterFactory bufferWriters, Column column, ReadOnlySpan<T> rows, PageList pages,
        LeafProjectionInfo leafProjectionInfo)
        where T : notnull
    {
        var dataEncoding = EncodingKindResolver.GetDataEncodingKind(column);
        var pageIndex = AddNewDataPage(bufferWriters, pages);
        ref var page = ref pages[pageIndex];

        if (leafProjectionInfo.MaxRepetitionLevel > 1)
        {
            switch (column.PhysicalType)
            {
                case ParquetPhysicalType.Boolean:
                    EncodeRepeatedRowsNestedCore<bool, T>(bufferWriters, column, dataEncoding, rows, ref page,
                        leafProjectionInfo);
                    return;
                case ParquetPhysicalType.Int32:
                    EncodeRepeatedRowsNestedCore<int, T>(bufferWriters, column, dataEncoding, rows, ref page,
                        leafProjectionInfo);
                    return;
                case ParquetPhysicalType.Int64:
                    EncodeRepeatedRowsNestedCore<long, T>(bufferWriters, column, dataEncoding, rows, ref page,
                        leafProjectionInfo);
                    return;
                case ParquetPhysicalType.Float:
                    EncodeRepeatedRowsNestedCore<float, T>(bufferWriters, column, dataEncoding, rows, ref page,
                        leafProjectionInfo);
                    return;
                case ParquetPhysicalType.Double:
                    EncodeRepeatedRowsNestedCore<double, T>(bufferWriters, column, dataEncoding, rows, ref page,
                        leafProjectionInfo);
                    return;
                case ParquetPhysicalType.ByteArray:
                case ParquetPhysicalType.Int96:
                case ParquetPhysicalType.FixedLenByteArray:
                    EncodeRepeatedRowsNestedCore<byte[], T>(bufferWriters, column, dataEncoding, rows, ref page,
                        leafProjectionInfo);
                    return;
            }

            throw new InvalidOperationException(
                $"Repeated column '{column.Name}' with physical type '{column.PhysicalType}' is not supported for repetition level {leafProjectionInfo.MaxRepetitionLevel}.");
        }

        switch (column.PhysicalType)
        {
            case ParquetPhysicalType.Boolean:
                if (typeof(T) == typeof(bool[]))
                {
                    EncodeRepeatedRowsCore(bufferWriters, column, dataEncoding,
                        Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<bool[]>>(ref rows), ref page, leafProjectionInfo);
                    return;
                }
                if (leafProjectionInfo.ElementOptional && typeof(T) == typeof(bool?[]))
                {
                    EncodeRepeatedRowsCoreNullableValue(bufferWriters, column, dataEncoding,
                        Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<bool?[]>>(ref rows), ref page, leafProjectionInfo);
                    return;
                }
                break;
            case ParquetPhysicalType.Int32:
                if (typeof(T) == typeof(int[]))
                {
                    EncodeRepeatedRowsCore(bufferWriters, column, dataEncoding,
                        Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<int[]>>(ref rows), ref page, leafProjectionInfo);
                    return;
                }
                if (leafProjectionInfo.ElementOptional && typeof(T) == typeof(int?[]))
                {
                    EncodeRepeatedRowsCoreNullableValue(bufferWriters, column, dataEncoding,
                        Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<int?[]>>(ref rows), ref page, leafProjectionInfo);
                    return;
                }
                break;
            case ParquetPhysicalType.Int64:
                if (typeof(T) == typeof(long[]))
                {
                    EncodeRepeatedRowsCore(bufferWriters, column, dataEncoding,
                        Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<long[]>>(ref rows), ref page, leafProjectionInfo);
                    return;
                }
                if (leafProjectionInfo.ElementOptional && typeof(T) == typeof(long?[]))
                {
                    EncodeRepeatedRowsCoreNullableValue(bufferWriters, column, dataEncoding,
                        Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<long?[]>>(ref rows), ref page, leafProjectionInfo);
                    return;
                }
                break;
            case ParquetPhysicalType.Float:
                if (typeof(T) == typeof(float[]))
                {
                    EncodeRepeatedRowsCore(bufferWriters, column, dataEncoding,
                        Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<float[]>>(ref rows), ref page, leafProjectionInfo);
                    return;
                }
                if (leafProjectionInfo.ElementOptional && typeof(T) == typeof(float?[]))
                {
                    EncodeRepeatedRowsCoreNullableValue(bufferWriters, column, dataEncoding,
                        Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<float?[]>>(ref rows), ref page, leafProjectionInfo);
                    return;
                }
                break;
            case ParquetPhysicalType.Double:
                if (typeof(T) == typeof(double[]))
                {
                    EncodeRepeatedRowsCore(bufferWriters, column, dataEncoding,
                        Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<double[]>>(ref rows), ref page, leafProjectionInfo);
                    return;
                }
                if (leafProjectionInfo.ElementOptional && typeof(T) == typeof(double?[]))
                {
                    EncodeRepeatedRowsCoreNullableValue(bufferWriters, column, dataEncoding,
                        Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<double?[]>>(ref rows), ref page, leafProjectionInfo);
                    return;
                }
                break;
            case ParquetPhysicalType.ByteArray:
            case ParquetPhysicalType.Int96:
            case ParquetPhysicalType.FixedLenByteArray:
                if (typeof(T) == typeof(byte[][]))
                {
                    if (leafProjectionInfo.ElementOptional)
                        EncodeRepeatedRowsCoreNullableReference(bufferWriters, column, dataEncoding,
                            Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<byte[][]>>(ref rows), ref page, leafProjectionInfo);
                    else
                        EncodeRepeatedRowsCore(bufferWriters, column, dataEncoding,
                            Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<byte[][]>>(ref rows), ref page, leafProjectionInfo);
                    return;
                }
                break;
        }

        throw new InvalidOperationException(
            $"Repeated column '{column.Name}' with physical type '{column.PhysicalType}' expects rows of '{column.PhysicalType}[]'.");
    }

    static void EncodeRepeatedRowsCore<TElement>(BufferWriterFactory bufferWriters, Column column, EncodingKind dataEncoding,
        ReadOnlySpan<TElement[]> rows, ref Page page, LeafProjectionInfo leafProjectionInfo)
        where TElement : notnull
    {
        if (leafProjectionInfo.ElementOptional)
            throw new InvalidOperationException(
                $"Column '{column.Name}' has optional list elements; use nullable row element type for this column.");

        var rowCount = rows.Length;
        var physicalValueCount = 0;
        var levelValueCount = 0;
        var nullCount = 0;
        var allowsNullRow = leafProjectionInfo.ListOptional;
        var listDefinedDefinitionLevel = leafProjectionInfo.IsList && leafProjectionInfo.ListOptional ? 1 : 0;
        var presentElementDefinitionLevel = listDefinedDefinitionLevel + 1;
        var definitionBitWidth = GetBitWidth(presentElementDefinitionLevel);

        for (var rowIndex = 0; rowIndex < rowCount; rowIndex++)
        {
            var row = rows[rowIndex];
            if (row is null)
            {
                if (!allowsNullRow)
                    throw new InvalidOperationException(
                        $"Column '{column.Name}' has repeated values; null row arrays are not supported.");
                levelValueCount = checked(levelValueCount + 1);
                nullCount = checked(nullCount + 1);
                continue;
            }

            if (row.Length == 0)
            {
                if (!leafProjectionInfo.IsList)
                    throw new InvalidOperationException(
                        $"Column '{column.Name}' has repeated values; empty rows are not supported for this schema.");
                levelValueCount = checked(levelValueCount + 1);
                nullCount = checked(nullCount + 1);
                continue;
            }

            levelValueCount = checked(levelValueCount + row.Length);
            physicalValueCount = checked(physicalValueCount + row.Length);
        }

        var flatValues = new TElement[physicalValueCount];
        var flatIndex = 0;
        for (var rowIndex = 0; rowIndex < rowCount; rowIndex++)
        {
            var row = rows[rowIndex];
            if (row is null || row.Length == 0)
                continue;
            row.CopyTo(flatValues.AsSpan(flatIndex));
            flatIndex += row.Length;
        }

        var repetitionLength = WriteRepeatedLevels(rows, ref page.Content);
        var definitionLength = WriteRepeatedDefinitionLevels(rows, listDefinedDefinitionLevel,
            presentElementDefinitionLevel, allowsNullRow, definitionBitWidth, ref page.Content);
        ValueEncodingDispatcher.WriteValues(dataEncoding, column, flatValues, bufferWriters, ref page.Content);
        WriteDataPageHeader(ref page, rowCount, levelValueCount, nullCount, repetitionLength, definitionLength,
            dataEncoding);
    }

    static void EncodeRepeatedRowsCoreNullableValue<TValue>(BufferWriterFactory bufferWriters, Column column,
        EncodingKind dataEncoding, ReadOnlySpan<TValue?[]> rows, ref Page page, LeafProjectionInfo leafProjectionInfo)
        where TValue : struct
    {
        if (!leafProjectionInfo.ElementOptional)
            throw new InvalidOperationException(
                $"Column '{column.Name}' expects required list elements, but nullable row values were provided.");

        var rowCount = rows.Length;
        var physicalValueCount = 0;
        var levelValueCount = 0;
        var nullCount = 0;
        var allowsNullRow = leafProjectionInfo.ListOptional;
        var listDefinedDefinitionLevel = leafProjectionInfo.IsList && leafProjectionInfo.ListOptional ? 1 : 0;
        var nullElementDefinitionLevel = listDefinedDefinitionLevel + 1;
        var presentElementDefinitionLevel = listDefinedDefinitionLevel + 2;
        var definitionBitWidth = GetBitWidth(presentElementDefinitionLevel);

        for (var rowIndex = 0; rowIndex < rowCount; rowIndex++)
        {
            var row = rows[rowIndex];
            if (row is null)
            {
                if (!allowsNullRow)
                    throw new InvalidOperationException(
                        $"Column '{column.Name}' has repeated values; null row arrays are not supported.");
                levelValueCount = checked(levelValueCount + 1);
                nullCount = checked(nullCount + 1);
                continue;
            }

            if (row.Length == 0)
            {
                if (!leafProjectionInfo.IsList)
                    throw new InvalidOperationException(
                        $"Column '{column.Name}' has repeated values; empty rows are not supported for this schema.");
                levelValueCount = checked(levelValueCount + 1);
                nullCount = checked(nullCount + 1);
                continue;
            }

            levelValueCount = checked(levelValueCount + row.Length);
            for (var i = 0; i < row.Length; i++)
            {
                if (row[i].HasValue)
                    physicalValueCount = checked(physicalValueCount + 1);
                else
                    nullCount = checked(nullCount + 1);
            }
        }

        var flatValues = new TValue[physicalValueCount];
        var flatIndex = 0;
        for (var rowIndex = 0; rowIndex < rowCount; rowIndex++)
        {
            var row = rows[rowIndex];
            if (row is null || row.Length == 0)
                continue;
            for (var i = 0; i < row.Length; i++)
            {
                var value = row[i];
                if (!value.HasValue)
                    continue;
                flatValues[flatIndex++] = value.Value;
            }
        }

        var repetitionLength = WriteRepeatedLevels(rows, ref page.Content);
        var definitionLength = WriteRepeatedDefinitionLevelsNullableValues(rows, listDefinedDefinitionLevel,
            nullElementDefinitionLevel, presentElementDefinitionLevel, allowsNullRow, definitionBitWidth, ref page.Content);
        ValueEncodingDispatcher.WriteValues(dataEncoding, column, flatValues, bufferWriters, ref page.Content);
        WriteDataPageHeader(ref page, rowCount, levelValueCount, nullCount, repetitionLength, definitionLength,
            dataEncoding);
    }

    static void EncodeRepeatedRowsCoreNullableReference<TElement>(BufferWriterFactory bufferWriters, Column column,
        EncodingKind dataEncoding, ReadOnlySpan<TElement[]> rows, ref Page page, LeafProjectionInfo leafProjectionInfo)
        where TElement : class
    {
        if (!leafProjectionInfo.ElementOptional)
            throw new InvalidOperationException(
                $"Column '{column.Name}' expects required list elements, but nullable row values were provided.");

        var rowCount = rows.Length;
        var physicalValueCount = 0;
        var levelValueCount = 0;
        var nullCount = 0;
        var allowsNullRow = leafProjectionInfo.ListOptional;
        var listDefinedDefinitionLevel = leafProjectionInfo.IsList && leafProjectionInfo.ListOptional ? 1 : 0;
        var nullElementDefinitionLevel = listDefinedDefinitionLevel + 1;
        var presentElementDefinitionLevel = listDefinedDefinitionLevel + 2;
        var definitionBitWidth = GetBitWidth(presentElementDefinitionLevel);

        for (var rowIndex = 0; rowIndex < rowCount; rowIndex++)
        {
            var row = rows[rowIndex];
            if (row is null)
            {
                if (!allowsNullRow)
                    throw new InvalidOperationException(
                        $"Column '{column.Name}' has repeated values; null row arrays are not supported.");
                levelValueCount = checked(levelValueCount + 1);
                nullCount = checked(nullCount + 1);
                continue;
            }

            if (row.Length == 0)
            {
                if (!leafProjectionInfo.IsList)
                    throw new InvalidOperationException(
                        $"Column '{column.Name}' has repeated values; empty rows are not supported for this schema.");
                levelValueCount = checked(levelValueCount + 1);
                nullCount = checked(nullCount + 1);
                continue;
            }

            levelValueCount = checked(levelValueCount + row.Length);
            for (var i = 0; i < row.Length; i++)
            {
                if (row[i] is null)
                    nullCount = checked(nullCount + 1);
                else
                    physicalValueCount = checked(physicalValueCount + 1);
            }
        }

        var flatValues = new TElement[physicalValueCount];
        var flatIndex = 0;
        for (var rowIndex = 0; rowIndex < rowCount; rowIndex++)
        {
            var row = rows[rowIndex];
            if (row is null || row.Length == 0)
                continue;
            for (var i = 0; i < row.Length; i++)
            {
                var value = row[i];
                if (value is null)
                    continue;
                flatValues[flatIndex++] = value;
            }
        }

        var repetitionLength = WriteRepeatedLevels(rows, ref page.Content);
        var definitionLength = WriteRepeatedDefinitionLevelsNullableReferences(rows, listDefinedDefinitionLevel,
            nullElementDefinitionLevel, presentElementDefinitionLevel, allowsNullRow, definitionBitWidth, ref page.Content);
        ValueEncodingDispatcher.WriteValues(dataEncoding, column, flatValues, bufferWriters, ref page.Content);
        WriteDataPageHeader(ref page, rowCount, levelValueCount, nullCount, repetitionLength, definitionLength,
            dataEncoding);
    }

    static void EncodeRepeatedRowsNestedCore<TElement, TRow>(BufferWriterFactory bufferWriters, Column column,
        EncodingKind dataEncoding, ReadOnlySpan<TRow> rows, ref Page page, LeafProjectionInfo leafProjectionInfo)
        where TElement : notnull
        where TRow : notnull
    {
        if (leafProjectionInfo.ElementOptional)
            throw new NotSupportedException(
                $"Column '{column.Name}' nested repeated optional elements are not implemented yet.");

        var allowsNullRow = leafProjectionInfo.ListOptional;
        var rowDefinedLevel = allowsNullRow ? 1 : 0;
        var repLevels = new List<int>(rows.Length * 2);
        var defLevels = new List<int>(rows.Length * 2);
        var values = new List<TElement>(rows.Length * 2);
        for (var rowIndex = 0; rowIndex < rows.Length; rowIndex++)
        {
            object? row = rows[rowIndex];
            TraverseNestedRepeatedRow(row, depth: 1, repForFirst: 0, currentDefinitionLevel: rowDefinedLevel,
                allowsNullRow, leafProjectionInfo.MaxRepetitionLevel, leafProjectionInfo.MaxDefinitionLevel, repLevels, defLevels,
                values, column.Name);
        }

        var repBitWidth = GetBitWidth(leafProjectionInfo.MaxRepetitionLevel);
        var defBitWidth = GetBitWidth(leafProjectionInfo.MaxDefinitionLevel);
        var repetitionLength = WriteLevelSequence(repLevels, repBitWidth, ref page.Content);
        var definitionLength = WriteLevelSequence(defLevels, defBitWidth, ref page.Content);
        ValueEncodingDispatcher.WriteValues(dataEncoding, column, CollectionsMarshal.AsSpan(values), bufferWriters, ref page.Content);
        var nullCount = defLevels.Count - values.Count;
        WriteDataPageHeader(ref page, rows.Length, defLevels.Count, nullCount, repetitionLength, definitionLength,
            dataEncoding);

        static void TraverseNestedRepeatedRow(object? node, int depth, int repForFirst, int currentDefinitionLevel,
            bool allowNullNode, int maxRepetitionLevel, int maxDefinitionLevel, List<int> repLevels, List<int> defLevels,
            List<TElement> values, string columnName)
        {
            if (node is null)
            {
                if (!allowNullNode)
                    throw new InvalidOperationException(
                        $"Column '{columnName}' has repeated values; null array is not supported at depth {depth}.");
                repLevels.Add(repForFirst);
                defLevels.Add(currentDefinitionLevel - 1);
                return;
            }

            if (node is not Array array)
                throw new InvalidOperationException(
                    $"Column '{columnName}' expects jagged array rows for nested repetition level {maxRepetitionLevel}.");

            if (array.Length == 0)
            {
                repLevels.Add(repForFirst);
                defLevels.Add(currentDefinitionLevel);
                return;
            }

            for (var i = 0; i < array.Length; i++)
            {
                var rep = i == 0 ? repForFirst : depth;
                var element = array.GetValue(i);
                if (depth == maxRepetitionLevel)
                {
                    if (element is not TElement value)
                        throw new InvalidOperationException(
                            $"Column '{columnName}' has incompatible leaf value type '{element?.GetType()}'.");
                    repLevels.Add(rep);
                    defLevels.Add(maxDefinitionLevel);
                    values.Add(value);
                    continue;
                }

                TraverseNestedRepeatedRow(element, depth + 1, rep, currentDefinitionLevel + 1, allowNullNode: false,
                    maxRepetitionLevel, maxDefinitionLevel, repLevels, defLevels, values, columnName);
            }
        }
    }

    static int WriteRepeatedLevels<TElement>(ReadOnlySpan<TElement[]> rows, ref BufferWriter writer)
    {
        var start = writer.WrittenLength;
        for (var rowIndex = 0; rowIndex < rows.Length; rowIndex++)
        {
            var row = rows[rowIndex];
            if (row is null)
            {
                WriteLevelRun(0, 1, 1, ref writer);
                continue;
            }

            var rowLength = row.Length;
            WriteLevelRun(0, 1, 1, ref writer);
            if (rowLength > 1)
                WriteLevelRun(1, rowLength - 1, 1, ref writer);
        }

        return writer.WrittenLength - start;
    }

    static int WriteLevelSequence(List<int> levels, int bitWidth, ref BufferWriter writer)
    {
        if (levels.Count == 0)
            return 0;

        var start = writer.WrittenLength;
        var runValue = levels[0];
        var runLength = 1;
        for (var i = 1; i < levels.Count; i++)
        {
            var value = levels[i];
            if (value == runValue)
            {
                runLength++;
                continue;
            }

            WriteLevelRun(runValue, runLength, bitWidth, ref writer);
            runValue = value;
            runLength = 1;
        }

        WriteLevelRun(runValue, runLength, bitWidth, ref writer);
        return writer.WrittenLength - start;
    }

    static int WriteRepeatedDefinitionLevels<TElement>(ReadOnlySpan<TElement[]> rows, int listDefinedDefinitionLevel,
        int presentElementDefinitionLevel, bool allowsNullRow, int definitionBitWidth, ref BufferWriter writer)
    {
        var start = writer.WrittenLength;
        for (var rowIndex = 0; rowIndex < rows.Length; rowIndex++)
        {
            var row = rows[rowIndex];
            if (row is null)
            {
                if (!allowsNullRow)
                    throw new InvalidOperationException("Null row is not allowed for this repeated column.");
                WriteLevelRun(0, 1, definitionBitWidth, ref writer);
                continue;
            }

            if (row.Length == 0)
            {
                WriteLevelRun(listDefinedDefinitionLevel, 1, definitionBitWidth, ref writer);
                continue;
            }

            WriteLevelRun(presentElementDefinitionLevel, row.Length, definitionBitWidth, ref writer);
        }

        return writer.WrittenLength - start;
    }

    static int WriteRepeatedDefinitionLevelsNullableValues<TValue>(ReadOnlySpan<TValue?[]> rows, int listDefinedDefinitionLevel,
        int nullElementDefinitionLevel, int presentElementDefinitionLevel, bool allowsNullRow, int definitionBitWidth,
        ref BufferWriter writer)
        where TValue : struct
    {
        var start = writer.WrittenLength;
        for (var rowIndex = 0; rowIndex < rows.Length; rowIndex++)
        {
            var row = rows[rowIndex];
            if (row is null)
            {
                if (!allowsNullRow)
                    throw new InvalidOperationException("Null row is not allowed for this repeated column.");
                WriteLevelRun(0, 1, definitionBitWidth, ref writer);
                continue;
            }

            if (row.Length == 0)
            {
                WriteLevelRun(listDefinedDefinitionLevel, 1, definitionBitWidth, ref writer);
                continue;
            }

            for (var i = 0; i < row.Length; i++)
                WriteLevelRun(row[i].HasValue ? presentElementDefinitionLevel : nullElementDefinitionLevel, 1,
                    definitionBitWidth, ref writer);
        }

        return writer.WrittenLength - start;
    }

    static int WriteRepeatedDefinitionLevelsNullableReferences<TElement>(ReadOnlySpan<TElement[]> rows,
        int listDefinedDefinitionLevel, int nullElementDefinitionLevel, int presentElementDefinitionLevel, bool allowsNullRow,
        int definitionBitWidth, ref BufferWriter writer)
        where TElement : class
    {
        var start = writer.WrittenLength;
        for (var rowIndex = 0; rowIndex < rows.Length; rowIndex++)
        {
            var row = rows[rowIndex];
            if (row is null)
            {
                if (!allowsNullRow)
                    throw new InvalidOperationException("Null row is not allowed for this repeated column.");
                WriteLevelRun(0, 1, definitionBitWidth, ref writer);
                continue;
            }

            if (row.Length == 0)
            {
                WriteLevelRun(listDefinedDefinitionLevel, 1, definitionBitWidth, ref writer);
                continue;
            }

            for (var i = 0; i < row.Length; i++)
                WriteLevelRun(row[i] is null ? nullElementDefinitionLevel : presentElementDefinitionLevel, 1,
                    definitionBitWidth, ref writer);
        }

        return writer.WrittenLength - start;
    }

    static int GetBitWidth(int maxLevel)
        => RleBitPackingHybridEncoding.GetBitWidthFromMaxValue(maxLevel);

    static void WriteLevelRun(int value, int runLength, int bitWidth, ref BufferWriter writer)
    {
        if (runLength <= 0)
            return;

        WriteUnsignedVarInt(((uint)runLength) << 1, ref writer);
        var byteWidth = (bitWidth + 7) >> 3;
        if (byteWidth == 0)
            return;

        var destination = writer.GetSpan(byteWidth);
        var unsignedValue = unchecked((uint)value);
        for (var i = 0; i < byteWidth; i++)
            destination[i] = (byte)(unsignedValue >> (8 * i));
        writer.Advance(byteWidth);
    }

    static void WriteUnsignedVarInt(uint value, ref BufferWriter writer)
    {
        while (value >= 0x80)
        {
            var byteSpan = writer.GetSpan(1);
            byteSpan[0] = (byte)(value | 0x80);
            writer.Advance(1);
            value >>= 7;
        }

        var finalByte = writer.GetSpan(1);
        finalByte[0] = (byte)value;
        writer.Advance(1);
    }


    static int AddDictionaryPage(BufferWriterFactory bufferWriters, PageList pages)
    {
        ref var page = ref pages.Add();
        EnsureInitialized(bufferWriters, ref page.Header, useColumnBuffer: false);
        EnsureInitialized(bufferWriters, ref page.Content, useColumnBuffer: true);
        page.ResetMetadata();
        return pages.Count - 1;
    }

    static int AddNewDataPage(BufferWriterFactory bufferWriters, PageList pages)
    {
        ref var page = ref pages.Add();
        EnsureInitialized(bufferWriters, ref page.Header, useColumnBuffer: false);
        EnsureInitialized(bufferWriters, ref page.Content, useColumnBuffer: false);
        page.ResetMetadata();
        return pages.Count - 1;
    }

    static void EnsureInitialized(BufferWriterFactory bufferWriters, ref BufferWriter buffer, bool useColumnBuffer)
    {
        if (buffer.IsInitialized)
        {
            buffer.Reset();
            return;
        }

        buffer = useColumnBuffer ? bufferWriters.CreateColumnBufferWriter() : bufferWriters.CreatePageBufferWriter();
    }

    static void WriteDataPageHeader(ref Page page, int rowCount, int valueCount, int nullCount,
        int repetitionLevelsByteLength, int definitionLevelsByteLength, EncodingKind encoding)
        => page.SetDataPageMetadata(rowCount, valueCount, nullCount, repetitionLevelsByteLength,
            definitionLevelsByteLength, encoding);

    static IEqualityComparer<T> GetDictionaryComparer<T>()
        where T : notnull
    {
        if (typeof(T) == typeof(byte[]))
            return (IEqualityComparer<T>)(object)ByteArrayComparer.Instance;

        return EqualityComparer<T>.Default;
    }
}
