using System.Buffers.Binary;
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
        IPageStrategy strategy, PageList pages)
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
            EncodeRepeatedRows(bufferWriters, column, values, pages);
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

                WriteDataPageHeader(ref page.Header, pageRowCount, pageRowCount, 0, 0, 0,
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
        var initialUniqueCapacity = Math.Clamp(values.Length / 4, 256, 65_536);
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

            const int dictionaryHeaderSize = sizeof(byte) + sizeof(int);
            var header = dictionaryPage.Header.GetSpan(dictionaryHeaderSize);
            header[0] = (byte)PageKind.Dictionary;
            BinaryPrimitives.WriteInt32LittleEndian(header[1..], dictionary.Count);
            dictionaryPage.Header.Advance(dictionaryHeaderSize);
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

    static void EncodeRepeatedRows<T>(BufferWriterFactory bufferWriters, Column column, ReadOnlySpan<T> rows, PageList pages)
        where T : notnull
    {
        var dataEncoding = EncodingKindResolver.GetDataEncodingKind(column);
        var pageIndex = AddNewDataPage(bufferWriters, pages);
        ref var page = ref pages[pageIndex];

        switch (column.PhysicalType)
        {
            case ParquetPhysicalType.Boolean:
                if (typeof(T) == typeof(bool[]))
                {
                    EncodeRepeatedRowsCore(bufferWriters, column, dataEncoding,
                        Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<bool[]>>(ref rows), ref page);
                    return;
                }
                break;
            case ParquetPhysicalType.Int32:
                if (typeof(T) == typeof(int[]))
                {
                    EncodeRepeatedRowsCore(bufferWriters, column, dataEncoding,
                        Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<int[]>>(ref rows), ref page);
                    return;
                }
                break;
            case ParquetPhysicalType.Int64:
                if (typeof(T) == typeof(long[]))
                {
                    EncodeRepeatedRowsCore(bufferWriters, column, dataEncoding,
                        Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<long[]>>(ref rows), ref page);
                    return;
                }
                break;
            case ParquetPhysicalType.Float:
                if (typeof(T) == typeof(float[]))
                {
                    EncodeRepeatedRowsCore(bufferWriters, column, dataEncoding,
                        Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<float[]>>(ref rows), ref page);
                    return;
                }
                break;
            case ParquetPhysicalType.Double:
                if (typeof(T) == typeof(double[]))
                {
                    EncodeRepeatedRowsCore(bufferWriters, column, dataEncoding,
                        Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<double[]>>(ref rows), ref page);
                    return;
                }
                break;
            case ParquetPhysicalType.ByteArray:
            case ParquetPhysicalType.Int96:
            case ParquetPhysicalType.FixedLenByteArray:
                if (typeof(T) == typeof(byte[][]))
                {
                    EncodeRepeatedRowsCore(bufferWriters, column, dataEncoding,
                        Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<byte[][]>>(ref rows), ref page);
                    return;
                }
                break;
        }

        throw new InvalidOperationException(
            $"Repeated column '{column.Name}' with physical type '{column.PhysicalType}' expects rows of '{column.PhysicalType}[]'.");
    }

    static void EncodeRepeatedRowsCore<TElement>(BufferWriterFactory bufferWriters, Column column, EncodingKind dataEncoding,
        ReadOnlySpan<TElement[]> rows, ref Page page)
        where TElement : notnull
    {
        var rowCount = rows.Length;
        var valueCount = 0;
        for (var rowIndex = 0; rowIndex < rowCount; rowIndex++)
        {
            var row = rows[rowIndex] ?? throw new InvalidOperationException(
                $"Column '{column.Name}' has repeated values; null row arrays are not supported.");
            if (row.Length == 0)
                throw new InvalidOperationException(
                    $"Column '{column.Name}' has repeated values; empty rows are not supported yet.");
            valueCount = checked(valueCount + row.Length);
        }

        var flatValues = new TElement[valueCount];
        var flatIndex = 0;
        for (var rowIndex = 0; rowIndex < rowCount; rowIndex++)
        {
            var row = rows[rowIndex];
            row.CopyTo(flatValues.AsSpan(flatIndex));
            flatIndex += row.Length;
        }

        var repetitionLength = WriteRepeatedLevels(rows, ref page.Content);
        var definitionLength = WriteRepeatedRequiredElementDefinitionLevels(valueCount, ref page.Content);
        ValueEncodingDispatcher.WriteValues(dataEncoding, column, flatValues, bufferWriters, ref page.Content);
        WriteDataPageHeader(ref page.Header, rowCount, valueCount, 0, repetitionLength, definitionLength, dataEncoding);
    }

    static int WriteRepeatedLevels<TElement>(ReadOnlySpan<TElement[]> rows, ref BufferWriter writer)
        where TElement : notnull
    {
        var start = writer.WrittenLength;
        for (var rowIndex = 0; rowIndex < rows.Length; rowIndex++)
        {
            var rowLength = rows[rowIndex].Length;
            WriteLevelRun(0, 1, 1, ref writer);
            if (rowLength > 1)
                WriteLevelRun(1, rowLength - 1, 1, ref writer);
        }

        return writer.WrittenLength - start;
    }

    static int WriteRepeatedRequiredElementDefinitionLevels(int valueCount, ref BufferWriter writer)
    {
        var start = writer.WrittenLength;
        WriteLevelRun(1, valueCount, 1, ref writer);
        return writer.WrittenLength - start;
    }

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
        return pages.Count - 1;
    }

    static int AddNewDataPage(BufferWriterFactory bufferWriters, PageList pages)
    {
        ref var page = ref pages.Add();
        EnsureInitialized(bufferWriters, ref page.Header, useColumnBuffer: false);
        EnsureInitialized(bufferWriters, ref page.Content, useColumnBuffer: false);
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

    static void WriteDataPageHeader(ref BufferWriter headerWriter, int rowCount, int valueCount, int nullCount,
        int repetitionLevelsByteLength, int definitionLevelsByteLength, EncodingKind encoding)
    {
        const int dataPageHeaderSize = sizeof(byte) + sizeof(byte) + sizeof(int) + sizeof(int) + sizeof(int) +
                                       sizeof(int) + sizeof(int);
        var header = headerWriter.GetSpan(dataPageHeaderSize);
        header[0] = (byte)PageKind.DataV2;
        header[1] = (byte)encoding;
        BinaryPrimitives.WriteInt32LittleEndian(header[2..], rowCount);
        BinaryPrimitives.WriteInt32LittleEndian(header[6..], nullCount);
        BinaryPrimitives.WriteInt32LittleEndian(header[10..], valueCount);
        BinaryPrimitives.WriteInt32LittleEndian(header[14..], repetitionLevelsByteLength);
        BinaryPrimitives.WriteInt32LittleEndian(header[18..], definitionLevelsByteLength);
        headerWriter.Advance(dataPageHeaderSize);
    }

    static IEqualityComparer<T> GetDictionaryComparer<T>()
        where T : notnull
    {
        if (typeof(T) == typeof(byte[]))
            return (IEqualityComparer<T>)(object)ByteArrayComparer.Instance;

        return EqualityComparer<T>.Default;
    }
}
