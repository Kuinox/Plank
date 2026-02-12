#pragma warning disable CA2007
namespace Plank.IO.ZeroAlloc.Tests;

sealed class ReusableFileWriteStreamTests
{
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
