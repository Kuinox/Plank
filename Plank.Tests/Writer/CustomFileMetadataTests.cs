using System.Collections.Immutable;
using System.Text;
using Plank.Reading.Physical;
using Plank.Schema;
using Plank.Writing;

namespace Plank.Tests.Writer;

internal sealed class CustomFileMetadataTests
{
    [Test]
    public async Task CustomMetadataRoundTripsWithoutMaterializingStrings()
    {
        using var stream = WriteFile(new ParquetWriterOptions
        {
            CreatedBy = "metadata-test",
            KeyValueMetadata =
            [
                new ParquetKeyValueMetadata("duplicate", "first"),
                new ParquetKeyValueMetadata("duplicate", null),
                new ParquetKeyValueMetadata("empty", "")
            ]
        });
        using var reader = new ParquetFileReader();
        reader.Reset(stream);
        var metadata = reader.Metadata;

        await Assert.That(metadata.HasCreatedBy).IsTrue();
        await Assert.That(Encoding.UTF8.GetString(metadata.CreatedByUtf8)).IsEqualTo("metadata-test");
        await Assert.That(metadata.KeyValueMetadataCount).IsEqualTo(3);
        await Assert.That(Encoding.UTF8.GetString(metadata.KeyValueMetadataKeyUtf8(0))).IsEqualTo("duplicate");
        await Assert.That(Encoding.UTF8.GetString(metadata.KeyValueMetadataValueUtf8(0))).IsEqualTo("first");
        await Assert.That(metadata.KeyValueMetadata[0].HasValue).IsTrue();
        await Assert.That(metadata.KeyValueMetadata[1].HasValue).IsFalse();
        await Assert.That(metadata.KeyValueMetadata[2].HasValue).IsTrue();
        await Assert.That(metadata.KeyValueMetadata[2].ValueLength).IsEqualTo(0);
    }

    [Test]
    public async Task CreatedByCanBeOmitted()
    {
        using var stream = WriteFile(new ParquetWriterOptions { CreatedBy = null });
        using var reader = new ParquetFileReader();
        reader.Reset(stream);

        await Assert.That(reader.Metadata.HasCreatedBy).IsFalse();
        await Assert.That(reader.Metadata.CreatedByUtf8.Length).IsEqualTo(0);
        await Assert.That(reader.Metadata.KeyValueMetadataCount).IsEqualTo(0);
    }

    [Test]
    public async Task WriterSnapshotsMetadataOptions()
    {
        var entries = new List<ParquetKeyValueMetadata>
        {
            new("stable", "value")
        };
        var schema = CreateSchema();
        var destination = new MemoryStream();
        var writer = schema.CreateWriter(destination, new ParquetWriterOptions
        {
            KeyValueMetadata = entries
        });
        entries[0] = new ParquetKeyValueMetadata("changed", "later");
        WriteRowGroup(writer, schema);
        writer.CloseFile();

        using var stream = new MemoryStream(destination.ToArray());
        using var reader = new ParquetFileReader();
        reader.Reset(stream);
        await Assert.That(Encoding.UTF8.GetString(reader.Metadata.KeyValueMetadataKeyUtf8(0)))
            .IsEqualTo("stable");
    }

    [Test]
    public async Task MetadataIsReadableByParquetSharp()
    {
        var path = Path.Combine(Path.GetTempPath(), $"plank-custom-metadata-{Guid.NewGuid():N}.parquet");
        try
        {
            using var stream = WriteFile(new ParquetWriterOptions
            {
                CreatedBy = "Plank metadata interop",
                KeyValueMetadata = [new ParquetKeyValueMetadata("source", "interop-test")]
            });
            await File.WriteAllBytesAsync(path, stream.ToArray()).ConfigureAwait(false);

            using var reader = new ParquetSharp.ParquetFileReader(path);
            await Assert.That(reader.FileMetaData.CreatedBy).IsEqualTo("Plank metadata interop");
            await Assert.That(reader.FileMetaData.KeyValueMetadata["source"]).IsEqualTo("interop-test");
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Test]
    public async Task EmptyMetadataKeysAreRejected()
    {
        var options = new ParquetWriterOptions
        {
            KeyValueMetadata = [new ParquetKeyValueMetadata("", "value")]
        };

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await Task.Run(options.Validate).ConfigureAwait(false));
    }

    static MemoryStream WriteFile(ParquetWriterOptions options)
    {
        var schema = CreateSchema();
        var destination = new MemoryStream();
        var writer = schema.CreateWriter(destination, options);
        WriteRowGroup(writer, schema);
        writer.CloseFile();
        return new MemoryStream(destination.ToArray());
    }

    static ParquetSchema CreateSchema()
        => new([
            ColumnDefinition.Leaf("Value", ParquetPhysicalType.Int32,
                new ColumnOptions(encodings: ImmutableArray.Create(EncodingKind.Plain)))
        ]);

    static void WriteRowGroup(ParquetWriter writer, ParquetSchema schema)
    {
        var column = writer.CreateSerializedColumn<int>(schema.LeafColumns[0]);
        column.Serialize([1]);
        writer.StartRowGroup().Write(column);
    }
}
