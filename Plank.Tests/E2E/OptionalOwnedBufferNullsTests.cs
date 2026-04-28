using ParquetSharp;
using Plank.Schema;

namespace Plank.Tests.E2E;

[ParquetSchema]
public sealed partial class OptionalOwnedBufferRowSchema
{
    [ParquetColumn("payload", Encodings = [EncodingKind.RleDictionary])]
    public ReadOnlyMemory<byte>? Payload { get; set; }
}

internal sealed class OptionalOwnedBufferNullsTests
{
    [Test]
    public void OptionalReadOnlyMemoryByteColumnSupportsAllNullBatch()
    {
        var path = Path.Combine(Path.GetTempPath(), $"plank-optional-owned-buffer-nulls-{Guid.NewGuid():N}.parquet");

        try
        {
            using (var stream = File.Create(path))
            {
                var writer = OptionalOwnedBufferRowSchema.CreateRowWriter(stream);

                for (var i = 0; i < 32; i++)
                {
                    var row = writer.GetRow();
                    row.Payload = null;
                    writer.Next();
                }

                writer.Complete();
            }

            using var reader = new ParquetFileReader(path);
            using var rowGroup = reader.RowGroup(0);
            var values = rowGroup.Column(0).LogicalReader<byte[]?>().ReadAll(32);
            if (values.Length != 32)
                throw new InvalidOperationException($"Expected 32 values, got {values.Length}.");
            if (values.Any(static v => v is not null))
                throw new InvalidOperationException("Expected all values to be null.");
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }
}
