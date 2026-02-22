using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Plank.Schema;

namespace Plank.Writing;

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
            out var dictionaryValueCount, out var dictionaryIndexes);

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
                    if (dictionaryIndexes is null)
                        throw new InvalidOperationException("Dictionary index buffer is missing for dictionary-encoded page.");
                    DictionaryIndexEncodingDispatcher.WriteIndexes(dictionaryEncoding,
                        dictionaryIndexes.AsSpan(pageStart, pageRowCount), dictionaryValueCount, ref page.Content);
                }
                else
                    ValueEncodingDispatcher.WriteValues(dataEncoding, column, pageValues, ref page.Content);

                WriteDataPageHeader(ref page.Header, pageRowCount, useDictionary ? dictionaryEncoding : dataEncoding);
            }
        }
        finally
        {
            if (dictionaryIndexes is not null)
                ArrayPool<int>.Shared.Return(dictionaryIndexes);
        }
    }

    static bool TryWriteDictionaryPage<T>(BufferWriterFactory bufferWriters, Column column, ReadOnlySpan<T> values,
        IPageStrategy strategy, PageList pages, out int dictionaryValueCount, out int[]? dictionaryIndexes)
        where T : notnull
    {
        dictionaryValueCount = 0;
        dictionaryIndexes = null;

        var dictionaryMode = strategy.GetDictionaryMode(column);
        if (dictionaryMode == DictionaryMode.Disabled)
        {
            return false;
        }

        var dictionaryPageIndex = AddDictionaryPage(bufferWriters, pages);
        ref var dictionaryPage = ref pages[dictionaryPageIndex];
        var dictionary = new Dictionary<T, int>(GetDictionaryComparer<T>());
        var dictionaryValues = new List<T>();
        int[]? rentedIndexes = ArrayPool<int>.Shared.Rent(Math.Max(values.Length, 1));
        try
        {
            var indexes = rentedIndexes.AsSpan(0, values.Length);
            for (var i = 0; i < values.Length; i++)
            {
                var value = values[i];
                if (!dictionary.TryGetValue(value, out var index))
                {
                    index = dictionary.Count;
                    dictionary.Add(value, index);
                    dictionaryValues.Add(value);
                }

                indexes[i] = index;

                if (dictionaryMode != DictionaryMode.Maybe)
                    continue;
                var rowsSeen = i + 1;
                if (rowsSeen % DictionaryDropCheckPeriodRows != 0 && rowsSeen != values.Length)
                    continue;
                if (!strategy.ShouldDropDictionary(column, dictionary, values.Length, rowsSeen))
                    continue;

                dictionaryPage.Header.Reset();
                dictionaryPage.Content.Reset();
                pages.RemoveLast();
                ArrayPool<int>.Shared.Return(rentedIndexes);
                rentedIndexes = null;
                return false;
            }

            PlainEncoding.WriteValues(column, CollectionsMarshal.AsSpan(dictionaryValues), ref dictionaryPage.Content);

            const int dictionaryHeaderSize = sizeof(byte) + sizeof(int);
            var header = dictionaryPage.Header.GetSpan(dictionaryHeaderSize);
            header[0] = (byte)PageKind.Dictionary;
            BinaryPrimitives.WriteInt32LittleEndian(header[1..], dictionary.Count);
            dictionaryPage.Header.Advance(dictionaryHeaderSize);
            dictionaryValueCount = dictionary.Count;
            dictionaryIndexes = rentedIndexes;
            rentedIndexes = null;
            return true;
        }
        finally
        {
            if (rentedIndexes is not null)
                ArrayPool<int>.Shared.Return(rentedIndexes);
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
