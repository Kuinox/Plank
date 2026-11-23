using Plank.Schema;

namespace Plank.Tests.Writer;

internal sealed class StreamWriterResetTests
{
    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task FailedResetClosesTheRejectedOwnedStream(bool failOnPosition)
    {
        var schema = new ParquetSchema([ColumnDefinition.RequiredLeaf("value", ParquetPhysicalType.Int32)]);
        using var original = new MemoryStream();
        using var writer = schema.CreateWriter(original);
        writer.CloseFile();
        var rejected = new RejectedStream(failOnPosition);

        Assert.Throws<IOException>(() => writer.Reset(rejected));
        await Assert.That(rejected.DisposeCount).IsEqualTo(1);
        writer.Dispose();
        await Assert.That(rejected.DisposeCount).IsEqualTo(1);
    }

    sealed class RejectedStream(bool failOnPosition) : MemoryStream
    {
        public int DisposeCount { get; private set; }
        public override long Position
        {
            get => failOnPosition ? throw new IOException("Cannot obtain stream position.") : base.Position;
            set => base.Position = value;
        }

        public override void SetLength(long value) => throw new IOException("Cannot set stream length.");

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                DisposeCount++;
            base.Dispose(disposing);
        }
    }
}
