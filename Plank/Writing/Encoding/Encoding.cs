using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using TextEncoding = System.Text.Encoding;
using Plank.Schema;
using Plank.Writing.PageStrategy;

namespace Plank.Writing.Encoding;

static class Encoding
{
    const int DictionaryDropCheckPeriodRows = 2048;
    static readonly TextEncoding Utf8 = TextEncoding.UTF8;

    internal static void Encode<T>(BufferWriterFactory bufferWriters, Column column, ReadOnlySpan<T> values,
        IPageStrategy strategy, PageList pages, LeafProjectionInfo leafProjectionInfo,
        ReusableDictionaryState<T> dictionaryState)
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
        var useDictionary = TryWriteDictionaryPage(bufferWriters, column, values, strategy, pages, dictionaryState,
            out var dictionaryValueCount, out var dictionaryIndexesBytes);
        var dictionaryIndexes = useDictionary && dictionaryIndexesBytes is not null
            ? MemoryMarshal.Cast<byte, int>(dictionaryIndexesBytes.AsSpan(0, checked(values.Length * sizeof(int))))
            : default;
        var dictionaryBitWidth = useDictionary
            ? RleBitPackingHybridEncoding.GetBitWidthFromMaxValue(dictionaryValueCount <= 1 ? 0 : dictionaryValueCount - 1)
            : 0;

        try
        {
            if (useDictionary)
            {
                WriteDictionaryDataPages(bufferWriters, values.Length, dictionaryEncoding, pages, dictionaryIndexes,
                    dictionaryBitWidth, strategy);
                return;
            }

            if (TryWriteFixedWidthDataPages(bufferWriters, column, values, dataEncoding, strategy, pages))
                return;
            if (TryWritePlainSizeDataPages(bufferWriters, column, values, dataEncoding, strategy, pages))
                return;

            WriteStrategyDataPages(bufferWriters, column, values, dataEncoding, strategy, pages);
        }
        finally
        {
            if (dictionaryIndexesBytes is not null)
                bufferWriters.ReturnScratch(dictionaryIndexesBytes);
        }
    }

    static void WriteStrategyDataPages<T>(BufferWriterFactory bufferWriters, Column column, ReadOnlySpan<T> values,
        EncodingKind dataEncoding, IPageStrategy strategy, PageList pages)
        where T : notnull
    {
        var rowsWritten = 0;
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
                if (strategy.ShouldStartNewDataPage(values.Length, rowsWritten, pageRowCount))
                    break;
            }

            WriteDataPage(bufferWriters, column, values.Slice(pageStart, pageRowCount), dataEncoding, pages);
        }
    }

    static bool TryWriteFixedWidthDataPages<T>(BufferWriterFactory bufferWriters, Column column, ReadOnlySpan<T> values,
        EncodingKind dataEncoding, IPageStrategy strategy, PageList pages)
        where T : notnull
    {
        if (!strategy.TryGetTargetDataPageSizeBytes(out var targetPageBytes))
            return false;
        if (!TryGetFixedWidthRowsPerPage(column, dataEncoding, targetPageBytes, out var rowsPerPage))
            return false;

        for (var pageStart = 0; pageStart < values.Length; pageStart += rowsPerPage)
        {
            var pageRowCount = Math.Min(rowsPerPage, values.Length - pageStart);
            WriteDataPage(bufferWriters, column, values.Slice(pageStart, pageRowCount), dataEncoding, pages);
        }

        return true;
    }

    static bool TryWritePlainSizeDataPages<T>(BufferWriterFactory bufferWriters, Column column, ReadOnlySpan<T> values,
        EncodingKind dataEncoding, IPageStrategy strategy, PageList pages)
        where T : notnull
    {
        if (!strategy.TryGetTargetDataPageSizeBytes(out var targetPageBytes))
            return false;
        if (dataEncoding != EncodingKind.Plain)
            return false;
        if (!TryGetPlainEncodedValueSize(column, typeof(T), out var fixedValueBytes))
            return false;

        if (fixedValueBytes > 0)
        {
            var rowsPerPage = Math.Max(1, targetPageBytes / fixedValueBytes);
            WriteFixedRowsDataPages(bufferWriters, column, values, dataEncoding, pages, rowsPerPage);
            return true;
        }

        WriteVariablePlainDataPages(bufferWriters, column, values, dataEncoding, pages, targetPageBytes);
        return true;
    }

    static void WriteFixedRowsDataPages<T>(BufferWriterFactory bufferWriters, Column column, ReadOnlySpan<T> values,
        EncodingKind dataEncoding, PageList pages, int rowsPerPage)
        where T : notnull
    {
        for (var pageStart = 0; pageStart < values.Length; pageStart += rowsPerPage)
        {
            var pageRowCount = Math.Min(rowsPerPage, values.Length - pageStart);
            WriteDataPage(bufferWriters, column, values.Slice(pageStart, pageRowCount), dataEncoding, pages);
        }
    }

    static void WriteVariablePlainDataPages<T>(BufferWriterFactory bufferWriters, Column column, ReadOnlySpan<T> values,
        EncodingKind dataEncoding, PageList pages, int targetPageBytes)
        where T : notnull
    {
        var rowsWritten = 0;
        while (rowsWritten < values.Length)
        {
            var pageStart = rowsWritten;
            var pageRowCount = 0;
            var pageBytes = 0;
            while (rowsWritten < values.Length)
            {
                var rowBytes = GetVariablePlainValueBytes(column, values[rowsWritten]);
                if (pageRowCount > 0 && pageBytes + rowBytes > targetPageBytes)
                    break;

                rowsWritten++;
                pageRowCount++;
                pageBytes = checked(pageBytes + rowBytes);
            }

            WriteDataPage(bufferWriters, column, values.Slice(pageStart, pageRowCount), dataEncoding, pages);
        }
    }

    static void WriteDataPage<T>(BufferWriterFactory bufferWriters, Column column, ReadOnlySpan<T> values,
        EncodingKind dataEncoding, PageList pages)
        where T : notnull
    {
        var pageIndex = AddNewDataPage(bufferWriters, pages);
        ref var page = ref pages[pageIndex];
        ValueEncodingDispatcher.WriteValues(dataEncoding, column, values, bufferWriters, ref page.Content);
        WriteDataPageHeader(ref page, values.Length, values.Length, 0, 0, 0, dataEncoding);
    }

    static bool TryGetFixedWidthRowsPerPage(Column column, EncodingKind encoding, int targetPageBytes,
        out int rowsPerPage)
    {
        rowsPerPage = 0;
        if (encoding != EncodingKind.Plain)
            return false;

        if (column.PhysicalType == ParquetPhysicalType.Boolean)
        {
            rowsPerPage = targetPageBytes > int.MaxValue / 8 ? int.MaxValue : targetPageBytes * 8;
            return true;
        }

        if (!TryGetFixedWidthByteCount(column, out var valueByteCount))
            return false;

        rowsPerPage = Math.Max(1, targetPageBytes / valueByteCount);
        return true;
    }

    static bool TryGetFixedWidthByteCount(Column column, out int valueByteCount)
    {
        valueByteCount = 0;
        valueByteCount = column.PhysicalType switch
        {
            ParquetPhysicalType.Int32 or ParquetPhysicalType.Float => sizeof(int),
            ParquetPhysicalType.Int64 or ParquetPhysicalType.Double => sizeof(long),
            ParquetPhysicalType.Int96 => 12,
            ParquetPhysicalType.FixedLenByteArray when column.Options.TypeLength is > 0 and <= int.MaxValue
                => checked((int)column.Options.TypeLength),
            _ => 0
        };
        return valueByteCount > 0;
    }

    static void WriteDictionaryDataPages(BufferWriterFactory bufferWriters, int totalRowCount,
        EncodingKind dictionaryEncoding, PageList pages, ReadOnlySpan<int> dictionaryIndexes, int dictionaryBitWidth,
        IPageStrategy strategy)
    {
        var rowsPerTargetPage = TryGetDictionaryRowsPerPage(strategy, dictionaryBitWidth, out var rowsPerPage)
            ? rowsPerPage
            : 0;
        var rowsWritten = 0;
        while (rowsWritten < totalRowCount)
        {
            var pageStart = rowsWritten;
            var pageRowCount = 0;
            while (rowsWritten < totalRowCount)
            {
                rowsWritten++;
                pageRowCount++;
                if (rowsWritten == totalRowCount)
                    break;
                if (rowsPerTargetPage > 0 && pageRowCount >= rowsPerTargetPage)
                    break;
                if (rowsPerTargetPage == 0 && strategy.ShouldStartNewDataPage(totalRowCount, rowsWritten, pageRowCount))
                    break;
            }

            var pageIndex = AddNewDataPage(bufferWriters, pages);
            ref var page = ref pages[pageIndex];
            if (dictionaryIndexes.IsEmpty)
                throw new InvalidOperationException("Dictionary index buffer is missing for dictionary-encoded page.");
            DictionaryIndexEncodingDispatcher.WriteIndexes(dictionaryEncoding,
                dictionaryIndexes.Slice(pageStart, pageRowCount), dictionaryBitWidth, ref page.Content);
            WriteDataPageHeader(ref page, pageRowCount, pageRowCount, 0, 0, 0, dictionaryEncoding);
        }
    }

    static bool TryGetDictionaryRowsPerPage(IPageStrategy strategy, int dictionaryBitWidth, out int rowsPerPage)
    {
        rowsPerPage = 0;
        if (!strategy.TryGetTargetDataPageSizeBytes(out var targetPageBytes))
            return false;

        if (dictionaryBitWidth <= 0)
        {
            rowsPerPage = int.MaxValue;
            return true;
        }

        var targetBits = (long)Math.Max(1, targetPageBytes - 1) * 8;
        rowsPerPage = (int)Math.Clamp(targetBits / dictionaryBitWidth, 1, int.MaxValue);
        return true;
    }

    static bool TryGetPlainEncodedValueSize(Column column, Type valueType, out int valueBytes)
    {
        valueBytes = 0;
        if (column.PhysicalType == ParquetPhysicalType.Boolean)
        {
            valueBytes = 1;
            return true;
        }

        if (TryGetFixedWidthByteCount(column, out valueBytes))
            return true;

        if (column.PhysicalType != ParquetPhysicalType.ByteArray)
            return false;

        if (valueType == typeof(byte[]) || valueType == typeof(ReadOnlyMemory<byte>) || valueType == typeof(string))
            return true;

        return false;
    }

    static int GetVariablePlainValueBytes<T>(Column column, T value)
        where T : notnull
    {
        if (typeof(T) == typeof(byte[]))
        {
            var bytes = Unsafe.As<T, byte[]>(ref value) ?? throw new InvalidOperationException(
                $"Column '{column.Name}' does not support null values.");
            return checked(sizeof(int) + bytes.Length);
        }

        if (typeof(T) == typeof(ReadOnlyMemory<byte>))
        {
            var memory = Unsafe.As<T, ReadOnlyMemory<byte>>(ref value);
            return checked(sizeof(int) + memory.Length);
        }

        if (typeof(T) == typeof(string))
        {
            var text = Unsafe.As<T, string>(ref value) ?? throw new InvalidOperationException(
                $"Column '{column.Name}' does not support null values.");
            return checked(sizeof(int) + Utf8.GetByteCount(text));
        }

        throw new InvalidOperationException(
            $"Column '{column.Name}' cannot estimate plain encoded size for value type '{typeof(T)}'.");
    }

    internal static void EncodeOptional<T>(BufferWriterFactory bufferWriters, Column column, ReadOnlySpan<T?> values,
        IPageStrategy strategy, PageList pages, LeafProjectionInfo leafProjectionInfo,
        ReusableDictionaryState<T> dictionaryState)
        where T : struct
    {
        ArgumentNullException.ThrowIfNull(column);
        ArgumentNullException.ThrowIfNull(strategy);
        ArgumentNullException.ThrowIfNull(pages);
        if (column.Options.Repetition != ParquetRepetition.Optional)
            throw new InvalidOperationException(
                $"Column '{column.Name}' does not support null values.");
        if (leafProjectionInfo.MaxDefinitionLevel != 1 || leafProjectionInfo.MaxRepetitionLevel != 0)
            throw new NotSupportedException(
                $"Column '{column.Name}' optional flat encoding requires a single optional leaf.");

        pages.Clear();
        if (values.Length == 0)
            return;

        var presentCount = CountPresentValues(values);
        var rentedValues = ArrayRenter<T>.Shared.Rent(presentCount);
        var densePresentValues = rentedValues.AsSpan(0, presentCount);
        CopyPresentValues(values, densePresentValues);
        try
        {
            EncodeOptionalFlatValues(bufferWriters, column, values, strategy, pages, densePresentValues, dictionaryState);
        }
        finally
        {
            ArrayRenter<T>.Shared.Return(rentedValues);
        }
    }

    internal static void EncodeOptional<T>(BufferWriterFactory bufferWriters, Column column, ReadOnlySpan<T> values,
        IPageStrategy strategy, PageList pages, LeafProjectionInfo leafProjectionInfo,
        ReusableDictionaryState<T> dictionaryState)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(column);
        ArgumentNullException.ThrowIfNull(strategy);
        ArgumentNullException.ThrowIfNull(pages);
        if (column.Options.Repetition != ParquetRepetition.Optional)
            throw new InvalidOperationException(
                $"Column '{column.Name}' does not support null values.");
        if (leafProjectionInfo.MaxDefinitionLevel != 1 || leafProjectionInfo.MaxRepetitionLevel != 0)
            throw new NotSupportedException(
                $"Column '{column.Name}' optional flat encoding requires a single optional leaf.");

        pages.Clear();
        if (values.Length == 0)
            return;

        var presentCount = CountPresentValues(values);
        if (presentCount == values.Length)
        {
            EncodeOptionalFlatReferences(bufferWriters, column, values, strategy, pages, values, dictionaryState);
            return;
        }

        var rentedValues = ArrayRenter<T>.Shared.Rent(presentCount);
        var densePresentValues = rentedValues.AsSpan(0, presentCount);
        CopyPresentValues(values, densePresentValues);
        try
        {
            EncodeOptionalFlatReferences(bufferWriters, column, values, strategy, pages, densePresentValues, dictionaryState);
        }
        finally
        {
            ArrayRenter<T>.Shared.Return(rentedValues, clearArray: RuntimeHelpers.IsReferenceOrContainsReferences<T>());
        }
    }

    static bool TryWriteDictionaryPage<T>(BufferWriterFactory bufferWriters, Column column, ReadOnlySpan<T> values,
        IPageStrategy strategy, PageList pages, ReusableDictionaryState<T> dictionaryState, out int dictionaryValueCount,
        out byte[]? dictionaryIndexesBytes)
        where T : notnull
    {
        dictionaryValueCount = 0;
        dictionaryIndexesBytes = null;

        if (values.IsEmpty)
            return false;

        var dictionaryMode = strategy.GetDictionaryMode();
        if (dictionaryMode == DictionaryMode.Disabled)
        {
            return false;
        }

        var dictionaryPageIndex = AddDictionaryPage(bufferWriters, pages);
        ref var dictionaryPage = ref pages[dictionaryPageIndex];
        var indexByteLength = checked(values.Length * sizeof(int));
        byte[]? rentedIndexesBytes = bufferWriters.RentScratch(checked((uint)Math.Max(indexByteLength, sizeof(int))));
        try
        {
            var indexes = MemoryMarshal.Cast<byte, int>(rentedIndexesBytes.AsSpan(0, indexByteLength));
            if (typeof(T) == typeof(bool))
            {
                WriteBooleanDictionaryPage(column, Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<bool>>(ref values),
                    ref dictionaryPage, indexes,
                    out dictionaryValueCount);

                dictionaryIndexesBytes = rentedIndexesBytes;
                rentedIndexesBytes = null;
                return true;
            }

            var initialUniqueCapacity = dictionaryMode == DictionaryMode.Forced
                ? GetInitialForcedDictionaryCapacity(values.Length)
                : Math.Max(256, values.Length / 2);
            var comparer = GetDictionaryComparer<T>();
            var knownSortOrder = strategy.GetDictionarySortOrder();
            dictionaryState.Reset(initialUniqueCapacity, knownSortOrder == DictionarySortOrder.Unsorted, comparer);
            indexes[0] = dictionaryState.AddFirst(values[0]);

            var currentSortedIndex = 0;
            var sortedDirection = knownSortOrder switch
            {
                DictionarySortOrder.Ascending => 1,
                DictionarySortOrder.Descending => -1,
                _ => 0
            };
            if (!dictionaryState.IsMapEnabled && values.Length > 1 && sortedDirection == 0
                && TryCompareForSort(values[0], values[1], out var firstComparison)
                && firstComparison != 0)
                sortedDirection = firstComparison < 0 ? 1 : -1;
            var nextDropCheckRow = dictionaryMode == DictionaryMode.Maybe
                ? Math.Min(DictionaryDropCheckPeriodRows, values.Length)
                : 0;
            for (var i = 1; i < values.Length; i++)
            {
                var value = values[i];
                if (!dictionaryState.IsMapEnabled)
                {
                    var previous = values[i - 1];
                    if (comparer.Equals(value, previous))
                    {
                        indexes[i] = currentSortedIndex;
                    }
                    else if (TryCompareForSort(previous, value, out var comparison)
                             && IsSortedStep(comparison, ref sortedDirection))
                    {
                        currentSortedIndex = dictionaryState.AddSortedUnique(value);
                        indexes[i] = currentSortedIndex;
                    }
                    else
                    {
                        if (knownSortOrder != DictionarySortOrder.Unsorted)
                        {
                            strategy.SetDictionarySortOrder(DictionarySortOrder.Unsorted);
                            knownSortOrder = DictionarySortOrder.Unsorted;
                        }
                        dictionaryState.EnableMap();
                        indexes[i] = dictionaryState.GetOrAddIndex(value);
                    }
                }
                else
                {
                    indexes[i] = dictionaryState.GetOrAddIndex(value);
                }

                if (dictionaryMode != DictionaryMode.Maybe)
                    continue;
                var rowsSeen = i + 1;
                if (rowsSeen != nextDropCheckRow && rowsSeen != values.Length)
                    continue;
                if (strategy.ShouldDropDictionary(dictionaryState.Count, values.Length, rowsSeen))
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

            if (!dictionaryState.IsMapEnabled)
            {
                var discoveredSortOrder = sortedDirection switch
                {
                    1 => DictionarySortOrder.Ascending,
                    -1 => DictionarySortOrder.Descending,
                    _ => DictionarySortOrder.Unknown
                };
                if (discoveredSortOrder != knownSortOrder)
                    strategy.SetDictionarySortOrder(discoveredSortOrder);
            }

            PlainEncoding.WriteValues(column, dictionaryState.AsSpan(), ref dictionaryPage.Content);

            dictionaryPage.SetDictionaryPageMetadata(dictionaryState.Count);
            dictionaryValueCount = dictionaryState.Count;
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

    static void EncodeOptionalFlatValues<T>(BufferWriterFactory bufferWriters, Column column, ReadOnlySpan<T?> values,
        IPageStrategy strategy, PageList pages, ReadOnlySpan<T> denseValues, ReusableDictionaryState<T> dictionaryState)
        where T : struct
    {
        var dataEncoding = EncodingKindResolver.GetDataEncodingKind(column);
        var dictionaryEncoding = EncodingKindResolver.GetDictionaryEncodingKind(column);
        var useDictionary = TryWriteDictionaryPage(bufferWriters, column, denseValues, strategy, pages, dictionaryState,
            out var dictionaryValueCount, out var dictionaryIndexesBytes);
        var dictionaryIndexes = useDictionary && dictionaryIndexesBytes is not null
            ? MemoryMarshal.Cast<byte, int>(dictionaryIndexesBytes.AsSpan(0, checked(denseValues.Length * sizeof(int))))
            : default;
        var dictionaryBitWidth = useDictionary
            ? RleBitPackingHybridEncoding.GetBitWidthFromMaxValue(dictionaryValueCount <= 1 ? 0 : dictionaryValueCount - 1)
            : 0;
        var useTargetPageBytes = TryGetOptionalPageSizer(column, dataEncoding, useDictionary, dictionaryBitWidth,
            strategy, out var targetPageBytes, out var presentValueBytes);

        var rowsWritten = 0;
        var denseOffset = 0;
        try
        {
            while (rowsWritten < values.Length)
            {
                var pageStart = rowsWritten;
                var pageRowCount = 0;
                var pageBytes = 0;
                while (rowsWritten < values.Length)
                {
                    var rowBytes = 0;
                    if (useTargetPageBytes)
                    {
                        rowBytes = GetOptionalRowBytes(column, values[rowsWritten], presentValueBytes);
                        if (pageRowCount > 0 && pageBytes + rowBytes > targetPageBytes)
                            break;
                    }

                    rowsWritten++;
                    pageRowCount++;
                    if (useTargetPageBytes)
                        pageBytes = checked(pageBytes + rowBytes);
                    if (rowsWritten == values.Length)
                        break;
                    if (!useTargetPageBytes && strategy.ShouldStartNewDataPage(values.Length, rowsWritten, pageRowCount))
                        break;
                }

                var pageIndex = AddNewDataPage(bufferWriters, pages);
                ref var page = ref pages[pageIndex];
                var pageRows = values.Slice(pageStart, pageRowCount);
                var nullCount = 0;
                var presentRows = 0;
                var definitionLength = WriteOptionalDefinitionLevels(pageRows, ref nullCount, ref presentRows, ref page.Content);
                var pageDenseValues = denseValues.Slice(denseOffset, presentRows);
                if (useDictionary)
                {
                    if (dictionaryIndexes.IsEmpty)
                        throw new InvalidOperationException("Dictionary index buffer is missing for dictionary-encoded page.");
                    DictionaryIndexEncodingDispatcher.WriteIndexes(dictionaryEncoding,
                        dictionaryIndexes.Slice(denseOffset, presentRows), dictionaryBitWidth, ref page.Content);
                }
                else if (presentRows > 0)
                    ValueEncodingDispatcher.WriteValues(dataEncoding, column, pageDenseValues, bufferWriters, ref page.Content);

                WriteDataPageHeader(ref page, pageRowCount, pageRowCount, nullCount, 0, definitionLength,
                    useDictionary ? dictionaryEncoding : dataEncoding);
                denseOffset += presentRows;
            }
        }
        finally
        {
            if (dictionaryIndexesBytes is not null)
                bufferWriters.ReturnScratch(dictionaryIndexesBytes);
        }
    }

    static void EncodeOptionalFlatReferences<T>(BufferWriterFactory bufferWriters, Column column, ReadOnlySpan<T> values,
        IPageStrategy strategy, PageList pages, ReadOnlySpan<T> denseValues, ReusableDictionaryState<T> dictionaryState)
        where T : class
    {
        var dataEncoding = EncodingKindResolver.GetDataEncodingKind(column);
        var dictionaryEncoding = EncodingKindResolver.GetDictionaryEncodingKind(column);
        var useDictionary = TryWriteDictionaryPage(bufferWriters, column, denseValues, strategy, pages, dictionaryState,
            out var dictionaryValueCount, out var dictionaryIndexesBytes);
        var dictionaryIndexes = useDictionary && dictionaryIndexesBytes is not null
            ? MemoryMarshal.Cast<byte, int>(dictionaryIndexesBytes.AsSpan(0, checked(denseValues.Length * sizeof(int))))
            : default;
        var dictionaryBitWidth = useDictionary
            ? RleBitPackingHybridEncoding.GetBitWidthFromMaxValue(dictionaryValueCount <= 1 ? 0 : dictionaryValueCount - 1)
            : 0;
        var useTargetPageBytes = TryGetOptionalPageSizer(column, dataEncoding, useDictionary, dictionaryBitWidth,
            strategy, out var targetPageBytes, out var presentValueBytes);

        var rowsWritten = 0;
        var denseOffset = 0;
        try
        {
            while (rowsWritten < values.Length)
            {
                var pageStart = rowsWritten;
                var pageRowCount = 0;
                var pageBytes = 0;
                while (rowsWritten < values.Length)
                {
                    var rowBytes = 0;
                    if (useTargetPageBytes)
                    {
                        rowBytes = GetOptionalRowBytes(column, values[rowsWritten], presentValueBytes);
                        if (pageRowCount > 0 && pageBytes + rowBytes > targetPageBytes)
                            break;
                    }

                    rowsWritten++;
                    pageRowCount++;
                    if (useTargetPageBytes)
                        pageBytes = checked(pageBytes + rowBytes);
                    if (rowsWritten == values.Length)
                        break;
                    if (!useTargetPageBytes && strategy.ShouldStartNewDataPage(values.Length, rowsWritten, pageRowCount))
                        break;
                }

                var pageIndex = AddNewDataPage(bufferWriters, pages);
                ref var page = ref pages[pageIndex];
                var pageRows = values.Slice(pageStart, pageRowCount);
                var nullCount = 0;
                var presentRows = 0;
                var definitionLength = WriteOptionalDefinitionLevels(pageRows, ref nullCount, ref presentRows, ref page.Content);
                var pageDenseValues = denseValues.Slice(denseOffset, presentRows);
                if (useDictionary)
                {
                    if (dictionaryIndexes.IsEmpty)
                        throw new InvalidOperationException("Dictionary index buffer is missing for dictionary-encoded page.");
                    DictionaryIndexEncodingDispatcher.WriteIndexes(dictionaryEncoding,
                        dictionaryIndexes.Slice(denseOffset, presentRows), dictionaryBitWidth, ref page.Content);
                }
                else if (presentRows > 0)
                    ValueEncodingDispatcher.WriteValues(dataEncoding, column, pageDenseValues, bufferWriters, ref page.Content);

                WriteDataPageHeader(ref page, pageRowCount, pageRowCount, nullCount, 0, definitionLength,
                    useDictionary ? dictionaryEncoding : dataEncoding);
                denseOffset += presentRows;
            }
        }
        finally
        {
            if (dictionaryIndexesBytes is not null)
                bufferWriters.ReturnScratch(dictionaryIndexesBytes);
        }
    }

    static bool TryGetOptionalPageSizer(Column column, EncodingKind dataEncoding, bool useDictionary,
        int dictionaryBitWidth, IPageStrategy strategy, out int targetPageBytes, out int presentValueBytes)
    {
        targetPageBytes = 0;
        presentValueBytes = 0;
        if (!strategy.TryGetTargetDataPageSizeBytes(out targetPageBytes))
            return false;

        if (!useDictionary && dataEncoding == EncodingKind.Plain
            && TryGetPlainEncodedValueSize(column, column.PhysicalType == ParquetPhysicalType.ByteArray
                ? typeof(byte[])
                : typeof(int), out presentValueBytes))
            return true;

        if (!useDictionary)
            return false;

        presentValueBytes = dictionaryBitWidth <= 0
            ? 0
            : Math.Max(1, (dictionaryBitWidth + 7) / 8);
        return true;
    }

    static int GetOptionalRowBytes<T>(Column column, T? value, int presentValueBytes)
        where T : struct
        => 1 + (value is { } presentValue ? GetOptionalPresentValueBytes(column, presentValue, presentValueBytes) : 0);

    static int GetOptionalRowBytes<T>(Column column, T value, int presentValueBytes)
        where T : class
        => 1 + (value is null ? 0 : GetOptionalPresentValueBytes(column, value, presentValueBytes));

    static int GetOptionalPresentValueBytes<T>(Column column, T value, int presentValueBytes)
        where T : notnull
        => presentValueBytes > 0 ? presentValueBytes : GetVariablePlainValueBytes(column, value);

    static int WriteOptionalDefinitionLevels<T>(ReadOnlySpan<T?> values, ref int nullCount, ref int presentRows,
        ref BufferWriter writer)
        where T : struct
    {
        var definitionBitWidth = 1;
        var currentLevel = -1;
        var currentRunLength = 0;
        for (var i = 0; i < values.Length; i++)
        {
            var level = values[i].HasValue ? 1 : 0;
            if (level == 0)
                nullCount++;
            else
                presentRows++;

            if (currentRunLength == 0)
            {
                currentLevel = level;
                currentRunLength = 1;
                continue;
            }

            if (currentLevel == level)
            {
                currentRunLength++;
                continue;
            }

            WriteLevelRun(currentLevel, currentRunLength, definitionBitWidth, ref writer);
            currentLevel = level;
            currentRunLength = 1;
        }

        if (currentRunLength > 0)
            WriteLevelRun(currentLevel, currentRunLength, definitionBitWidth, ref writer);
        return writer.WrittenLength;
    }

    static int WriteOptionalDefinitionLevels<T>(ReadOnlySpan<T> values, ref int nullCount, ref int presentRows,
        ref BufferWriter writer)
        where T : class
    {
        var definitionBitWidth = 1;
        var currentLevel = -1;
        var currentRunLength = 0;
        for (var i = 0; i < values.Length; i++)
        {
            var level = values[i] is null ? 0 : 1;
            if (level == 0)
                nullCount++;
            else
                presentRows++;

            if (currentRunLength == 0)
            {
                currentLevel = level;
                currentRunLength = 1;
                continue;
            }

            if (currentLevel == level)
            {
                currentRunLength++;
                continue;
            }

            WriteLevelRun(currentLevel, currentRunLength, definitionBitWidth, ref writer);
            currentLevel = level;
            currentRunLength = 1;
        }

        if (currentRunLength > 0)
            WriteLevelRun(currentLevel, currentRunLength, definitionBitWidth, ref writer);
        return writer.WrittenLength;
    }

    static int CountPresentValues<T>(ReadOnlySpan<T?> values)
        where T : struct
    {
        var count = 0;
        for (var i = 0; i < values.Length; i++)
            if (values[i].HasValue)
                count++;
        return count;
    }

    static int CountPresentValues<T>(ReadOnlySpan<T> values)
        where T : class
    {
        var count = 0;
        for (var i = 0; i < values.Length; i++)
            if (values[i] is not null)
                count++;
        return count;
    }

    static void CopyPresentValues<T>(ReadOnlySpan<T?> values, Span<T> destination)
        where T : struct
    {
        var index = 0;
        for (var i = 0; i < values.Length; i++)
            if (values[i].HasValue)
                destination[index++] = values[i]!.Value;
    }

    static void CopyPresentValues<T>(ReadOnlySpan<T> values, Span<T> destination)
        where T : class
    {
        var index = 0;
        for (var i = 0; i < values.Length; i++)
            if (values[i] is not null)
                destination[index++] = values[i];
    }

    static void WriteBooleanDictionaryPage(Column column, ReadOnlySpan<bool> values, ref Page dictionaryPage,
        Span<int> indexes, out int dictionaryValueCount)
    {
        for (var i = 0; i < values.Length; i++)
            indexes[i] = values[i] ? 1 : 0;

        Span<bool> dictionaryValues = stackalloc bool[2];
        dictionaryValues[0] = false;
        dictionaryValues[1] = true;
        PlainEncoding.WriteValues(column, dictionaryValues, ref dictionaryPage.Content);

        dictionaryPage.SetDictionaryPageMetadata(2);
        dictionaryValueCount = 2;
    }

    static bool IsSortedStep(int comparison, ref int sortedDirection)
    {
        if (comparison == 0)
            return true;
        if (sortedDirection == 0)
        {
            sortedDirection = comparison < 0 ? 1 : -1;
            return true;
        }

        return sortedDirection == 1 ? comparison < 0 : comparison > 0;
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
                if (typeof(T) == typeof(ReadOnlyMemory<byte>[][]))
                {
                    if (leafProjectionInfo.ElementOptional)
                        throw new InvalidOperationException(
                            $"Column '{column.Name}' has optional list elements; use nullable row element type for this column.");

                    EncodeRepeatedRowsCore(bufferWriters, column, dataEncoding,
                        Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<ReadOnlyMemory<byte>[]>>(ref rows), ref page, leafProjectionInfo);
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
        if (typeof(T) == typeof(ReadOnlyMemory<byte>))
            return (IEqualityComparer<T>)(object)ReadOnlyMemoryByteComparer.Instance;

        return EqualityComparer<T>.Default;
    }

    static int GetInitialForcedDictionaryCapacity(int rowCount)
        => Math.Max(256, Math.Min(rowCount, 65_536));

    static bool TryCompareForSort<T>(T left, T right, out int comparison)
    {
        if (typeof(T) == typeof(bool))
        {
            comparison = Unsafe.As<T, bool>(ref left).CompareTo(Unsafe.As<T, bool>(ref right));
            return true;
        }
        if (typeof(T) == typeof(int))
        {
            comparison = Unsafe.As<T, int>(ref left).CompareTo(Unsafe.As<T, int>(ref right));
            return true;
        }
        if (typeof(T) == typeof(byte))
        {
            comparison = Unsafe.As<T, byte>(ref left).CompareTo(Unsafe.As<T, byte>(ref right));
            return true;
        }
        if (typeof(T) == typeof(ushort))
        {
            comparison = Unsafe.As<T, ushort>(ref left).CompareTo(Unsafe.As<T, ushort>(ref right));
            return true;
        }
        if (typeof(T) == typeof(uint))
        {
            comparison = Unsafe.As<T, uint>(ref left).CompareTo(Unsafe.As<T, uint>(ref right));
            return true;
        }
        if (typeof(T) == typeof(long))
        {
            comparison = Unsafe.As<T, long>(ref left).CompareTo(Unsafe.As<T, long>(ref right));
            return true;
        }
        if (typeof(T) == typeof(ulong))
        {
            comparison = Unsafe.As<T, ulong>(ref left).CompareTo(Unsafe.As<T, ulong>(ref right));
            return true;
        }
        if (typeof(T) == typeof(float))
        {
            comparison = Unsafe.As<T, float>(ref left).CompareTo(Unsafe.As<T, float>(ref right));
            return true;
        }
        if (typeof(T) == typeof(double))
        {
            comparison = Unsafe.As<T, double>(ref left).CompareTo(Unsafe.As<T, double>(ref right));
            return true;
        }
        if (typeof(T) == typeof(string))
        {
            comparison = string.CompareOrdinal(Unsafe.As<T, string>(ref left), Unsafe.As<T, string>(ref right));
            return true;
        }
        if (typeof(T) == typeof(byte[]))
        {
            comparison = Unsafe.As<T, byte[]>(ref left).AsSpan().SequenceCompareTo(Unsafe.As<T, byte[]>(ref right).AsSpan());
            return true;
        }
        if (typeof(T) == typeof(ReadOnlyMemory<byte>))
        {
            comparison = Unsafe.As<T, ReadOnlyMemory<byte>>(ref left).Span.SequenceCompareTo(
                Unsafe.As<T, ReadOnlyMemory<byte>>(ref right).Span);
            return true;
        }

        comparison = 0;
        return false;
    }

}
