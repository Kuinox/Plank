using Plank.Writing;

namespace Plank.Tests.E2E;

internal sealed class RowWorkerConstructionTests
{
    const int Attempts = 50;

    static readonly ParquetWriterOptions FailingStartup = new()
    {
        Execution = new ParquetExecutionOptions
        {
            WorkerCount = 8,
            OnWorkerStarted = static _ => throw new ProbeException()
        }
    };

    [Test]
    public void AStartupFaultDoesNotAbandonTheOwnedStream()
    {
        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            using var stream = new MemoryStream();
            if (!ConstructorFaulted(() => DatasetRowSchema.CreateRowWriter(stream, FailingStartup)))
                continue;
            if (stream.CanWrite)
                throw new InvalidOperationException("The failed constructor left the owned stream open.");
            return;
        }

        throw new InvalidOperationException("The startup fault never surfaced during construction.");
    }

    [Test]
    public void AStartupFaultClosesTheFileTheWriterOpened()
    {
        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            using var file = new RecordingWriteSource();
            if (!ConstructorFaulted(() => DatasetRowSchema.CreateRowWriter(SelectPath, file, FailingStartup)))
                continue;
            if (file.OpenCount != 1 || file.IsOpen)
                throw new InvalidOperationException("The failed constructor left the opened file open.");
            return;
        }

        throw new InvalidOperationException("The startup fault never surfaced during construction.");
    }

    // A startup fault reaches the caller from the constructor when a worker records it before the first slot
    // is taken, and from Complete otherwise. Only the constructor path can leak, so retry until it is hit.
    static bool ConstructorFaulted(Func<DatasetRowSchema.PipelineWriter> create)
    {
        DatasetRowSchema.PipelineWriter writer;
        try
        {
            writer = create();
        }
        catch (ProbeException)
        {
            return true;
        }

        using (writer)
        {
            try
            {
                writer.Complete();
            }
            catch (ProbeException)
            {
                return false;
            }
        }

        throw new InvalidOperationException("Expected the original worker exception.");
    }

    static ReadOnlySpan<byte> SelectPath(ulong fileIndex, IParquetBufferPool bufferPool, out ParquetBuffer? allocation)
    {
        allocation = null;
        return "probe.parquet"u8;
    }

    sealed class ProbeException : Exception;

    sealed class RecordingWriteSource : IParquetWriteSource
    {
        internal int OpenCount { get; private set; }

        internal bool IsOpen { get; private set; }

        public void Open(ReadOnlySpan<byte> path, FileMode mode)
        {
            OpenCount++;
            IsOpen = true;
        }

        public void Close()
            => IsOpen = false;

        public void Write(ulong offset, ReadOnlySpan<byte> source)
        {
        }

        public void SetLength(ulong length)
        {
        }

        public void Flush()
        {
        }

        public void Dispose()
        {
        }
    }
}
