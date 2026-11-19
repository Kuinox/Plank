using Plank.Reading;
using Plank.Reading.Physical;
using Plank.Schema;
using Plank.Writing;

namespace Plank.Tests.Writer;

internal sealed class MergeSafetyTests
{
    // PyArrow 26 file (hunt/codec-isa/harness/legacystats.py): required uint32 "v" = [1, 5, 2147483648, 3000000000], string "s" = [a, b, é, z].
    const string ModernFile = "UEFSMRUAFSAVICwVCBUAFQYVBhw2ACgEAF7QshgEAQAAABERAAAAAQAAAAUAAAAAAACAAF7QshUAFSoVKiwVCBUAFQYVBhw2ACgCw6kYAWEREQAAAAEAAABhAQAAAGICAAAAw6kBAAAAehUEGTw1ABgGc2NoZW1hFQQAFQIlABgBdiUaTKwTIBIAAAAVDCUAGAFzJQBMHAAAABYIGRwZLCYAHBUCGSUGABkYAXYVABYIFmYWZiYIPDYAKAQAXtCyGAQBAAAAEREAGRwVABUAFQIAAAAmABwVDBklBgAZGAFzFQAWCBZmFmYmbjw2ACgCw6kYAWEREQAZHBUAFQAVAgA8FgoZBhkGAAAAFswBFggmCBbMAQAZHBgMQVJST1c6c2NoZW1hGOABLy8vLy82QUFBQUFRQUFBQUFBQUtBQXdBQmdBRkFBZ0FDZ0FBQUFBQkJBQU1BQUFBQ0FBSUFBQUFCQUFJQUFBQUJBQUFBQUlBQUFCQUFBQUFCQUFBQU5qLy8vOEFBQUFGRUFBQUFCZ0FBQUFFQUFBQUFBQUFBQUVBQUFCekFBQUFCQUFFQUFRQUFBQVFBQlFBQ0FBQUFBY0FEQUFBQUJBQUVBQUFBQUFBQUFJUUFBQUFHQUFBQUFRQUFBQUFBQUFBQVFBQUFIWUFCZ0FJQUFRQUJnQUFBQ0FBQUFBQUFBQUEAGCBwYXJxdWV0LWNwcC1hcnJvdyB2ZXJzaW9uIDI2LjAuMRksHAAAHAAAAMsBAABQQVIx";

    static readonly ParquetSchema Schema = new([
        ColumnDefinition.RequiredLeaf("v", ParquetPhysicalType.Int32, logicalType: new LogicalType.Int(32, false)),
        ColumnDefinition.RequiredLeaf("s", ParquetPhysicalType.ByteArray, logicalType: new LogicalType.String()),
    ]);

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    public async Task InPlaceMergeKeepsOriginalReadableUntilFirstImport(int failure)
    {
        var original = Convert.FromBase64String(ModernFile);
        using var destination = new MemorySource(original);
        var merger = Schema.CreateMerger(destination);
        if (failure == 1)
            Assert.Throws<CorruptParquetException>(() => merger.AppendFile(new MemoryReadSource(original[..^1])));
        if (failure == 2)
            Assert.Throws<IOException>(() => merger.AppendFile(new FailingReadSource(original, failingRead: 3)));

        // The caller gives up on the merge, e.g. because the exception propagated, without CloseFile.
        await Assert.That(destination.ToArray().AsSpan().SequenceEqual(original)).IsTrue();
    }

    [Test]
    public async Task InPlaceMergeClosesAfterRolledBackImport()
    {
        var original = Convert.FromBase64String(ModernFile);
        using (var unchanged = new MemorySource(original))
        {
            Schema.CreateMerger(unchanged).CloseFile();
            await Assert.That(ReadRowGroupRowCounts(unchanged.ToArray())).IsEquivalentTo([4UL]);
        }

        using var destination = new MemorySource(original);
        var merger = Schema.CreateMerger(destination);
        Assert.Throws<IOException>(() => merger.AppendFile(new FailingReadSource(original, failingRead: 3)));
        merger.AppendFile(new MemoryReadSource(original));
        merger.CloseFile();
        await Assert.That(ReadRowGroupRowCounts(destination.ToArray())).IsEquivalentTo([4UL, 4UL]);
    }

    static ulong[] ReadRowGroupRowCounts(byte[] bytes)
    {
        using var reader = new ParquetFileReader();
        reader.Reset(new MemoryStream(bytes, writable: false));
        var counts = new ulong[reader.Metadata.RowGroupCount];
        for (var i = 0; i < counts.Length; i++)
            counts[i] = reader.Metadata.RowGroups[i].RowCount;
        return counts;
    }

    sealed class FailingReadSource(byte[] bytes, int failingRead) : IParquetReadSource
    {
        int _reads;

        public ulong Length => (ulong)bytes.Length;

        public void ReadExactly(ulong offset, Span<byte> destination)
        {
            if (++_reads == failingRead)
                throw new IOException("Injected read failure.");
            bytes.AsSpan(checked((int)offset), destination.Length).CopyTo(destination);
        }

        public void Dispose() { }
    }

    sealed class MemorySource : IParquetReadWriteSource
    {
        readonly MemoryStream _stream = new();

        internal MemorySource(byte[] bytes) => _stream.Write(bytes);

        public ulong Length => checked((ulong)_stream.Length);

        public void Open(ReadOnlySpan<byte> path, FileMode mode) => throw new NotSupportedException();

        public void Close() { }

        public void ReadExactly(ulong offset, Span<byte> destination)
        {
            _stream.Position = checked((long)offset);
            _stream.ReadExactly(destination);
        }

        public void Write(ulong offset, ReadOnlySpan<byte> source)
        {
            _stream.Position = checked((long)offset);
            _stream.Write(source);
        }

        public void SetLength(ulong length) => _stream.SetLength(checked((long)length));

        public void Flush() { }

        public void Dispose() => _stream.Dispose();

        internal byte[] ToArray() => _stream.ToArray();
    }
}
