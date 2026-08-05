#pragma warning disable CA2007
namespace Plank.IO.ZeroAlloc.Tests;

sealed class ReusableFileWriteStreamTests
{
    [Test]
    public async Task Utf8OpenOrCreateSupportsReadSeekAndSetLength()
    {
        var path = Path.Combine(Path.GetTempPath(), $"plank-zeroalloc-utf8-{Guid.NewGuid():N}.bin");

        try
        {
            await File.WriteAllBytesAsync(path, [1, 2, 3]);
            var utf8Path = System.Text.Encoding.UTF8.GetBytes(path);
            using var stream = new ReusableFileWriteStream();
            stream.Open(utf8Path, FileMode.OpenOrCreate, FileAccess.ReadWrite);

            var first = new byte[3];
            var count = stream.Read(first);
            stream.Seek(0, SeekOrigin.End);
            stream.Write([4, 5]);
            stream.Position = 1;
            var middle = new byte[3];
            var middleCount = stream.Read(middle);
            stream.SetLength(4);
            stream.CloseFile();

            await Assert.That(stream.CanRead).IsFalse();
            await Assert.That(stream.CanSeek).IsFalse();
            await Assert.That(stream.CanWrite).IsFalse();
            await Assert.That(count).IsEqualTo(3);
            await Assert.That(first.AsSpan().SequenceEqual(new byte[] { 1, 2, 3 })).IsTrue();
            await Assert.That(middleCount).IsEqualTo(3);
            await Assert.That(middle.AsSpan().SequenceEqual(new byte[] { 2, 3, 4 })).IsTrue();
            await Assert.That(await File.ReadAllBytesAsync(path)).IsEquivalentTo(new byte[] { 1, 2, 3, 4 });
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Test]
    public async Task ReadSeekAndWriteDoNotAllocateAfterWarmup()
    {
        var path = Path.Combine(Path.GetTempPath(), $"plank-zeroalloc-rw-{Guid.NewGuid():N}.bin");

        try
        {
            var utf8Path = System.Text.Encoding.UTF8.GetBytes(path);
            var buffer = new byte[] { 7 };
            using var stream = new ReusableFileWriteStream();
            stream.Open(utf8Path, FileMode.OpenOrCreate, FileAccess.ReadWrite);
            stream.Write(buffer);
            stream.Position = 0;
            _ = stream.Read(buffer);

            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 100; i++)
            {
                stream.Position = 0;
                _ = stream.Read(buffer);
                stream.Position = 0;
                stream.Write(buffer);
            }
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            await Assert.That(allocated).IsEqualTo(0);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Test]
    public async Task OpenWriteCloseAndReopenWritesDifferentFiles()
    {
        var pathA = Path.Combine(Path.GetTempPath(), $"plank-zeroalloc-a-{Guid.NewGuid():N}.bin");
        var pathB = Path.Combine(Path.GetTempPath(), $"plank-zeroalloc-b-{Guid.NewGuid():N}.bin");

        try
        {
            using var stream = new ReusableFileWriteStream();
            stream.Open(pathA);
            stream.Write([1, 2, 3]);
            stream.CloseFile();

            stream.Open(pathB);
            stream.Write([4, 5]);
            stream.CloseFile();

            var bytesA = await File.ReadAllBytesAsync(pathA);
            var bytesB = await File.ReadAllBytesAsync(pathB);
            await Assert.That(bytesA.AsSpan().SequenceEqual(new byte[] { 1, 2, 3 })).IsTrue();
            await Assert.That(bytesB.AsSpan().SequenceEqual(new byte[] { 4, 5 })).IsTrue();
        }
        finally
        {
            if (File.Exists(pathA))
                File.Delete(pathA);
            if (File.Exists(pathB))
                File.Delete(pathB);
        }
    }

    [Test]
    public async Task OpenWhileAlreadyOpenThrows()
    {
        var pathA = Path.Combine(Path.GetTempPath(), $"plank-zeroalloc-open-a-{Guid.NewGuid():N}.bin");
        var pathB = Path.Combine(Path.GetTempPath(), $"plank-zeroalloc-open-b-{Guid.NewGuid():N}.bin");

        try
        {
            using var stream = new ReusableFileWriteStream();
            stream.Open(pathA);

            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await Task.Run(() => stream.Open(pathB)));

            stream.CloseFile();
        }
        finally
        {
            if (File.Exists(pathA))
                File.Delete(pathA);
            if (File.Exists(pathB))
                File.Delete(pathB);
        }
    }

    [Test]
    public async Task DisposeActsAsCloseFileAndAllowsReuse()
    {
        var pathA = Path.Combine(Path.GetTempPath(), $"plank-zeroalloc-dispose-a-{Guid.NewGuid():N}.bin");
        var pathB = Path.Combine(Path.GetTempPath(), $"plank-zeroalloc-dispose-b-{Guid.NewGuid():N}.bin");

        try
        {
            var stream = new ReusableFileWriteStream();
            stream.Open(pathA);
            stream.Write([9]);
            await stream.DisposeAsync();

            stream.Open(pathB);
            stream.Write([8, 7]);
            stream.CloseFile();

            var bytesA = await File.ReadAllBytesAsync(pathA);
            var bytesB = await File.ReadAllBytesAsync(pathB);
            await Assert.That(bytesA.AsSpan().SequenceEqual(new byte[] { 9 })).IsTrue();
            await Assert.That(bytesB.AsSpan().SequenceEqual(new byte[] { 8, 7 })).IsTrue();
        }
        finally
        {
            if (File.Exists(pathA))
                File.Delete(pathA);
            if (File.Exists(pathB))
                File.Delete(pathB);
        }
    }

    [Test]
    public async Task WriteWithoutOpenThrows()
    {
        using var stream = new ReusableFileWriteStream();

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await Task.Run(() => stream.Write([1])));
    }

    [Test]
    public async Task CloseFileIsIdempotent()
    {
        using var stream = new ReusableFileWriteStream();
        stream.CloseFile();
        await Task.Run(stream.CloseFile);
        await Assert.That(stream.CanWrite).IsFalse();
    }
}
#pragma warning restore CA2007
