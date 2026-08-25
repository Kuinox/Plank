using System.Collections.Immutable;
using ParquetSharp;
using Plank.Schema;
using Plank.Writing;

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
    public void OptionalReadOnlyMemoryByteColumnSupportsBinaryEncodings()
    {
        var encodings = new[]
        {
            EncodingKind.Plain,
            EncodingKind.DeltaLengthByteArray,
            EncodingKind.DeltaByteArray,
            EncodingKind.RleDictionary
        };

        for (var i = 0; i < encodings.Length; i++)
            AssertEncoding(encodings[i]);
    }

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

    static void AssertEncoding(EncodingKind encoding)
    {
        var path = Path.Combine(Path.GetTempPath(),
            $"plank-optional-owned-buffer-{encoding}-{Guid.NewGuid():N}.parquet");
        ReadOnlyMemory<byte>?[] values =
        [
            "alpha"u8.ToArray(),
            null,
            "alphabet"u8.ToArray(),
            ReadOnlyMemory<byte>.Empty,
            "beta"u8.ToArray(),
            null
        ];

        try
        {
            var schema = new ParquetSchema([
                ColumnDefinition.OptionalLeaf("payload", ParquetPhysicalType.ByteArray,
                    new ColumnOptions(encodings: ImmutableArray.Create(encoding)))
            ]);
            using (var stream = File.Create(path))
            {
                var writer = schema.CreateWriter(stream);
                var serialized = writer.CreateSerializedColumn<ReadOnlyMemory<byte>?>(schema.LeafColumns[0]);
                serialized.Serialize(values);
                writer.StartRowGroup().Write(serialized);
                writer.CloseFile();
            }

            using var reader = new ParquetFileReader(path);
            using var rowGroup = reader.RowGroup(0);
            var actual = rowGroup.Column(0).LogicalReader<byte[]?>().ReadAll(values.Length);
            for (var i = 0; i < values.Length; i++)
            {
                if (!values[i].HasValue)
                {
                    if (actual[i] is not null)
                        throw new InvalidOperationException(
                            $"Encoding '{encoding}' value {i} should be null.");
                    continue;
                }

                if (actual[i] is null || !actual[i].AsSpan().SequenceEqual(values[i]!.Value.Span))
                    throw new InvalidOperationException(
                        $"Encoding '{encoding}' value {i} did not round-trip.");
            }
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }
}
