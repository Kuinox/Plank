using Plank.Schema;

namespace Plank.Tests.Writer;

internal sealed class NonSeekableWriteFailureTests
{
    static readonly ParquetSchema Schema = new([
        ColumnDefinition.RequiredLeaf("value", ParquetPhysicalType.Int32)
    ]);

    [Test]
    public void CannotRetryCloseAfterAPartialFooterWrite()
    {
        using var stream = new PartialWriteStream();
        using var writer = Schema.CreateWriter(stream);
        stream.FailNextWrite = true;

        Assert.Throws<IOException>(writer.CloseFile);
        var bytesWritten = stream.BytesWritten;
        Assert.Throws<InvalidOperationException>(writer.CloseFile);
        if (stream.BytesWritten != bytesWritten)
            throw new InvalidOperationException("The rejected retry wrote more bytes.");
    }

    [Test]
    public void CannotRetryColumnAfterAPartialPageWrite()
    {
        using var stream = new PartialWriteStream();
        using var writer = Schema.CreateWriter(stream);
        var column = writer.CreateSerializedColumn<int>(Schema.LeafColumns[0]);
        column.Serialize([42]);
        var rowGroup = writer.StartRowGroup();
        stream.FailNextWrite = true;

        Assert.Throws<IOException>(() => rowGroup.Write(column));
        Assert.Throws<InvalidOperationException>(() => rowGroup.Write(column));
    }

    sealed class PartialWriteStream : Stream
    {
        public bool FailNextWrite { get; set; }
        public int BytesWritten { get; private set; }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (FailNextWrite)
            {
                FailNextWrite = false;
                BytesWritten++;
                throw new IOException("The first byte was written before the stream failed.");
            }
            BytesWritten += buffer.Length;
        }
    }
}
