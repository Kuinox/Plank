using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Plank.Schema;
using Plank.Writing.PageStrategy;

namespace Plank.Writing.Encoding;

static class Encoding
{
    const int DictionaryDropCheckPeriodRows = 2048;
    const int MaximumInitialForcedDictionaryCapacity = 2048;

    internal static bool Encode<T>(BufferWriterFactory bufferWriters, Column column, ReadOnlySpan<T> values,
        PageStrategyContext strategyContext, PageList pages, ParquetDataPageVersion dataPageVersion,
        LeafProjectionInfo leafProjectionInfo, ReusableDictionaryState<T> dictionaryState,
        out PlainBinaryMinMax binaryMinMax)
        where T : notnull
    {
        ArgumentNullException.ThrowIfNull(column);
        ArgumentNullException.ThrowIfNull(strategyContext);
        ArgumentNullException.ThrowIfNull(pages);
        var strategy = strategyContext.Strategy;

        binaryMinMax = default;
        pages.Clear();
        if (values.Length == 0)
            return false;

        if (column.Options.Repetition == ParquetRepetition.Repeated)
        {
            EncodeRepeatedRows(bufferWriters, column, values, pages, dataPageVersion, leafProjectionInfo);
            return false;
        }

        var dataEncoding = EncodingKindResolver.GetDataEncodingKind(column);
        var dictionaryEncoding = EncodingKindResolver.GetDictionaryEncodingKind(column);
        var useDictionary = TryWriteDictionaryPage(bufferWriters, column, values, strategyContext, pages, dictionaryState,
            out var dictionaryValueCount, out var dictionaryIndexesBuffer);
        var dictionaryIndexes = useDictionary && !dictionaryIndexesBuffer.IsEmpty
            ? MemoryMarshal.Cast<byte, int>(dictionaryIndexesBuffer.Span[..checked(values.Length * sizeof(int))])
            : default;
        var dictionaryBitWidth = useDictionary
            ? EncodingPrimitives.GetBitWidthFromMaxValue(dictionaryValueCount <= 1 ? 0 : dictionaryValueCount - 1)
            : 0;

        try
        {
            if (useDictionary)
            {
                WriteDictionaryDataPages(bufferWriters, values.Length, dictionaryEncoding, pages, dictionaryIndexes,
                    dictionaryBitWidth, strategy);
                return typeof(T) != typeof(bool);
            }

            if (TryWriteFixedWidthDataPages(bufferWriters, column, values, dataEncoding, strategy, pages))
                return false;
            if (TryWriteSizeBoundedDataPages(bufferWriters, column, values, dataEncoding, strategy, pages,
                    ref binaryMinMax))
                return false;

            WriteStrategyDataPages(bufferWriters, column, values, dataEncoding, strategy, pages);
            return false;
        }
        finally
        {
            dictionaryIndexesBuffer.Dispose();
        }
    }

    internal static void EncodeRequiredDateTimeDictionary(BufferWriterFactory bufferWriters, Column column,
        ReadOnlySpan<DateTime> values, LogicalType.Timestamp timestamp, PageStrategyContext strategyContext,
        PageList pages, ReusableDictionaryState<DateTime> dictionaryState)
    {
        ArgumentNullException.ThrowIfNull(column);
        ArgumentNullException.ThrowIfNull(strategyContext);
        ArgumentNullException.ThrowIfNull(pages);
        if (column.Options.Repetition != ParquetRepetition.Required
            || column.PhysicalType != ParquetPhysicalType.Int64)
            throw new InvalidOperationException(
                $"Column '{column.Name}' must be a required INT64 column for direct timestamp dictionary encoding.");
        if (strategyContext.Strategy.GetDictionaryMode() != DictionaryMode.Forced)
            throw new InvalidOperationException(
                $"Column '{column.Name}' must force dictionary encoding for the direct timestamp path.");

        pages.Clear();
        if (values.IsEmpty)
            return;

        var dictionaryEncoding = EncodingKindResolver.GetDictionaryEncodingKind(column);
        if (!TryWriteDictionaryPage(bufferWriters, column, values, strategyContext, pages, dictionaryState,
                out var dictionaryValueCount, out var dictionaryIndexesBuffer, timestamp))
            throw new InvalidOperationException(
                $"Column '{column.Name}' did not produce a required timestamp dictionary page.");

        try
        {
            var dictionaryIndexes = MemoryMarshal.Cast<byte, int>(
                dictionaryIndexesBuffer.Span[..checked(values.Length * sizeof(int))]);
            var dictionaryBitWidth = EncodingPrimitives.GetBitWidthFromMaxValue(
                dictionaryValueCount <= 1 ? 0 : dictionaryValueCount - 1);
            WriteDictionaryDataPages(bufferWriters, values.Length, dictionaryEncoding, pages, dictionaryIndexes,
                dictionaryBitWidth, strategyContext.Strategy);
        }
        finally
        {
            dictionaryIndexesBuffer.Dispose();
        }
    }

    static int GetStrategyPageRowCount(IPageStrategy strategy, int totalRowCount, int rowsWritten)
    {
        var requestedRowCount = strategy.GetNextDataPageRowCount(checked((uint)totalRowCount),
            checked((uint)rowsWritten));
        var remainingRowCount = checked((uint)(totalRowCount - rowsWritten));
        if (requestedRowCount == 0 || requestedRowCount > remainingRowCount)
            throw new InvalidOperationException(
                $"Page strategy returned {requestedRowCount} rows for a page with {remainingRowCount} rows remaining.");

        return checked((int)requestedRowCount);
    }

    static void WriteStrategyDataPages<T>(BufferWriterFactory bufferWriters, Column column, ReadOnlySpan<T> values,
        EncodingKind dataEncoding, IPageStrategy strategy, PageList pages)
        where T : notnull
    {
        var rowsWritten = 0;
        while (rowsWritten < values.Length)
        {
            var pageStart = rowsWritten;
            var pageRowCount = GetStrategyPageRowCount(strategy, values.Length, rowsWritten);
            rowsWritten += pageRowCount;

            WriteDataPage(bufferWriters, column, values.Slice(pageStart, pageRowCount), dataEncoding, pages);
        }
    }

    static bool TryWriteFixedWidthDataPages<T>(BufferWriterFactory bufferWriters, Column column, ReadOnlySpan<T> values,
        EncodingKind dataEncoding, IPageStrategy strategy, PageList pages)
        where T : notnull
    {
        if (!strategy.TryGetTargetDataPageSizeBytes(out var targetPageBytes))
            return false;
        if (!TryGetFixedWidthRowsPerPage(column, dataEncoding, checked((int)targetPageBytes), out var rowsPerPage))
            return false;

        for (var pageStart = 0; pageStart < values.Length; pageStart += rowsPerPage)
        {
            var pageRowCount = Math.Min(rowsPerPage, values.Length - pageStart);
            WriteDataPage(bufferWriters, column, values.Slice(pageStart, pageRowCount), dataEncoding, pages);
        }

        return true;
    }

    static bool TryWriteSizeBoundedDataPages<T>(BufferWriterFactory bufferWriters, Column column,
        ReadOnlySpan<T> values,
        EncodingKind dataEncoding, IPageStrategy strategy, PageList pages, ref PlainBinaryMinMax binaryMinMax)
        where T : notnull
    {
        if (!strategy.TryGetTargetDataPageSizeBytes(out var targetPageBytes))
            return false;

        if (dataEncoding is EncodingKind.DeltaByteArray or EncodingKind.DeltaLengthByteArray)
        {
            if (column.PhysicalType != ParquetPhysicalType.ByteArray
                || typeof(T) != typeof(byte[]) && typeof(T) != typeof(ReadOnlyMemory<byte>))
                return false;

            WriteVariableByteArrayDataPages(bufferWriters, column, values, dataEncoding, pages,
                checked((int)targetPageBytes));
            return true;
        }

        if (dataEncoding != EncodingKind.Plain)
            return false;
        // TryWriteFixedWidthDataPages already claimed every fixed-width Plain shape - it covers
        // Boolean plus exactly the types TryGetFixedWidthByteCount recognises, which is the same set
        // TryGetPlainEncodedValueSize reports a non-zero size for. So the only Plain columns that
        // reach here are variable-length byte arrays.
        if (!TryGetPlainEncodedValueSize(column, typeof(T), out _))
            return false;

        WritePlainByteArrayDataPages(bufferWriters, column, values, pages, checked((int)targetPageBytes),
            ref binaryMinMax);
        return true;
    }

    /// <summary>
    /// Plain BYTE_ARRAY pages, filled one page per pass over the values.
    /// </summary>
    /// <remarks>
    /// The delta encodings still need <see cref="WriteVariableByteArrayDataPages"/>, because their
    /// encoded size is not the plain size the page budget is measured in. Plain does not: the page
    /// writer knows exactly how many bytes each value contributes as it copies it, so it can reserve
    /// the page budget up front and advance by what it actually wrote. That removes the separate
    /// sizing walk over the value array, and with it the second walk
    /// <see cref="PlainEncoding.WriteRequiredByteArrayPayloads"/> needed to size its own destination.
    /// The page boundary rule is unchanged, so the encoded bytes are identical. The pass also reports
    /// the column's min and max through <paramref name="binaryMinMax"/>, so the statistics that follow
    /// do not have to walk the values a second time.
    /// </remarks>
    static void WritePlainByteArrayDataPages<T>(BufferWriterFactory bufferWriters, Column column,
        ReadOnlySpan<T> values, PageList pages, int targetPageBytes, ref PlainBinaryMinMax binaryMinMax)
        where T : notnull
    {
        // TryGetPlainEncodedValueSize admits exactly these two row shapes for a BYTE_ARRAY column.
        if (typeof(T) == typeof(byte[]))
        {
            WritePlainByteArrayDataPagesCore(bufferWriters, column,
                Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<byte[]>>(ref values), pages, targetPageBytes,
                ref binaryMinMax);
            return;
        }

        if (typeof(T) == typeof(ReadOnlyMemory<byte>))
        {
            WritePlainMemoryDataPagesCore(bufferWriters, column,
                Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<ReadOnlyMemory<byte>>>(ref values), pages, targetPageBytes,
                ref binaryMinMax);
            return;
        }

        throw new InvalidOperationException(
            $"Column '{column.Name}' cannot plain encode BYTE_ARRAY values of type '{typeof(T)}'.");
    }

    // byte[] is a shared-generic reference-type instantiation, so the row-shape dispatch above only
    // folds away once the loop is concrete. Keep this and the ReadOnlyMemory<byte> variant separate.
    static void WritePlainByteArrayDataPagesCore(BufferWriterFactory bufferWriters, Column column,
        ReadOnlySpan<byte[]> values, PageList pages, int targetPageBytes, ref PlainBinaryMinMax binaryMinMax)
    {
        var rowsWritten = 0;
        while (rowsWritten < values.Length)
        {
            var pageIndex = AddNewDataPage(bufferWriters, pages);
            ref var page = ref pages[pageIndex];
            var pageRowCount = PlainEncoding.WriteRequiredByteArrayPage(column, values, rowsWritten,
                targetPageBytes, ref page.Content, ref binaryMinMax);
            rowsWritten += pageRowCount;
            WriteDataPageHeader(ref page, pageRowCount, pageRowCount, 0, 0, 0, EncodingKind.Plain);
        }
    }

    static void WritePlainMemoryDataPagesCore(BufferWriterFactory bufferWriters, Column column,
        ReadOnlySpan<ReadOnlyMemory<byte>> values, PageList pages, int targetPageBytes,
        ref PlainBinaryMinMax binaryMinMax)
    {
        var rowsWritten = 0;
        while (rowsWritten < values.Length)
        {
            var pageIndex = AddNewDataPage(bufferWriters, pages);
            ref var page = ref pages[pageIndex];
            var pageRowCount = PlainEncoding.WriteRequiredMemoryPage(values, rowsWritten, targetPageBytes,
                ref page.Content, ref binaryMinMax);
            rowsWritten += pageRowCount;
            WriteDataPageHeader(ref page, pageRowCount, pageRowCount, 0, 0, 0, EncodingKind.Plain);
        }
    }

    static void WriteVariableByteArrayDataPages<T>(BufferWriterFactory bufferWriters, Column column,
        ReadOnlySpan<T> values, EncodingKind dataEncoding, PageList pages, int targetPageBytes)
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
            int pageRowCount;
            if (rowsPerTargetPage > 0)
            {
                pageRowCount = Math.Min(rowsPerTargetPage, totalRowCount - rowsWritten);
            }
            else
            {
                pageRowCount = GetStrategyPageRowCount(strategy, totalRowCount, rowsWritten);
            }
            rowsWritten += pageRowCount;

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

        var targetBits = (long)Math.Max(1U, targetPageBytes - 1U) * 8;
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

        if (valueType == typeof(byte[]) || valueType == typeof(ReadOnlyMemory<byte>))
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

        throw new InvalidOperationException(
            $"Column '{column.Name}' cannot estimate plain encoded size for value type '{typeof(T)}'.");
    }

    internal static void EncodeOptional<T>(BufferWriterFactory bufferWriters, Column column, ReadOnlySpan<T?> values,
        PageStrategyContext strategyContext, PageList pages, ParquetDataPageVersion dataPageVersion,
        LeafProjectionInfo leafProjectionInfo, ReusableDictionaryState<T> dictionaryState)
        where T : struct
    {
        ArgumentNullException.ThrowIfNull(column);
        ArgumentNullException.ThrowIfNull(strategyContext);
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

        if (RuntimeHelpers.IsReferenceOrContainsReferences<T>())
        {
            if (typeof(T) != typeof(ReadOnlyMemory<byte>))
                throw new NotSupportedException($"Optional value column type '{typeof(T)}' is not supported.");

            var memoryValues = Unsafe.As<ReadOnlySpan<T?>, ReadOnlySpan<ReadOnlyMemory<byte>?>>(ref values);
            var memoryDictionary = (ReusableDictionaryState<ReadOnlyMemory<byte>>)(object)dictionaryState;
            var memoryMinMax = default(PlainBinaryMinMax);
            EncodeOptionalFlatByteArrays<ReadOnlyMemory<byte>?, ReadOnlyMemory<byte>,
                NullableValueRow<ReadOnlyMemory<byte>>, OptionalMemoryRow>(bufferWriters, column, memoryValues,
                strategyContext, pages, dataPageVersion, memoryDictionary, ref memoryMinMax);
            return;
        }

        var presentCount = CountPresentValues<T?, T, NullableValueRow<T>>(values);
        var rentedValues = bufferWriters.RentScratch<T>(checked((uint)presentCount));
        var densePresentValues = ParquetBuffer.AsSpan<T>(rentedValues, presentCount);
        CopyPresentValues<T?, T, NullableValueRow<T>>(values, densePresentValues);
        try
        {
            EncodeOptionalFlatValues(bufferWriters, column, values, strategyContext, pages, dataPageVersion,
                densePresentValues, dictionaryState);
        }
        finally
        {
            bufferWriters.ReturnScratch(rentedValues);
        }
    }

    internal static void EncodeOptionalConverted<TSource, TPhysical>(BufferWriterFactory bufferWriters, Column column,
        ReadOnlySpan<TSource?> values, ReadOnlySpan<TPhysical> densePresentValues,
        PageStrategyContext strategyContext, PageList pages, ParquetDataPageVersion dataPageVersion,
        LeafProjectionInfo leafProjectionInfo, ReusableDictionaryState<TPhysical> dictionaryState)
        where TSource : struct
        where TPhysical : struct
    {
        ArgumentNullException.ThrowIfNull(column);
        ArgumentNullException.ThrowIfNull(strategyContext);
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

        EncodeOptionalFlatValues(bufferWriters, column, values, strategyContext, pages, dataPageVersion,
            densePresentValues, dictionaryState);
    }

    /// <summary>
    /// Encodes the single-page optional-double dictionary shape without first compacting present values.
    /// Definition levels, dictionary indexes, and statistics are produced in the same nullable-row scan.
    /// </summary>
    internal static ColumnStatistics EncodeOptionalForcedDoubleDictionary(BufferWriterFactory bufferWriters,
        Column column, ReadOnlySpan<double?> values, PageStrategyContext strategyContext, PageList pages,
        ParquetDataPageVersion dataPageVersion, LeafProjectionInfo leafProjectionInfo,
        ReusableDictionaryState<double> dictionaryState)
    {
        ArgumentNullException.ThrowIfNull(column);
        ArgumentNullException.ThrowIfNull(strategyContext);
        ArgumentNullException.ThrowIfNull(pages);
        if (column.Options.Repetition != ParquetRepetition.Optional)
            throw new InvalidOperationException($"Column '{column.Name}' does not support null values.");
        if (leafProjectionInfo.MaxDefinitionLevel != 1 || leafProjectionInfo.MaxRepetitionLevel != 0)
            throw new NotSupportedException(
                $"Column '{column.Name}' optional flat encoding requires a single optional leaf.");
        if (strategyContext.Strategy is not ForceDictionaryPageStrategy)
            throw new InvalidOperationException(
                $"Column '{column.Name}' requires the force-dictionary page strategy for fused encoding.");

        pages.Clear();
        if (values.IsEmpty)
            return ColumnStatistics.Empty(0);

        var dictionaryPageIndex = AddDictionaryPage(bufferWriters, pages);
        var dataPageIndex = AddNewDataPage(bufferWriters, pages);
        ref var dictionaryPage = ref pages[dictionaryPageIndex];
        ref var dataPage = ref pages[dataPageIndex];
        var indexByteLength = checked(values.Length * sizeof(int));
        var rentedIndexesBuffer = bufferWriters.RentScratch(checked((uint)Math.Max(indexByteLength, sizeof(int))));
        try
        {
            var indexes = MemoryMarshal.Cast<byte, int>(rentedIndexesBuffer.Span[..indexByteLength]);
            dictionaryState.Reset(GetInitialForcedDictionaryCapacity(values.Length), useMap: true);
            Volatile.Write(ref strategyContext.DictionarySortOrder, (int)DictionarySortOrder.Unsorted);

            var lengthPrefix = ReserveLevelLengthPrefix(dataPageVersion == ParquetDataPageVersion.V1,
                ref dataPage.Content);
            var definitionStart = dataPage.Content.WrittenLength;
            var currentLevel = -1;
            var currentRunLength = 0;
            var presentCount = 0;
            var nullCount = 0;
            var nanCount = 0L;
            var min = 0.0d;
            var max = 0.0d;
            var hasStatisticsValue = false;

            for (var i = 0; i < values.Length; i++)
            {
                var level = 0;
                if (values[i] is { } value)
                {
                    level = 1;
                    indexes[presentCount++] = dictionaryState.GetOrAddIndex(value);
                    if (double.IsNaN(value))
                    {
                        nanCount++;
                    }
                    else if (!hasStatisticsValue)
                    {
                        min = value;
                        max = value;
                        hasStatisticsValue = true;
                    }
                    else
                    {
                        if (value < min || value == 0 && min == 0
                            && BitConverter.DoubleToInt64Bits(value) < BitConverter.DoubleToInt64Bits(min))
                            min = value;
                        if (value > max || value == 0 && max == 0
                            && BitConverter.DoubleToInt64Bits(value) > BitConverter.DoubleToInt64Bits(max))
                            max = value;
                    }
                }
                else
                {
                    nullCount++;
                }

                if (currentRunLength == 0)
                {
                    currentLevel = level;
                    currentRunLength = 1;
                }
                else if (currentLevel == level)
                {
                    currentRunLength++;
                }
                else
                {
                    EncodingPrimitives.WriteRleRun(currentLevel, currentRunLength, 1, ref dataPage.Content);
                    currentLevel = level;
                    currentRunLength = 1;
                }
            }

            if (currentRunLength > 0)
                EncodingPrimitives.WriteRleRun(currentLevel, currentRunLength, 1, ref dataPage.Content);
            var definitionLength = CompleteLevelEncoding(definitionStart, lengthPrefix, ref dataPage.Content);
            if (presentCount == 0)
                throw new InvalidOperationException("Fused optional dictionary encoding requires a present value.");

            PlainEncoding.WriteValues(column, dictionaryState.AsSpan(), ref dictionaryPage.Content);
            dictionaryPage.SetDictionaryPageMetadata(checked((uint)dictionaryState.Count));
            var dictionaryBitWidth = EncodingPrimitives.GetBitWidthFromMaxValue(
                dictionaryState.Count <= 1 ? 0 : dictionaryState.Count - 1);
            DictionaryIndexEncodingDispatcher.WriteIndexes(EncodingKindResolver.GetDictionaryEncodingKind(column),
                indexes[..presentCount], dictionaryBitWidth, ref dataPage.Content);
            WriteDataPageHeader(ref dataPage, values.Length, values.Length, nullCount, 0, definitionLength,
                EncodingKindResolver.GetDictionaryEncodingKind(column));

            return ColumnStatistics.FromDoubleAccumulation(min, max, nullCount, nanCount, hasStatisticsValue);
        }
        finally
        {
            rentedIndexesBuffer.Dispose();
        }
    }

    internal static void EncodeOptional<T>(BufferWriterFactory bufferWriters, Column column, ReadOnlySpan<T> values,
        PageStrategyContext strategyContext, PageList pages, ParquetDataPageVersion dataPageVersion,
        LeafProjectionInfo leafProjectionInfo, ReusableDictionaryState<T> dictionaryState,
        out PlainBinaryMinMax binaryMinMax)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(column);
        ArgumentNullException.ThrowIfNull(strategyContext);
        ArgumentNullException.ThrowIfNull(pages);
        if (column.Options.Repetition != ParquetRepetition.Optional)
            throw new InvalidOperationException(
                $"Column '{column.Name}' does not support null values.");
        if (leafProjectionInfo.MaxDefinitionLevel != 1 || leafProjectionInfo.MaxRepetitionLevel != 0)
            throw new NotSupportedException(
                $"Column '{column.Name}' optional flat encoding requires a single optional leaf.");

        binaryMinMax = default;
        pages.Clear();
        if (values.Length == 0)
            return;

        if (typeof(T) != typeof(byte[]))
            throw new NotSupportedException($"Optional reference column type '{typeof(T)}' is not supported.");

        var byteArrays = Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<byte[]>>(ref values);
        var byteArrayDictionary = (ReusableDictionaryState<byte[]>)(object)dictionaryState;
        EncodeOptionalFlatByteArrays<byte[], byte[], ReferenceRow<byte[]>, OptionalByteArrayRow>(bufferWriters,
            column, byteArrays, strategyContext, pages, dataPageVersion, byteArrayDictionary, ref binaryMinMax);
    }

    static bool TryWriteDictionaryPage<T>(BufferWriterFactory bufferWriters, Column column, ReadOnlySpan<T> values,
        PageStrategyContext strategyContext, PageList pages, ReusableDictionaryState<T> dictionaryState,
        out int dictionaryValueCount, out ParquetBuffer dictionaryIndexesBuffer,
        LogicalType.Timestamp? timestamp = null)
        where T : notnull
    {
        dictionaryValueCount = 0;
        dictionaryIndexesBuffer = default;

        if (values.IsEmpty)
            return false;

        var strategy = strategyContext.Strategy;
        var dictionaryMode = strategy.GetDictionaryMode();
        if (dictionaryMode == DictionaryMode.Disabled)
        {
            return false;
        }

        var dictionaryPageIndex = AddDictionaryPage(bufferWriters, pages);
        ref var dictionaryPage = ref pages[dictionaryPageIndex];
        var indexByteLength = checked(values.Length * sizeof(int));
        var rentedIndexesBuffer = bufferWriters.RentScratch(checked((uint)Math.Max(indexByteLength, sizeof(int))));
        try
        {
            var indexes = MemoryMarshal.Cast<byte, int>(rentedIndexesBuffer.Span[..indexByteLength]);
            if (typeof(T) == typeof(bool))
            {
                WriteBooleanDictionaryPage(column, Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<bool>>(ref values),
                    ref dictionaryPage, indexes,
                    out dictionaryValueCount);

                dictionaryIndexesBuffer = rentedIndexesBuffer;
                rentedIndexesBuffer = default;
                return true;
            }

            var initialUniqueCapacity = dictionaryMode == DictionaryMode.Forced
                ? GetInitialForcedDictionaryCapacity(values.Length)
                : Math.Max(256, values.Length / 2);
            var knownSortOrder = (DictionarySortOrder)Volatile.Read(ref strategyContext.DictionarySortOrder);
            if (dictionaryMode == DictionaryMode.Forced && typeof(T) == typeof(byte[]))
            {
                var byteArrayValues = Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<byte[]>>(ref values);
                BuildForcedRequiredByteArrayDictionaryIndexes(byteArrayValues, indexes,
                    Unsafe.As<ReusableDictionaryState<byte[]>>(dictionaryState), initialUniqueCapacity,
                    knownSortOrder, strategyContext);
            }
            else if (dictionaryMode == DictionaryMode.Forced
                     && knownSortOrder == DictionarySortOrder.Unsorted)
            {
                dictionaryState.Reset(initialUniqueCapacity, useMap: true);
                BuildForcedDictionaryIndexes(values, indexes, dictionaryState);
            }
            else
            {
                dictionaryState.Reset(initialUniqueCapacity, knownSortOrder == DictionarySortOrder.Unsorted);
                var comparer = GetDictionaryComparer<T>();
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
                                Volatile.Write(ref strategyContext.DictionarySortOrder,
                                    (int)DictionarySortOrder.Unsorted);
                                knownSortOrder = DictionarySortOrder.Unsorted;
                            }
                            dictionaryState.EnableMap();
                            if (dictionaryMode == DictionaryMode.Forced)
                            {
                                BuildForcedDictionaryIndexes(values[i..], indexes[i..], dictionaryState);
                                break;
                            }
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
                    if (strategy.ShouldDropDictionary(checked((uint)dictionaryState.Count),
                            checked((uint)values.Length), checked((uint)rowsSeen)))
                    {
                        dictionaryPage.Header.Reset();
                        dictionaryPage.Content.Reset();
                        pages.RemoveLast();
                        rentedIndexesBuffer.Dispose();
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
                        Volatile.Write(ref strategyContext.DictionarySortOrder, (int)discoveredSortOrder);
                }
            }

            if (timestamp is null)
            {
                PlainEncoding.WriteValues(column, dictionaryState.AsSpan(), ref dictionaryPage.Content);
            }
            else
            {
                if (typeof(T) != typeof(DateTime))
                    throw new InvalidOperationException(
                        $"Column '{column.Name}' direct timestamp dictionary values must be DateTime values.");
                var dictionaryValues = dictionaryState.AsSpan();
                var dateTimes = Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<DateTime>>(ref dictionaryValues);
                WriteDateTimeDictionaryValues(bufferWriters, column, dateTimes, timestamp,
                    ref dictionaryPage.Content);
            }

            dictionaryPage.SetDictionaryPageMetadata(checked((uint)dictionaryState.Count));
            dictionaryValueCount = dictionaryState.Count;
            dictionaryIndexesBuffer = rentedIndexesBuffer;
            rentedIndexesBuffer = default;
            return true;
        }
        finally
        {
            rentedIndexesBuffer.Dispose();
        }
    }

    /// <summary>
    /// Builds a forced dictionary for a required <c>byte[]</c> column. Keeping this hot loop concrete
    /// lets the JIT inline <see cref="ReusableDictionaryState{T}.GetOrAddIndex"/> instead of using the
    /// shared-generic <c>__Canon</c> implementation used by the surrounding method. Once a sorted run
    /// breaks, the remaining map-only tail also avoids checking the dictionary mode for every value.
    /// </summary>
    static void BuildForcedRequiredByteArrayDictionaryIndexes(ReadOnlySpan<byte[]> values, Span<int> indexes,
        ReusableDictionaryState<byte[]> dictionaryState, int initialUniqueCapacity,
        DictionarySortOrder knownSortOrder, PageStrategyContext strategyContext)
    {
        dictionaryState.Reset(initialUniqueCapacity, knownSortOrder == DictionarySortOrder.Unsorted);
        if (dictionaryState.IsMapEnabled)
        {
            for (var i = 0; i < values.Length; i++)
                indexes[i] = dictionaryState.GetOrAddIndex(values[i]);
            return;
        }

        indexes[0] = dictionaryState.AddFirst(values[0]);
        var currentSortedIndex = 0;
        var sortedDirection = knownSortOrder switch
        {
            DictionarySortOrder.Ascending => 1,
            DictionarySortOrder.Descending => -1,
            _ => 0
        };
        if (values.Length > 1 && sortedDirection == 0)
        {
            var firstComparison = values[0].AsSpan().SequenceCompareTo(values[1]);
            if (firstComparison != 0)
                sortedDirection = firstComparison < 0 ? 1 : -1;
        }

        for (var i = 1; i < values.Length; i++)
        {
            var value = values[i];
            var previous = values[i - 1];
            if (ReferenceEquals(value, previous)
                || value is not null && previous is not null && value.AsSpan().SequenceEqual(previous))
            {
                indexes[i] = currentSortedIndex;
                continue;
            }

            var comparison = previous.AsSpan().SequenceCompareTo(value);
            if (IsSortedStep(comparison, ref sortedDirection))
            {
                currentSortedIndex = dictionaryState.AddSortedUnique(value!);
                indexes[i] = currentSortedIndex;
                continue;
            }

            if (knownSortOrder != DictionarySortOrder.Unsorted)
                Volatile.Write(ref strategyContext.DictionarySortOrder, (int)DictionarySortOrder.Unsorted);
            dictionaryState.EnableMap();
            for (; i < values.Length; i++)
                indexes[i] = dictionaryState.GetOrAddIndex(values[i]);
            return;
        }

        var discoveredSortOrder = sortedDirection switch
        {
            1 => DictionarySortOrder.Ascending,
            -1 => DictionarySortOrder.Descending,
            _ => DictionarySortOrder.Unknown
        };
        if (discoveredSortOrder != knownSortOrder)
            Volatile.Write(ref strategyContext.DictionarySortOrder, (int)discoveredSortOrder);
    }

    /// <summary>
    /// Builds indexes after a forced-dictionary column has already been identified as unsorted.
    /// This map-only loop avoids repeating the sorted-run and dictionary-mode branches for every
    /// value in subsequent batches.
    /// </summary>
    static void BuildForcedDictionaryIndexes<T>(ReadOnlySpan<T> values, Span<int> indexes,
        ReusableDictionaryState<T> dictionaryState)
        where T : notnull
    {
        for (var i = 0; i < values.Length; i++)
            indexes[i] = dictionaryState.GetOrAddIndex(values[i]);
    }

    static void EncodeOptionalFlatValues<T, TSource>(BufferWriterFactory bufferWriters, Column column,
        ReadOnlySpan<TSource?> values,
        PageStrategyContext strategyContext, PageList pages, ParquetDataPageVersion dataPageVersion,
        ReadOnlySpan<T> denseValues,
        ReusableDictionaryState<T> dictionaryState)
        where T : struct
        where TSource : struct
    {
        var strategy = strategyContext.Strategy;
        var dataEncoding = EncodingKindResolver.GetDataEncodingKind(column);
        var dictionaryEncoding = EncodingKindResolver.GetDictionaryEncodingKind(column);
        var useDictionary = TryWriteDictionaryPage(bufferWriters, column, denseValues, strategyContext, pages,
            dictionaryState, out var dictionaryValueCount, out var dictionaryIndexesBuffer);
        var dictionaryIndexes = useDictionary && !dictionaryIndexesBuffer.IsEmpty
            ? MemoryMarshal.Cast<byte, int>(dictionaryIndexesBuffer.Span[..checked(denseValues.Length * sizeof(int))])
            : default;
        var dictionaryBitWidth = useDictionary
            ? EncodingPrimitives.GetBitWidthFromMaxValue(dictionaryValueCount <= 1 ? 0 : dictionaryValueCount - 1)
            : 0;
        var useTargetPageBytes = TryGetOptionalPageSizer(column, dataEncoding, useDictionary, dictionaryBitWidth,
            strategy, out var targetPageBytes, out var presentValueBytes);
        var fixedRowsPerPage = 0;
        if (!useDictionary && column.PhysicalType == ParquetPhysicalType.Int32
            && dataEncoding == EncodingKind.ByteStreamSplit && strategy is DefaultStrategy
            && strategy.TryGetTargetDataPageSizeBytes(out var fixedTargetPageBytes))
        {
            const int estimatedEncodedBytesPerRow = sizeof(int) + 1;
            fixedRowsPerPage = Math.Max(1, checked((int)fixedTargetPageBytes) / estimatedEncodedBytesPerRow);
        }
        else if (useTargetPageBytes && !useDictionary && dataEncoding == EncodingKind.Plain
                 && denseValues.Length == values.Length
                 && TryGetDirectOptionalPlainNumericRowBytes<T, TSource>(column, out var encodedBytesPerRow))
        {
            fixedRowsPerPage = Math.Max(1, targetPageBytes / encodedBytesPerRow);
        }

        var rowsWritten = 0;
        var denseOffset = 0;
        try
        {
            while (rowsWritten < values.Length)
            {
                var pageStart = rowsWritten;
                var pageRowCount = 0;
                var pageBytes = 0;
                if (fixedRowsPerPage > 0)
                {
                    pageRowCount = Math.Min(fixedRowsPerPage, values.Length - rowsWritten);
                    rowsWritten += pageRowCount;
                }
                else if (!useTargetPageBytes)
                {
                    pageRowCount = GetStrategyPageRowCount(strategy, values.Length, rowsWritten);
                    rowsWritten += pageRowCount;
                }
                else
                {
                    while (rowsWritten < values.Length)
                    {
                        var rowBytes = 0;
                        if (useTargetPageBytes)
                        {
                            rowBytes = GetOptionalRowBytes<TSource?, TSource, NullableValueRow<TSource>>(
                                column, in values[rowsWritten], presentValueBytes);
                            if (pageRowCount > 0 && pageBytes + rowBytes > targetPageBytes)
                                break;
                        }

                        rowsWritten++;
                        pageRowCount++;
                        if (useTargetPageBytes)
                            pageBytes = checked(pageBytes + rowBytes);
                        if (rowsWritten == values.Length)
                            break;
                    }
                }

                var pageIndex = AddNewDataPage(bufferWriters, pages);
                ref var page = ref pages[pageIndex];
                var pageRows = values.Slice(pageStart, pageRowCount);
                var nullCount = 0;
                var presentRows = 0;
                var definitionLength = WriteOptionalDefinitionLevels<TSource?, TSource, NullableValueRow<TSource>>(
                    pageRows, ref nullCount, ref presentRows, dataPageVersion == ParquetDataPageVersion.V1,
                    ref page.Content);
                var pageDenseValues = denseValues.Slice(denseOffset, presentRows);
                if (useDictionary)
                {
                    if (dictionaryIndexes.IsEmpty)
                        throw new InvalidOperationException("Dictionary index buffer is missing for dictionary-encoded page.");
                    DictionaryIndexEncodingDispatcher.WriteIndexes(dictionaryEncoding,
                        dictionaryIndexes.Slice(denseOffset, presentRows), dictionaryBitWidth, ref page.Content);
                }
                // Encode even with nothing present. PLAIN is happy to emit zero
                // bytes, but DELTA_BINARY_PACKED and the DELTA_*_BYTE_ARRAY
                // encodings begin with a mandatory header, so skipping the call
                // for an all-null page produced a data section that no compliant
                // reader accepts — Plank could not read it back and neither could
                // arrow-cpp ("Unexpected end of stream: InitHeader EOF").
                else
                    ValueEncodingDispatcher.WriteValues(dataEncoding, column, pageDenseValues, bufferWriters, ref page.Content);

                WriteDataPageHeader(ref page, pageRowCount, pageRowCount, nullCount, 0, definitionLength,
                    useDictionary ? dictionaryEncoding : dataEncoding);
                denseOffset += presentRows;
            }
        }
        finally
        {
            dictionaryIndexesBuffer.Dispose();
        }
    }

    /// <summary>
    /// Dictionary page for an optional byte-array column. Byte-array keys always take the hash-map
    /// path, so unlike <see cref="TryWriteDictionaryPage"/> there is no sorted-run fast path here and
    /// the column's sort order is reported as unsorted.
    /// </summary>
    static bool TryWriteOptionalByteArrayDictionaryPage<TRow, TValue, TProbe>(BufferWriterFactory bufferWriters,
        Column column, ReadOnlySpan<TRow> values, int presentCount, PageStrategyContext strategyContext,
        PageList pages, ReusableDictionaryState<TValue> dictionaryState,
        out int dictionaryValueCount, out ParquetBuffer dictionaryIndexesBuffer)
        where TValue : notnull
        where TProbe : IOptionalRow<TRow, TValue>
    {
        dictionaryValueCount = 0;
        dictionaryIndexesBuffer = default;
        if (presentCount == 0)
            return false;

        var strategy = strategyContext.Strategy;
        var dictionaryMode = strategy.GetDictionaryMode();
        if (dictionaryMode == DictionaryMode.Disabled)
            return false;

        var dictionaryPageIndex = AddDictionaryPage(bufferWriters, pages);
        ref var dictionaryPage = ref pages[dictionaryPageIndex];
        var indexByteLength = checked(presentCount * sizeof(int));
        var rentedIndexesBuffer = bufferWriters.RentScratch(checked((uint)indexByteLength));
        try
        {
            var indexes = MemoryMarshal.Cast<byte, int>(rentedIndexesBuffer.Span[..indexByteLength]);
            var initialUniqueCapacity = dictionaryMode == DictionaryMode.Forced
                ? GetInitialForcedDictionaryCapacity(presentCount)
                : Math.Max(256, presentCount / 2);
            dictionaryState.Reset(initialUniqueCapacity, useMap: true);
            Volatile.Write(ref strategyContext.DictionarySortOrder, (int)DictionarySortOrder.Unsorted);

            // byte[] shares __Canon generic codegen, where the probe dispatch and GetOrAddIndex do not
            // inline - a ~20% cost on this hot per-value build loop. typeof(TValue) is a JIT constant per
            // instantiation, so the byte[] instantiation compiles to the concrete branch only (and value
            // types like ReadOnlyMemory<byte> keep their already-inlined dedicated codegen).
            var kept = typeof(TValue) == typeof(byte[])
                ? BuildByteArrayDictionaryIndexes(
                    MemoryMarshal.CreateReadOnlySpan(
                        ref Unsafe.As<TRow, byte[]>(ref MemoryMarshal.GetReference(values)), values.Length),
                    indexes, Unsafe.As<ReusableDictionaryState<byte[]>>(dictionaryState), dictionaryMode,
                    presentCount, strategy)
                : BuildOptionalDictionaryIndexes<TRow, TValue, TProbe>(values, indexes, dictionaryState,
                    dictionaryMode, presentCount, strategy);
            if (!kept)
            {
                dictionaryPage.Header.Reset();
                dictionaryPage.Content.Reset();
                pages.RemoveLast();
                return false;
            }

            PlainEncoding.WriteValues(column, dictionaryState.AsSpan(), ref dictionaryPage.Content);
            dictionaryPage.SetDictionaryPageMetadata(checked((uint)dictionaryState.Count));
            dictionaryValueCount = dictionaryState.Count;
            dictionaryIndexesBuffer = rentedIndexesBuffer;
            rentedIndexesBuffer = default;
            return true;
        }
        finally
        {
            rentedIndexesBuffer.Dispose();
        }
    }

    /// <summary>
    /// Concrete byte[] build loop. Kept separate from the generic path so that, on the byte[]
    /// instantiation, the null check and <see cref="ReusableDictionaryState{T}.GetOrAddIndex"/> inline
    /// instead of paying non-inlined calls under __Canon shared generics. Returns false if the
    /// dictionary was dropped.
    /// </summary>
    static bool BuildByteArrayDictionaryIndexes(ReadOnlySpan<byte[]> values, Span<int> indexes,
        ReusableDictionaryState<byte[]> dictionaryState, DictionaryMode dictionaryMode, int presentCount,
        IPageStrategy strategy)
    {
        var denseIndex = 0;
        var nextDropCheckRow = dictionaryMode == DictionaryMode.Maybe
            ? Math.Min(DictionaryDropCheckPeriodRows, presentCount)
            : 0;
        for (var i = 0; i < values.Length; i++)
        {
            var value = values[i];
            if (value is null)
                continue;

            indexes[denseIndex++] = dictionaryState.GetOrAddIndex(value);
            if (dictionaryMode != DictionaryMode.Maybe)
                continue;
            if (denseIndex != nextDropCheckRow && denseIndex != presentCount)
                continue;
            if (strategy.ShouldDropDictionary(checked((uint)dictionaryState.Count),
                    checked((uint)presentCount), checked((uint)denseIndex)))
                return false;

            nextDropCheckRow = Math.Min(presentCount, denseIndex + DictionaryDropCheckPeriodRows);
        }

        return true;
    }

    /// <summary>
    /// Generic build loop for the value-type instantiations (for example ReadOnlyMemory&lt;byte&gt;),
    /// which get dedicated codegen where the probe and dictionary calls already inline. Returns false
    /// if the dictionary was dropped.
    /// </summary>
    static bool BuildOptionalDictionaryIndexes<TRow, TValue, TProbe>(ReadOnlySpan<TRow> values,
        Span<int> indexes, ReusableDictionaryState<TValue> dictionaryState, DictionaryMode dictionaryMode,
        int presentCount, IPageStrategy strategy)
        where TValue : notnull
        where TProbe : IOptionalRow<TRow, TValue>
    {
        var denseIndex = 0;
        var nextDropCheckRow = dictionaryMode == DictionaryMode.Maybe
            ? Math.Min(DictionaryDropCheckPeriodRows, presentCount)
            : 0;
        for (var i = 0; i < values.Length; i++)
        {
            if (!TProbe.IsPresent(in values[i]))
                continue;

            indexes[denseIndex++] = dictionaryState.GetOrAddIndex(TProbe.GetValue(in values[i]));
            if (dictionaryMode != DictionaryMode.Maybe)
                continue;
            if (denseIndex != nextDropCheckRow && denseIndex != presentCount)
                continue;
            if (strategy.ShouldDropDictionary(checked((uint)dictionaryState.Count),
                    checked((uint)presentCount), checked((uint)denseIndex)))
                return false;

            nextDropCheckRow = Math.Min(presentCount, denseIndex + DictionaryDropCheckPeriodRows);
        }

        return true;
    }

    /// <summary>
    /// Page loop for an optional byte-array column. The values are written straight from the rows
    /// rather than from a compacted array, so the encoders receive the page rows and skip nulls
    /// themselves through <typeparamref name="TRowAccess"/>.
    /// </summary>
    static void EncodeOptionalFlatByteArrays<TRow, TValue, TProbe, TRowAccess>(BufferWriterFactory bufferWriters,
        Column column, ReadOnlySpan<TRow> values, PageStrategyContext strategyContext, PageList pages,
        ParquetDataPageVersion dataPageVersion, ReusableDictionaryState<TValue> dictionaryState,
        ref PlainBinaryMinMax binaryMinMax)
        where TValue : notnull
        where TProbe : IOptionalRow<TRow, TValue>
        where TRowAccess : IByteArrayRow<TRow>
    {
        var strategy = strategyContext.Strategy;
        var dataEncoding = EncodingKindResolver.GetDataEncodingKind(column);
        var dictionaryEncoding = EncodingKindResolver.GetDictionaryEncodingKind(column);
        // Only the dictionary path consumes the present count, and counting it is a full extra pass over an
        // array of references whose objects rarely fit in cache. Skip it when no dictionary can be built.
        var presentCount = strategy.GetDictionaryMode() == DictionaryMode.Disabled
            ? 0
            : CountPresentValues<TRow, TValue, TProbe>(values);
        var useDictionary = TryWriteOptionalByteArrayDictionaryPage<TRow, TValue, TProbe>(bufferWriters, column,
            values, presentCount, strategyContext, pages, dictionaryState, out var dictionaryValueCount,
            out var dictionaryIndexesBuffer);
        var dictionaryIndexes = useDictionary && !dictionaryIndexesBuffer.IsEmpty
            ? MemoryMarshal.Cast<byte, int>(dictionaryIndexesBuffer.Span[..checked(presentCount * sizeof(int))])
            : default;
        var dictionaryBitWidth = useDictionary
            ? EncodingPrimitives.GetBitWidthFromMaxValue(dictionaryValueCount <= 1 ? 0 : dictionaryValueCount - 1)
            : 0;
        var useTargetPageBytes = TryGetOptionalPageSizer(column, dataEncoding, useDictionary, dictionaryBitWidth,
            strategy, out var targetPageBytes, out var presentValueBytes);

        // A decimal column orders its bytes by sign and magnitude, so the lexicographic extremes this
        // pass could collect would be the wrong answer and the statistics walk has to run anyway.
        var trackMinMax = ColumnStatistics.OrdersBinaryValuesLexicographically(column);
        var rowsWritten = 0;
        var denseOffset = 0;
        var totalNullCount = 0L;
        try
        {
            while (rowsWritten < values.Length)
            {
                var pageStart = rowsWritten;
                int pageRowCount;
                var pageBytes = -1;
                if (useTargetPageBytes)
                {
                    pageRowCount = MeasureByteArrayPageRows<TRow, TValue, TProbe>(column, values, rowsWritten,
                        presentValueBytes, targetPageBytes, trackMinMax, ref binaryMinMax, out pageBytes);
                    rowsWritten += pageRowCount;
                }
                else
                {
                    pageRowCount = GetStrategyPageRowCount(strategy, values.Length, rowsWritten);
                    rowsWritten += pageRowCount;
                }

                var pageIndex = AddNewDataPage(bufferWriters, pages);
                ref var page = ref pages[pageIndex];
                var pageRows = values.Slice(pageStart, pageRowCount);
                var nullCount = 0;
                var presentRows = 0;
                var definitionLength = WriteOptionalDefinitionLevels<TRow, TValue, TProbe>(pageRows, ref nullCount,
                    ref presentRows, dataPageVersion == ParquetDataPageVersion.V1, ref page.Content);
                if (useDictionary)
                {
                    if (dictionaryIndexes.IsEmpty)
                        throw new InvalidOperationException("Dictionary index buffer is missing for dictionary-encoded page.");
                    DictionaryIndexEncodingDispatcher.WriteIndexes(dictionaryEncoding,
                        dictionaryIndexes.Slice(denseOffset, presentRows), dictionaryBitWidth, ref page.Content);
                }
                // Encode even with nothing present. PLAIN is happy to emit zero
                // bytes, but DELTA_BINARY_PACKED and the DELTA_*_BYTE_ARRAY
                // encodings begin with a mandatory header, so skipping the call
                // for an all-null page produced a data section that no compliant
                // reader accepts — Plank could not read it back and neither could
                // arrow-cpp ("Unexpected end of stream: InitHeader EOF").
                else
                    // A variable-length row was measured as one definition-level byte plus its plain
                    // length prefix and payload, so the budget the sizer arrived at already holds the
                    // payload size and the writer does not have to walk the rows again to find it.
                    // Fixed-width shapes are left to count for themselves: their rows were measured
                    // without a length prefix the length-prefixed writer goes on to emit.
                    ValueEncodingDispatcher.WriteOptionalValues<TRow, TRowAccess>(dataEncoding, column, pageRows,
                        bufferWriters, pageBytes >= 0 && presentValueBytes == 0 ? pageBytes - pageRowCount : -1,
                        ref page.Content);

                WriteDataPageHeader(ref page, pageRowCount, pageRowCount, nullCount, 0, definitionLength,
                    useDictionary ? dictionaryEncoding : dataEncoding);
                denseOffset += presentRows;
                totalNullCount += nullCount;
            }

            binaryMinMax.NullCount = totalNullCount;
        }
        finally
        {
            dictionaryIndexesBuffer.Dispose();
        }
    }

    /// <summary>
    /// How many of <paramref name="rows"/> fit in one page of <paramref name="targetPageBytes"/>. At least one
    /// row always goes in, however big it is.
    /// </summary>
    /// <remarks>
    /// The byte[] instantiation of the caller is shared __Canon code, where <c>typeof(TValue) == typeof(byte[])</c>
    /// is a runtime type-handle comparison and the static-abstract row probes are real out-of-line calls. Measuring
    /// a page cost more than encoding it: the probes, the size switch and the loop around them were a quarter of an
    /// optional plain BYTE_ARRAY column. Testing the row type once per page instead of once per row hands the work
    /// to a concrete loop the JIT can see through.
    /// </remarks>
    static int MeasureByteArrayPageRows<TRow, TValue, TProbe>(Column column, ReadOnlySpan<TRow> rows, int startIndex,
        int presentValueBytes, int targetPageBytes, bool trackMinMax, ref PlainBinaryMinMax binaryMinMax,
        out int pageBytes)
        where TValue : notnull
        where TProbe : IOptionalRow<TRow, TValue>
    {
        if (typeof(TRow) == typeof(byte[]))
            return MeasureByteArrayPageRows(
                MemoryMarshal.CreateReadOnlySpan(
                    ref Unsafe.As<TRow, byte[]>(ref MemoryMarshal.GetReference(rows)), rows.Length),
                startIndex, presentValueBytes, targetPageBytes, trackMinMax, ref binaryMinMax, out pageBytes);

        pageBytes = 0;
        for (var i = startIndex; i < rows.Length; i++)
        {
            var rowBytes = GetOptionalRowBytes<TRow, TValue, TProbe>(column, in rows[i], presentValueBytes);
            if (i > startIndex && pageBytes + rowBytes > targetPageBytes)
                return i - startIndex;
            pageBytes = checked(pageBytes + rowBytes);
        }

        return rows.Length - startIndex;
    }

    static int MeasureByteArrayPageRows(ReadOnlySpan<byte[]> rows, int startIndex, int presentValueBytes,
        int targetPageBytes, bool trackMinMax, ref PlainBinaryMinMax binaryMinMax, out int pageBytes)
    {
        var minIndex = binaryMinMax.Found ? binaryMinMax.MinIndex : -1;
        var maxIndex = binaryMinMax.Found ? binaryMinMax.MaxIndex : -1;
        var min = minIndex < 0 ? null : rows[minIndex];
        var max = maxIndex < 0 ? null : rows[maxIndex];
        pageBytes = 0;
        var i = startIndex;
        for (; i < rows.Length; i++)
        {
            var row = rows[i];
            if (row is null)
            {
                if (i > startIndex && 1 > targetPageBytes - pageBytes)
                    break;
                pageBytes = checked(pageBytes + 1);
                continue;
            }

            var rowBytes = presentValueBytes > 0
                ? 1 + presentValueBytes
                : checked(1 + sizeof(int) + row.Length);
            if (i > startIndex && rowBytes > targetPageBytes - pageBytes)
                break;
            pageBytes = checked(pageBytes + rowBytes);

            if (!trackMinMax)
            {
                continue;
            }

            if (min is null)
            {
                min = row;
                max = row;
                minIndex = i;
                maxIndex = i;
            }
            // A value below the running min cannot also be above the running max, so the second
            // comparison only runs when the first one did not claim the value.
            else if (EncodingPrimitives.ComparePayload(row, min) < 0)
            {
                min = row;
                minIndex = i;
            }
            else if (EncodingPrimitives.ComparePayload(row, max) > 0)
            {
                max = row;
                maxIndex = i;
            }
        }

        if (min is not null)
        {
            binaryMinMax.Found = true;
            binaryMinMax.MinIndex = minIndex;
            binaryMinMax.MaxIndex = maxIndex;
        }

        return i - startIndex;
    }

    static bool TryGetOptionalPageSizer(Column column, EncodingKind dataEncoding, bool useDictionary,
        int dictionaryBitWidth, IPageStrategy strategy, out int targetPageBytes, out int presentValueBytes)
    {
        targetPageBytes = 0;
        presentValueBytes = 0;
        if (!strategy.TryGetTargetDataPageSizeBytes(out var targetPageBytesUnsigned))
            return false;
        targetPageBytes = checked((int)targetPageBytesUnsigned);

        if (!useDictionary && dataEncoding == EncodingKind.Plain
            && TryGetPlainEncodedValueSize(column, column.PhysicalType == ParquetPhysicalType.ByteArray
                ? typeof(byte[])
                : typeof(int), out presentValueBytes))
            return true;

        if (!useDictionary && column.PhysicalType == ParquetPhysicalType.ByteArray
            && dataEncoding is EncodingKind.DeltaByteArray or EncodingKind.DeltaLengthByteArray)
            return true;

        if (!useDictionary)
            return false;

        // A dictionary holding a single distinct value needs zero bits per index,
        // but zero is also how GetOptionalPresentValueBytes spells "size unknown,
        // measure the value instead" — and that measurement only handles
        // variable-length types, so an optional dictionary column of one distinct
        // value threw "cannot estimate plain encoded size" for every fixed-width
        // type. Charge a byte instead: this is a page budget, and rounding a
        // free index up to one byte only splits pages very slightly early.
        presentValueBytes = Math.Max(1, (dictionaryBitWidth + 7) / 8);
        return true;
    }

    static bool TryGetDirectOptionalPlainNumericRowBytes<T, TSource>(Column column, out int rowBytes)
        where T : struct
        where TSource : struct
    {
        rowBytes = column.PhysicalType switch
        {
            ParquetPhysicalType.Int32 when typeof(T) == typeof(int) && typeof(TSource) == typeof(int)
                => sizeof(int) + 1,
            ParquetPhysicalType.Int64 when typeof(T) == typeof(long) && typeof(TSource) == typeof(long)
                => sizeof(long) + 1,
            ParquetPhysicalType.Float when typeof(T) == typeof(float) && typeof(TSource) == typeof(float)
                => sizeof(float) + 1,
            ParquetPhysicalType.Double when typeof(T) == typeof(double) && typeof(TSource) == typeof(double)
                => sizeof(double) + 1,
            _ => 0
        };
        return rowBytes > 0;
    }

    static int GetOptionalRowBytes<TRow, TValue, TProbe>(Column column, in TRow row, int presentValueBytes)
        where TValue : notnull
        where TProbe : IOptionalRow<TRow, TValue>
        => 1 + (TProbe.IsPresent(in row)
            ? GetOptionalPresentValueBytes(column, TProbe.GetValue(in row), presentValueBytes)
            : 0);

    static int GetOptionalPresentValueBytes<T>(Column column, T value, int presentValueBytes)
        where T : notnull
        => presentValueBytes > 0 ? presentValueBytes : GetVariablePlainValueBytes(column, value);

    static int WriteOptionalDefinitionLevels<TRow, TValue, TProbe>(ReadOnlySpan<TRow> values, ref int nullCount,
        ref int presentRows, bool writeLengthPrefix, ref BufferWriter writer)
        where TProbe : IOptionalRow<TRow, TValue>
    {
        var lengthPrefix = ReserveLevelLengthPrefix(writeLengthPrefix, ref writer);
        var start = writer.WrittenLength;
        var definitionBitWidth = 1;
        var currentLevel = -1;
        var currentRunLength = 0;
        for (var i = 0; i < values.Length; i++)
        {
            var level = IsPresentFast<TRow, TValue, TProbe>(in values[i]) ? 1 : 0;
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

            EncodingPrimitives.WriteRleRun(currentLevel, currentRunLength, definitionBitWidth, ref writer);
            currentLevel = level;
            currentRunLength = 1;
        }

        if (currentRunLength > 0)
            EncodingPrimitives.WriteRleRun(currentLevel, currentRunLength, definitionBitWidth, ref writer);
        return CompleteLevelEncoding(start, lengthPrefix, ref writer);
    }

    static int CountPresentValues<TRow, TValue, TProbe>(ReadOnlySpan<TRow> values)
        where TProbe : IOptionalRow<TRow, TValue>
    {
        var count = 0;
        for (var i = 0; i < values.Length; i++)
            if (IsPresentFast<TRow, TValue, TProbe>(in values[i]))
                count++;
        return count;
    }

    /// <summary>
    /// Presence probe with a concrete byte[] fast path. <c>typeof(TValue) == typeof(byte[])</c> is a
    /// JIT constant per instantiation, so the byte[] instantiation folds to a plain null check (no
    /// non-inlined static-abstract dispatch under __Canon shared generics) while value-type
    /// instantiations keep the probe, which already inlines there.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static bool IsPresentFast<TRow, TValue, TProbe>(in TRow row)
        where TProbe : IOptionalRow<TRow, TValue>
        => typeof(TValue) == typeof(byte[])
            ? Unsafe.As<TRow, byte[]>(ref Unsafe.AsRef(in row)) is not null
            : TProbe.IsPresent(in row);

    static void CopyPresentValues<TRow, TValue, TProbe>(ReadOnlySpan<TRow> values, Span<TValue> destination)
        where TProbe : IOptionalRow<TRow, TValue>
    {
        var index = 0;
        for (var i = 0; i < values.Length; i++)
            if (TProbe.IsPresent(in values[i]))
                destination[index++] = TProbe.GetValue(in values[i]);
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

        dictionaryPage.SetDictionaryPageMetadata(2U);
        dictionaryValueCount = 2;
    }

    static void WriteDateTimeDictionaryValues(BufferWriterFactory bufferWriters, Column column,
        ReadOnlySpan<DateTime> values, LogicalType.Timestamp timestamp, ref BufferWriter destination)
    {
        var rented = bufferWriters.RentScratch<long>(checked((uint)values.Length));
        try
        {
            var converted = ParquetBuffer.AsSpan<long>(rented, values.Length);
            var expectedKind = timestamp.IsAdjustedToUtc ? DateTimeKind.Utc : DateTimeKind.Unspecified;
            TimestampConversion.ConvertDateTimes(values, converted, timestamp.Unit, expectedKind);
            PlainEncoding.WriteValues(column, converted, ref destination);
        }
        finally
        {
            bufferWriters.ReturnScratch(rented);
        }
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

    static void EncodeRepeatedRows<T>(BufferWriterFactory bufferWriters, Column column, ReadOnlySpan<T> rows,
        PageList pages, ParquetDataPageVersion dataPageVersion, LeafProjectionInfo leafProjectionInfo)
        where T : notnull
    {
        var dataEncoding = EncodingKindResolver.GetDataEncodingKind(column);
        var writeLevelLengthPrefixes = dataPageVersion == ParquetDataPageVersion.V1;
        var pageIndex = AddNewDataPage(bufferWriters, pages);
        ref var page = ref pages[pageIndex];

        if (leafProjectionInfo.MaxRepetitionLevel > 1)
        {
            switch (column.PhysicalType)
            {
                case ParquetPhysicalType.Boolean:
                    EncodeRepeatedRowsNestedCore<bool, T>(bufferWriters, column, dataEncoding, rows, ref page,
                        writeLevelLengthPrefixes, leafProjectionInfo);
                    return;
                case ParquetPhysicalType.Int32:
                    EncodeRepeatedRowsNestedCore<int, T>(bufferWriters, column, dataEncoding, rows, ref page,
                        writeLevelLengthPrefixes, leafProjectionInfo);
                    return;
                case ParquetPhysicalType.Int64:
                    EncodeRepeatedRowsNestedCore<long, T>(bufferWriters, column, dataEncoding, rows, ref page,
                        writeLevelLengthPrefixes, leafProjectionInfo);
                    return;
                case ParquetPhysicalType.Float:
                    EncodeRepeatedRowsNestedCore<float, T>(bufferWriters, column, dataEncoding, rows, ref page,
                        writeLevelLengthPrefixes, leafProjectionInfo);
                    return;
                case ParquetPhysicalType.Double:
                    EncodeRepeatedRowsNestedCore<double, T>(bufferWriters, column, dataEncoding, rows, ref page,
                        writeLevelLengthPrefixes, leafProjectionInfo);
                    return;
                case ParquetPhysicalType.ByteArray:
                case ParquetPhysicalType.Int96:
                case ParquetPhysicalType.FixedLenByteArray:
                    EncodeRepeatedRowsNestedCore<byte[], T>(bufferWriters, column, dataEncoding, rows, ref page,
                        writeLevelLengthPrefixes, leafProjectionInfo);
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
                    EncodeRepeatedRowsCore<bool, bool, RequiredRow<bool>>(bufferWriters, column, dataEncoding,
                        Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<bool[]>>(ref rows), ref page,
                        writeLevelLengthPrefixes, leafProjectionInfo);
                    return;
                }
                if (leafProjectionInfo.ElementOptional && typeof(T) == typeof(bool?[]))
                {
                    EncodeRepeatedRowsCore<bool?, bool, NullableValueRow<bool>>(bufferWriters, column,
                        dataEncoding, Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<bool?[]>>(ref rows), ref page,
                        writeLevelLengthPrefixes, leafProjectionInfo);
                    return;
                }
                break;
            case ParquetPhysicalType.Int32:
                if (typeof(T) == typeof(int[]))
                {
                    EncodeRepeatedRowsCore<int, int, RequiredRow<int>>(bufferWriters, column, dataEncoding,
                        Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<int[]>>(ref rows), ref page,
                        writeLevelLengthPrefixes, leafProjectionInfo);
                    return;
                }
                if (leafProjectionInfo.ElementOptional && typeof(T) == typeof(int?[]))
                {
                    EncodeRepeatedRowsCore<int?, int, NullableValueRow<int>>(bufferWriters, column,
                        dataEncoding, Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<int?[]>>(ref rows), ref page,
                        writeLevelLengthPrefixes, leafProjectionInfo);
                    return;
                }
                break;
            case ParquetPhysicalType.Int64:
                if (typeof(T) == typeof(long[]))
                {
                    EncodeRepeatedRowsCore<long, long, RequiredRow<long>>(bufferWriters, column, dataEncoding,
                        Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<long[]>>(ref rows), ref page,
                        writeLevelLengthPrefixes, leafProjectionInfo);
                    return;
                }
                if (leafProjectionInfo.ElementOptional && typeof(T) == typeof(long?[]))
                {
                    EncodeRepeatedRowsCore<long?, long, NullableValueRow<long>>(bufferWriters, column,
                        dataEncoding, Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<long?[]>>(ref rows), ref page,
                        writeLevelLengthPrefixes, leafProjectionInfo);
                    return;
                }
                break;
            case ParquetPhysicalType.Float:
                if (typeof(T) == typeof(float[]))
                {
                    EncodeRepeatedRowsCore<float, float, RequiredRow<float>>(bufferWriters, column, dataEncoding,
                        Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<float[]>>(ref rows), ref page,
                        writeLevelLengthPrefixes, leafProjectionInfo);
                    return;
                }
                if (leafProjectionInfo.ElementOptional && typeof(T) == typeof(float?[]))
                {
                    EncodeRepeatedRowsCore<float?, float, NullableValueRow<float>>(bufferWriters, column,
                        dataEncoding, Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<float?[]>>(ref rows), ref page,
                        writeLevelLengthPrefixes, leafProjectionInfo);
                    return;
                }
                break;
            case ParquetPhysicalType.Double:
                if (typeof(T) == typeof(double[]))
                {
                    EncodeRepeatedRowsCore<double, double, RequiredRow<double>>(bufferWriters, column, dataEncoding,
                        Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<double[]>>(ref rows), ref page,
                        writeLevelLengthPrefixes, leafProjectionInfo);
                    return;
                }
                if (leafProjectionInfo.ElementOptional && typeof(T) == typeof(double?[]))
                {
                    EncodeRepeatedRowsCore<double?, double, NullableValueRow<double>>(bufferWriters, column,
                        dataEncoding, Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<double?[]>>(ref rows), ref page,
                        writeLevelLengthPrefixes, leafProjectionInfo);
                    return;
                }
                break;
            case ParquetPhysicalType.ByteArray:
            case ParquetPhysicalType.Int96:
            case ParquetPhysicalType.FixedLenByteArray:
                if (typeof(T) == typeof(byte[][]))
                {
                    if (leafProjectionInfo.ElementOptional)
                        EncodeRepeatedRowsCore<byte[], byte[], ReferenceRow<byte[]>>(bufferWriters, column,
                            dataEncoding, Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<byte[][]>>(ref rows), ref page,
                            writeLevelLengthPrefixes, leafProjectionInfo);
                    else
                        EncodeRepeatedRowsCore<byte[], byte[], RequiredRow<byte[]>>(bufferWriters, column,
                            dataEncoding, Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<byte[][]>>(ref rows), ref page,
                            writeLevelLengthPrefixes, leafProjectionInfo);
                    return;
                }
                if (typeof(T) == typeof(ReadOnlyMemory<byte>[][]))
                {
                    if (leafProjectionInfo.ElementOptional)
                        throw new InvalidOperationException(
                            $"Column '{column.Name}' has optional list elements; use nullable row element type for this column.");

                    EncodeRepeatedRowsCore<ReadOnlyMemory<byte>, ReadOnlyMemory<byte>,
                        RequiredRow<ReadOnlyMemory<byte>>>(bufferWriters, column, dataEncoding,
                        Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<ReadOnlyMemory<byte>[]>>(ref rows), ref page,
                        writeLevelLengthPrefixes, leafProjectionInfo);
                    return;
                }
                break;
        }

        throw new InvalidOperationException(
            $"Repeated column '{column.Name}' with physical type '{column.PhysicalType}' expects rows of '{column.PhysicalType}[]'.");
    }

    /// <summary>
    /// Flattens one page of repeated rows and writes its levels and values. The element shape -
    /// required, nullable value or nullable reference - is carried by <typeparamref name="TProbe"/>;
    /// each of those used to be a full copy of this method.
    /// </summary>
    static void EncodeRepeatedRowsCore<TRowElement, TValue, TProbe>(BufferWriterFactory bufferWriters, Column column,
        EncodingKind dataEncoding, ReadOnlySpan<TRowElement[]> rows, ref Page page, bool writeLevelLengthPrefixes,
        LeafProjectionInfo leafProjectionInfo)
        where TValue : notnull
        where TProbe : IOptionalRow<TRowElement, TValue>
    {
        if (TProbe.ValueRequired && leafProjectionInfo.ElementOptional)
            throw new InvalidOperationException(
                $"Column '{column.Name}' has optional list elements; use nullable row element type for this column.");
        if (!TProbe.ValueRequired && !leafProjectionInfo.ElementOptional)
            throw new InvalidOperationException(
                $"Column '{column.Name}' expects required list elements, but nullable row values were provided.");

        var rowCount = rows.Length;
        var physicalValueCount = 0;
        var levelValueCount = 0;
        var nullCount = 0;
        var allowsNullRow = leafProjectionInfo.ListOptional;
        var listDefinedDefinitionLevel = leafProjectionInfo.IsList && leafProjectionInfo.ListOptional ? 1 : 0;
        // A required element sits one level above the defined list; an optional one needs a level of
        // its own in between.
        var nullElementDefinitionLevel = listDefinedDefinitionLevel + 1;
        var presentElementDefinitionLevel = listDefinedDefinitionLevel + (TProbe.ValueRequired ? 1 : 2);
        var definitionBitWidth = EncodingPrimitives.GetBitWidthFromMaxValue(presentElementDefinitionLevel);

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
            if (TProbe.ValueRequired)
            {
                physicalValueCount = checked(physicalValueCount + row.Length);
                continue;
            }

            for (var i = 0; i < row.Length; i++)
            {
                if (TProbe.IsPresent(in row[i]))
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

            if (TProbe.ValueRequired)
            {
                // TRowElement is TValue on the required instantiations, so the row copies in bulk.
                MemoryMarshal.CreateReadOnlySpan(
                        ref Unsafe.As<TRowElement, TValue>(ref MemoryMarshal.GetArrayDataReference(row)), row.Length)
                    .CopyTo(flatValues.AsSpan(flatIndex));
                flatIndex += row.Length;
                continue;
            }

            for (var i = 0; i < row.Length; i++)
            {
                if (!TProbe.IsPresent(in row[i]))
                    continue;
                flatValues[flatIndex++] = TProbe.GetValue(in row[i]);
            }
        }

        var repetitionLength = WriteRepeatedLevels(rows, writeLevelLengthPrefixes, ref page.Content);
        var definitionLength = WriteRepeatedDefinitionLevels<TRowElement, TValue, TProbe>(rows,
            listDefinedDefinitionLevel, nullElementDefinitionLevel, presentElementDefinitionLevel, allowsNullRow,
            definitionBitWidth, writeLevelLengthPrefixes, ref page.Content);
        ValueEncodingDispatcher.WriteValues(dataEncoding, column, flatValues, bufferWriters, ref page.Content);
        WriteDataPageHeader(ref page, rowCount, levelValueCount, nullCount, repetitionLength, definitionLength,
            dataEncoding);
    }

    static void EncodeRepeatedRowsNestedCore<TElement, TRow>(BufferWriterFactory bufferWriters, Column column,
        EncodingKind dataEncoding, ReadOnlySpan<TRow> rows, ref Page page, bool writeLevelLengthPrefixes,
        LeafProjectionInfo leafProjectionInfo)
        where TElement : notnull
        where TRow : notnull
    {
        var allowsNullRow = leafProjectionInfo.ListOptional;
        var rowDefinedLevel = allowsNullRow ? 1 : 0;
        var repLevels = new List<int>(rows.Length * 2);
        var defLevels = new List<int>(rows.Length * 2);
        var values = new List<TElement>(rows.Length * 2);
        for (var rowIndex = 0; rowIndex < rows.Length; rowIndex++)
        {
            object? row = rows[rowIndex];
            TraverseNestedRepeatedRow(row, depth: 1, repForFirst: 0, currentDefinitionLevel: rowDefinedLevel,
                allowsNullRow, leafProjectionInfo.ElementOptional, leafProjectionInfo.MaxRepetitionLevel,
                leafProjectionInfo.MaxDefinitionLevel, repLevels, defLevels, values, column.Name);
        }

        var repBitWidth = EncodingPrimitives.GetBitWidthFromMaxValue(leafProjectionInfo.MaxRepetitionLevel);
        var defBitWidth = EncodingPrimitives.GetBitWidthFromMaxValue(leafProjectionInfo.MaxDefinitionLevel);
        var repetitionLength = WriteLevelSequence(repLevels, repBitWidth, writeLevelLengthPrefixes, ref page.Content);
        var definitionLength = WriteLevelSequence(defLevels, defBitWidth, writeLevelLengthPrefixes, ref page.Content);
        ValueEncodingDispatcher.WriteValues(dataEncoding, column, CollectionsMarshal.AsSpan(values), bufferWriters, ref page.Content);
        var nullCount = defLevels.Count - values.Count;
        WriteDataPageHeader(ref page, rows.Length, defLevels.Count, nullCount, repetitionLength, definitionLength,
            dataEncoding);

        static void TraverseNestedRepeatedRow(object? node, int depth, int repForFirst, int currentDefinitionLevel,
            bool allowNullNode, bool elementOptional, int maxRepetitionLevel, int maxDefinitionLevel,
            List<int> repLevels, List<int> defLevels, List<TElement> values, string columnName)
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
                    {
                        if (!elementOptional || element is not null)
                            throw new InvalidOperationException(
                                $"Column '{columnName}' has incompatible leaf value type '{element?.GetType()}'.");
                        repLevels.Add(rep);
                        defLevels.Add(maxDefinitionLevel - 1);
                        continue;
                    }
                    repLevels.Add(rep);
                    defLevels.Add(maxDefinitionLevel);
                    values.Add(value);
                    continue;
                }

                TraverseNestedRepeatedRow(element, depth + 1, rep, currentDefinitionLevel + 1, allowNullNode: false,
                    elementOptional, maxRepetitionLevel, maxDefinitionLevel, repLevels, defLevels, values, columnName);
            }
        }
    }

    static int WriteRepeatedLevels<TElement>(ReadOnlySpan<TElement[]> rows, bool writeLengthPrefix,
        ref BufferWriter writer)
    {
        var lengthPrefix = ReserveLevelLengthPrefix(writeLengthPrefix, ref writer);
        var start = writer.WrittenLength;
        for (var rowIndex = 0; rowIndex < rows.Length; rowIndex++)
        {
            var row = rows[rowIndex];
            if (row is null)
            {
                EncodingPrimitives.WriteRleRun(0, 1, 1, ref writer);
                continue;
            }

            var rowLength = row.Length;
            EncodingPrimitives.WriteRleRun(0, 1, 1, ref writer);
            if (rowLength > 1)
                EncodingPrimitives.WriteRleRun(1, rowLength - 1, 1, ref writer);
        }

        return CompleteLevelEncoding(start, lengthPrefix, ref writer);
    }

    static int WriteLevelSequence(List<int> levels, int bitWidth, bool writeLengthPrefix, ref BufferWriter writer)
    {
        var lengthPrefix = ReserveLevelLengthPrefix(writeLengthPrefix, ref writer);
        var start = writer.WrittenLength;
        if (levels.Count == 0)
            return CompleteLevelEncoding(start, lengthPrefix, ref writer);

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

            EncodingPrimitives.WriteRleRun(runValue, runLength, bitWidth, ref writer);
            runValue = value;
            runLength = 1;
        }

        EncodingPrimitives.WriteRleRun(runValue, runLength, bitWidth, ref writer);
        return CompleteLevelEncoding(start, lengthPrefix, ref writer);
    }

    /// <summary>
    /// Definition levels for one page of repeated rows. Required elements share a single RLE run per
    /// row; optional ones need a level per element because presence varies within the row.
    /// </summary>
    static int WriteRepeatedDefinitionLevels<TRowElement, TValue, TProbe>(ReadOnlySpan<TRowElement[]> rows,
        int listDefinedDefinitionLevel, int nullElementDefinitionLevel, int presentElementDefinitionLevel,
        bool allowsNullRow, int definitionBitWidth, bool writeLengthPrefix, ref BufferWriter writer)
        where TProbe : IOptionalRow<TRowElement, TValue>
    {
        var lengthPrefix = ReserveLevelLengthPrefix(writeLengthPrefix, ref writer);
        var start = writer.WrittenLength;
        for (var rowIndex = 0; rowIndex < rows.Length; rowIndex++)
        {
            var row = rows[rowIndex];
            if (row is null)
            {
                if (!allowsNullRow)
                    throw new InvalidOperationException("Null row is not allowed for this repeated column.");
                EncodingPrimitives.WriteRleRun(0, 1, definitionBitWidth, ref writer);
                continue;
            }

            if (row.Length == 0)
            {
                EncodingPrimitives.WriteRleRun(listDefinedDefinitionLevel, 1, definitionBitWidth, ref writer);
                continue;
            }

            if (TProbe.ValueRequired)
            {
                EncodingPrimitives.WriteRleRun(presentElementDefinitionLevel, row.Length, definitionBitWidth,
                    ref writer);
                continue;
            }

            for (var i = 0; i < row.Length; i++)
                EncodingPrimitives.WriteRleRun(
                    TProbe.IsPresent(in row[i]) ? presentElementDefinitionLevel : nullElementDefinitionLevel, 1,
                    definitionBitWidth, ref writer);
        }

        return CompleteLevelEncoding(start, lengthPrefix, ref writer);
    }

    static Span<byte> ReserveLevelLengthPrefix(bool writeLengthPrefix, ref BufferWriter writer)
    {
        if (!writeLengthPrefix)
            return [];

        var lengthPrefix = writer.GetSpan(sizeof(uint))[..sizeof(uint)];
        writer.Advance(sizeof(uint));
        return lengthPrefix;
    }

    static int CompleteLevelEncoding(int start, Span<byte> lengthPrefix, ref BufferWriter writer)
    {
        var length = writer.WrittenLength - start;
        if (!lengthPrefix.IsEmpty)
            BinaryPrimitives.WriteUInt32LittleEndian(lengthPrefix, checked((uint)length));
        return length;
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
        => page.SetDataPageMetadata(checked((uint)rowCount), checked((uint)valueCount), checked((uint)nullCount),
            checked((uint)repetitionLevelsByteLength), checked((uint)definitionLevelsByteLength), encoding);

    static IEqualityComparer<T> GetDictionaryComparer<T>()
        where T : notnull
    {
        if (typeof(T) == typeof(byte[]))
            return (IEqualityComparer<T>)(object)ByteArrayComparer.Instance;
        if (typeof(T) == typeof(ReadOnlyMemory<byte>))
            return (IEqualityComparer<T>)(object)ReadOnlyMemoryByteComparer.Instance;
        if (typeof(T) == typeof(float))
            return (IEqualityComparer<T>)(object)FloatBitwiseComparer.Instance;
        if (typeof(T) == typeof(double))
            return (IEqualityComparer<T>)(object)DoubleBitwiseComparer.Instance;

        return EqualityComparer<T>.Default;
    }

    sealed class FloatBitwiseComparer : IEqualityComparer<float>
    {
        internal static FloatBitwiseComparer Instance { get; } = new();

        public bool Equals(float x, float y)
            => BitConverter.SingleToInt32Bits(x) == BitConverter.SingleToInt32Bits(y);

        public int GetHashCode(float obj) => BitConverter.SingleToInt32Bits(obj);
    }

    sealed class DoubleBitwiseComparer : IEqualityComparer<double>
    {
        internal static DoubleBitwiseComparer Instance { get; } = new();

        public bool Equals(double x, double y)
            => BitConverter.DoubleToInt64Bits(x) == BitConverter.DoubleToInt64Bits(y);

        public int GetHashCode(double obj) => BitConverter.DoubleToInt64Bits(obj).GetHashCode();
    }

    static int GetInitialForcedDictionaryCapacity(int rowCount)
        => Math.Max(256, Math.Min(rowCount, MaximumInitialForcedDictionaryCapacity));

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
        if (typeof(T) == typeof(DateTime))
        {
            comparison = Unsafe.As<T, DateTime>(ref left).Ticks.CompareTo(Unsafe.As<T, DateTime>(ref right).Ticks);
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
