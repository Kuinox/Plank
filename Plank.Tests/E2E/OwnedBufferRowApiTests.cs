using System.Buffers;
using ParquetSharp;
using Plank.Schema;
using Plank.Writing;

namespace Plank.Tests.E2E;

[ParquetSchema]
public sealed partial class OwnedBufferRowSchema
{
    [ParquetColumn("payload", Encodings = [EncodingKind.Plain])]
    public ReadOnlyMemory<byte> Payload { get; set; }
}

[ParquetSchema]
public sealed partial class OwnedUtf8StringRowSchema
{
    [ParquetColumn("value", LogicalType = LogicalTypeKind.String, Encodings = [EncodingKind.Plain])]
    public ReadOnlyMemory<byte> Value { get; set; }
}

internal sealed class OwnedBufferRowApiTests
{
    [Test]
    public void GeneratedRowApiAcceptsImemoryOwnerAndDisposesOwnersAfterSlotReuse()
    {
        var path = Path.Combine(Path.GetTempPath(), $"plank-owned-buffer-row-api-{Guid.NewGuid():N}.parquet");
        var owners = new List<TrackingMemoryOwner>(1024);

        try
        {
            using (var stream = File.Create(path))
            {
                var writer = OwnedBufferRowSchema.CreateRowWriter(stream, new ParquetWriterOptions
                {
                    RowApiMaxParallelism = 1
                });

                for (var i = 0; i < 1024; i++)
                {
                    var owner = new TrackingMemoryOwner([(byte)(i & 0xFF)]);
                    owners.Add(owner);

                    var row = writer.GetRow();
                    row.SetPayload(owner);
                    writer.Next();
                }

                if (owners.Any(static o => !o.IsDisposed))
                    throw new InvalidOperationException("Expected all owners from the flushed slot to be disposed after reuse.");

                writer.Complete();
            }

            using var reader = new ParquetFileReader(path);
            if (reader.FileMetaData.NumRowGroups != 1)
                throw new InvalidOperationException($"Expected 1 row group, got {reader.FileMetaData.NumRowGroups}.");

            using var rowGroup = reader.RowGroup(0);
            var values = rowGroup.Column(0).LogicalReader<byte[]>().ReadAll(1024);
            if (values.Length != 1024)
                throw new InvalidOperationException($"Expected 1024 values, got {values.Length}.");
            if (values[0].Length != 1 || values[0][0] != 0)
                throw new InvalidOperationException("Unexpected first payload value.");
            if (values[1023].Length != 1 || values[1023][0] != 255)
                throw new InvalidOperationException("Unexpected last payload value.");
        }
        finally
        {
            foreach (var owner in owners)
                owner.Dispose();

            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Test]
    public void GeneratedRowApiAcceptsImemoryOwnerForUtf8StringColumns()
    {
        var path = Path.Combine(Path.GetTempPath(), $"plank-owned-utf8-row-api-{Guid.NewGuid():N}.parquet");
        var owners = new List<TrackingMemoryOwner>();

        try
        {
            using (var stream = File.Create(path))
            {
                var writer = OwnedUtf8StringRowSchema.CreateRowWriter(stream);

                foreach (var value in new[] { "hello", "perf", "world" })
                {
                    var owner = new TrackingMemoryOwner(System.Text.Encoding.UTF8.GetBytes(value));
                    owners.Add(owner);
                    var row = writer.GetRow();
                    row.SetValue(owner);
                    writer.Next();
                }

                writer.Complete();
            }

            using var reader = new ParquetFileReader(path);
            using var rowGroup = reader.RowGroup(0);
            var values = rowGroup.Column(0).LogicalReader<string>().ReadAll(3);
            if (!values.AsSpan().SequenceEqual(["hello", "perf", "world"]))
                throw new InvalidOperationException("Unexpected UTF-8 string values.");
        }
        finally
        {
            foreach (var owner in owners)
                owner.Dispose();
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    sealed class TrackingMemoryOwner(byte[] buffer) : IMemoryOwner<byte>
    {
        bool _disposed;

        public Memory<byte> Memory => buffer;

        public bool IsDisposed => _disposed;

        public void Dispose()
        {
            _disposed = true;
        }
    }
}
