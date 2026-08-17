using Plank.Benchmarks.Published;

namespace Plank.Benchmarks.Tests;

internal sealed class NonClosingMemoryStreamTests
{
    [Test]
    public async Task DoesNotDeriveFromMemoryStream()
    {
        // MemoryStream's span and memory overloads check GetType() == typeof(MemoryStream) before
        // taking their direct-copy path, so a subclass silently routes every span write through
        // Stream's pooled-array fallback and charges the extra copy to the benchmark being timed.
        await Assert.That(typeof(NonClosingMemoryStream).IsSubclassOf(typeof(MemoryStream))).IsFalse();
        await Assert.That(typeof(NonClosingMemoryStream).BaseType).IsEqualTo(typeof(Stream));
    }

    [Test]
    public async Task DisposeKeepsTheBufferReadable()
    {
        var stream = new NonClosingMemoryStream();
        stream.Write("payload"u8);
        stream.Dispose();

        stream.Write("more"u8);

        await Assert.That(stream.Length).IsEqualTo(11L);
        await Assert.That(stream.ToArray()).IsEquivalentTo("payloadmore"u8.ToArray());
    }

    [Test]
    public async Task ResetEmptiesTheStream()
    {
        var stream = new NonClosingMemoryStream();
        stream.Write("payload"u8);

        stream.Reset();

        await Assert.That(stream.Length).IsEqualTo(0L);
        await Assert.That(stream.Position).IsEqualTo(0L);
        await Assert.That(stream.ToArray()).IsEquivalentTo(Array.Empty<byte>());
    }

    [Test]
    public async Task WritesAndReadsRoundTripThroughEveryOverload()
    {
        var stream = new NonClosingMemoryStream();
        stream.Write("span"u8);
        stream.Write("array"u8.ToArray(), 0, 5);
        stream.WriteByte((byte)'!');
        await stream.WriteAsync("async"u8.ToArray()).ConfigureAwait(false);

        await Assert.That(stream.Length).IsEqualTo(15L);
        stream.Position = 0;

        var spanTarget = new byte[4];
        await Assert.That(stream.Read(spanTarget)).IsEqualTo(4);
        await Assert.That(spanTarget).IsEquivalentTo("span"u8.ToArray());

        var arrayTarget = new byte[5];
        await Assert.That(stream.Read(arrayTarget, 0, 5)).IsEqualTo(5);
        await Assert.That(arrayTarget).IsEquivalentTo("array"u8.ToArray());

        await Assert.That(stream.ReadByte()).IsEqualTo((int)'!');

        var asyncTarget = new byte[5];
        await Assert.That(await stream.ReadAsync(asyncTarget).ConfigureAwait(false)).IsEqualTo(5);
        await Assert.That(asyncTarget).IsEquivalentTo("async"u8.ToArray());
        await Assert.That(stream.ReadByte()).IsEqualTo(-1);
    }

    [Test]
    public async Task SeekAndSetLengthTrackTheUnderlyingBuffer()
    {
        var stream = new NonClosingMemoryStream();
        stream.Write("0123456789"u8);

        await Assert.That(stream.Seek(-4, SeekOrigin.End)).IsEqualTo(6L);
        stream.SetLength(4);

        await Assert.That(stream.Length).IsEqualTo(4L);
        await Assert.That(stream.ToArray()).IsEquivalentTo("0123"u8.ToArray());

        stream.Position = 0;
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        await Assert.That(copy.ToArray()).IsEquivalentTo("0123"u8.ToArray());
    }
}
