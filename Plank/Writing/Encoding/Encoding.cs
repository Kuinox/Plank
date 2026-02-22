using System.Buffers.Binary;
using System.Collections.Generic;
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

                WriteDataPageHeader(ref page.Header, pageRowCount, useDictionary ? dictionaryEncoding : dataEncoding);
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

    static void WriteDataPageHeader(ref BufferWriter headerWriter, int rowCount, EncodingKind encoding)
    {
        const int dataPageHeaderSize = sizeof(byte) + sizeof(byte) + sizeof(int);
        var header = headerWriter.GetSpan(dataPageHeaderSize);
        header[0] = (byte)PageKind.DataV1;
        header[1] = (byte)encoding;
        BinaryPrimitives.WriteInt32LittleEndian(header[2..], rowCount);
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
