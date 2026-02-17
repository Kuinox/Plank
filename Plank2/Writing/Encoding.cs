using System.Buffers.Binary;
using Plank.Schema;

namespace Plank2.Writing;

static class Encoding
{
    internal static void Encode<T>(BufferWriterFactory bufferWriters, Column column, ReadOnlySpan<T> values,
        IPageStrategy strategy, PageList pages)
        where T : notnull
    {
        ArgumentNullException.ThrowIfNull(column);
        ArgumentNullException.ThrowIfNull(strategy);
        ArgumentNullException.ThrowIfNull(pages);

        pages.Clear();

        var dataEncoding = EncodingKindResolver.GetDataEncodingKind(column);
        var dictionaryEncoding = EncodingKindResolver.GetDictionaryEncodingKind(column);
        var useDictionary = TryWriteDictionaryPage(bufferWriters, column, values, strategy, pages, out var dictionary);

        if (values.Length == 0)
            return;

        var currentPageIndex = AddNewDataPage(bufferWriters, pages);
        var currentPageRowCount = 0;

        for (var i = 0; i < values.Length; i++)
        {
            ref var currentPage = ref pages[currentPageIndex];
            var value = values[i];
            if (useDictionary)
            {
                var dictionaryIndex = dictionary![value];
                DictionaryIndexEncodingDispatcher.WriteIndex(dictionaryEncoding, dictionaryIndex, ref currentPage.Content);
            }
            else
                ValueEncodingDispatcher.WriteValue(dataEncoding, column, value, ref currentPage.Content);

            currentPageRowCount++;

            var rowsWritten = i + 1;
            if (rowsWritten == values.Length)
                continue;
            if (!strategy.ShouldStartNewDataPage(column, values.Length, rowsWritten, currentPageRowCount))
                continue;

            WriteDataPageHeader(ref currentPage.Header, currentPageRowCount, useDictionary ? dictionaryEncoding : dataEncoding);
            currentPageIndex = AddNewDataPage(bufferWriters, pages);
            currentPageRowCount = 0;
        }

        ref var lastPage = ref pages[currentPageIndex];
        WriteDataPageHeader(ref lastPage.Header, currentPageRowCount, useDictionary ? dictionaryEncoding : dataEncoding);
    }

    static bool TryWriteDictionaryPage<T>(BufferWriterFactory bufferWriters, Column column, ReadOnlySpan<T> values,
        IPageStrategy strategy, PageList pages, out Dictionary<T, int>? dictionary)
        where T : notnull
    {
        var dictionaryMode = strategy.GetDictionaryMode(column);
        if (dictionaryMode == DictionaryMode.Disabled)
        {
            dictionary = null;
            return false;
        }

        var dictionaryPageIndex = AddDictionaryPage(bufferWriters, pages);
        ref var dictionaryPage = ref pages[dictionaryPageIndex];
        dictionary = new Dictionary<T, int>();
        var dictionaryValues = new List<T>();
        for (var i = 0; i < values.Length; i++)
        {
            var value = values[i];
            if (!dictionary.TryGetValue(value, out _))
            {
                dictionary.Add(value, dictionary.Count);
                dictionaryValues.Add(value);
            }

            if (dictionaryMode != DictionaryMode.Maybe)
                continue;
            if (!strategy.ShouldDropDictionary(column, dictionary, values.Length, i + 1))
                continue;

            dictionaryPage.Header.Reset();
            dictionaryPage.Content.Reset();
            pages.RemoveLast();
            dictionary = null;
            return false;
        }

        for (var i = 0; i < dictionaryValues.Count; i++)
            PlainEncoding.WriteValue(column, dictionaryValues[i], ref dictionaryPage.Content);

        const int dictionaryHeaderSize = sizeof(byte) + sizeof(int);
        var header = dictionaryPage.Header.GetSpan(dictionaryHeaderSize);
        header[0] = (byte)PageKind.Dictionary;
        BinaryPrimitives.WriteInt32LittleEndian(header[1..], dictionary.Count);
        dictionaryPage.Header.Advance(dictionaryHeaderSize);
        return true;
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
}
