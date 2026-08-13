namespace Plank.Benchmarks.Published;

sealed class NonClosingMemoryStream : MemoryStream
{
    public void Reset()
    {
        Position = 0;
        SetLength(0);
    }

    protected override void Dispose(bool disposing)
    {
    }
}
