using System.Collections.Immutable;
using System.Text;
using Plank.Reading.Physical;
using Plank.Schema;
using Plank.Writing;

namespace Plank.Tests.Writer;

internal sealed class AppendFileTests
{
    [Test]
    public async Task AppendsValuesToLatestRowGroup()
    {
        var path = NewPath();
        try
        {
            var schema = CreateSchema(ParquetPhysicalType.Int32);
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var writer = schema.CreateWriter(stream);
                WriteRowGroup(writer, schema, [1, 2]);
                WriteRowGroup(writer, schema, [3, 4]);
                writer.CloseFile();
            }

            using (var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                var writer = schema.CreateAppender(stream, new ParquetAppendOptions
                {
                    AppendToLatestRowGroup = true
                });
                WriteRowGroup(writer, schema, [5, 6]);
                writer.CloseFile();
            }

            await Assert.That(ReadValues(path, schema)).IsEquivalentTo([1, 2, 3, 4, 5, 6]);
            using var reader = new ParquetFileReader();
            using var readStream = File.OpenRead(path);
            reader.Reset(readStream);
            await Assert.That(reader.Metadata.RowGroupCount).IsEqualTo(2);
            await Assert.That(reader.Metadata.RowGroups[0].RowCount).IsEqualTo(2UL);
            await Assert.That(reader.Metadata.RowGroups[1].RowCount).IsEqualTo(4UL);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Test]
    public async Task ClosingWithoutWritingPreservesLatestRowGroup()
    {
        var path = NewPath();
        try
        {
            var schema = CreateSchema(ParquetPhysicalType.Int32);
            WriteNewFile(path, schema, [1, 2, 3], ParquetWriterOptions.Default);

            using (var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                var writer = schema.CreateAppender(stream, new ParquetAppendOptions
                {
                    AppendToLatestRowGroup = true
                });
                writer.CloseFile();
            }

            await Assert.That(ReadValues(path, schema)).IsEquivalentTo([1, 2, 3]);
            using var reader = new ParquetFileReader();
            using var readStream = File.OpenRead(path);
            reader.Reset(readStream);
            await Assert.That(reader.Metadata.RowGroupCount).IsEqualTo(1);
            await Assert.That(reader.Metadata.RowGroups[0].RowCount).IsEqualTo(3UL);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Test]
    public async Task AppendsDecodedOptionalStringsToLatestRowGroup()
    {
        var path = NewPath();
        var schema = new ParquetSchema([
            ColumnDefinition.OptionalLeaf("Value", ParquetPhysicalType.ByteArray,
                logicalType: new LogicalType.String())
        ]);
        try
        {
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var writer = schema.CreateWriter(stream);
                WriteStringRowGroup(writer, schema, ["alpha", null]);
                writer.CloseFile();
            }

            using (var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                var writer = schema.CreateAppender(stream, new ParquetAppendOptions
                {
                    AppendToLatestRowGroup = true
                });
                WriteStringRowGroup(writer, schema, ["omega"]);
                writer.CloseFile();
            }

            var values = new List<string?>();
            using var readStream = File.OpenRead(path);
            using var reader = schema.CreateReader(readStream);
            foreach (var rowGroup in reader.RowGroups)
                foreach (var buffer in rowGroup.Column<byte>(0))
                    for (var i = 0; i < buffer.Count; i++)
                        values.Add(buffer.IsNull(i) ? null : Encoding.UTF8.GetString(buffer.GetValue(i)));

            await Assert.That(values).IsEquivalentTo(["alpha", null, "omega"]);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Test]
    public async Task ReopensFileAndAppendsRowGroups()
    {
        var path = NewPath();
        try
        {
            var schema = CreateSchema(ParquetPhysicalType.Int32);
            WriteNewFile(path, schema, [1, 2], new ParquetWriterOptions
            {
                CreatedBy = "initial-writer",
                KeyValueMetadata = [new ParquetKeyValueMetadata("initial", "yes")]
            });

            using (var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                var writer = schema.CreateAppender(stream, new ParquetAppendOptions
                {
                    WriterOptions = new ParquetWriterOptions
                    {
                        KeyValueMetadata = [new ParquetKeyValueMetadata("appended", "yes")]
                    }
                });
                WriteRowGroup(writer, schema, [3, 4, 5]);
                writer.CloseFile();
            }

            await Assert.That(ReadValues(path, schema)).IsEquivalentTo([1, 2, 3, 4, 5]);
            using var physicalReader = new ParquetFileReader();
            using var readStream = File.OpenRead(path);
            physicalReader.Reset(readStream);
            var metadata = physicalReader.Metadata;
            await Assert.That(metadata.RowGroupCount).IsEqualTo(2);
            await Assert.That(Encoding.UTF8.GetString(metadata.CreatedByUtf8)).IsEqualTo("initial-writer");
            await Assert.That(metadata.KeyValueMetadataCount).IsEqualTo(2);
            await Assert.That(Encoding.UTF8.GetString(metadata.KeyValueMetadataKeyUtf8(1))).IsEqualTo("appended");

            using var parquetSharpReader = new ParquetSharp.ParquetFileReader(path);
            await Assert.That(parquetSharpReader.FileMetaData.NumRowGroups).IsEqualTo(2);
            await Assert.That(parquetSharpReader.FileMetaData.NumRows).IsEqualTo(5L);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Test]
    public async Task SchemaMismatchDoesNotModifyFile()
    {
        var path = NewPath();
        try
        {
            var schema = CreateSchema(ParquetPhysicalType.Int32);
            WriteNewFile(path, schema, [1, 2], ParquetWriterOptions.Default);
            var before = await File.ReadAllBytesAsync(path).ConfigureAwait(false);
            var mismatchedSchema = CreateSchema(ParquetPhysicalType.Int64);

            using (var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                    await Task.Run(() => mismatchedSchema.CreateAppender(stream)).ConfigureAwait(false));

            var after = await File.ReadAllBytesAsync(path).ConfigureAwait(false);
            await Assert.That(after).IsEquivalentTo(before);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Test]
    public async Task ExistingMetadataCanBeReplaced()
    {
        var path = NewPath();
        try
        {
            var schema = CreateSchema(ParquetPhysicalType.Int32);
            WriteNewFile(path, schema, [1], new ParquetWriterOptions
            {
                CreatedBy = "old",
                KeyValueMetadata = [new ParquetKeyValueMetadata("old", "value")]
            });

            using (var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                var writer = schema.CreateAppender(stream, new ParquetAppendOptions
                {
                    PreserveExistingMetadata = false,
                    WriterOptions = new ParquetWriterOptions
                    {
                        CreatedBy = "new",
                        KeyValueMetadata = [new ParquetKeyValueMetadata("new", "value")]
                    }
                });
                writer.CloseFile();
            }

            using var reader = new ParquetFileReader();
            using var readStream = File.OpenRead(path);
            reader.Reset(readStream);
            await Assert.That(Encoding.UTF8.GetString(reader.Metadata.CreatedByUtf8)).IsEqualTo("new");
            await Assert.That(reader.Metadata.KeyValueMetadataCount).IsEqualTo(1);
            await Assert.That(Encoding.UTF8.GetString(reader.Metadata.KeyValueMetadataKeyUtf8(0))).IsEqualTo("new");
            await Assert.That(ReadValues(path, schema)).IsEquivalentTo([1]);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    static void WriteNewFile(string path, ParquetSchema schema, int[] values, ParquetWriterOptions options)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        var writer = schema.CreateWriter(stream, options);
        WriteRowGroup(writer, schema, values);
        writer.CloseFile();
    }

    static void WriteRowGroup(ParquetWriter writer, ParquetSchema schema, int[] values)
    {
        var column = writer.CreateSerializedColumn<int>(schema.LeafColumns[0]);
        column.Serialize(values);
        writer.StartRowGroup().Write(column);
    }

    static void WriteStringRowGroup(ParquetWriter writer, ParquetSchema schema, string?[] values)
    {
        var column = writer.CreateSerializedColumn<string>(schema.LeafColumns[0]);
        column.Serialize(values!);
        writer.StartRowGroup().Write(column);
    }

    static int[] ReadValues(string path, ParquetSchema schema)
    {
        using var stream = File.OpenRead(path);
        using var reader = schema.CreateReader(stream);
        var values = new List<int>();
        foreach (var rowGroup in reader.RowGroups)
            foreach (var buffer in rowGroup.Column<int>(0))
                values.AddRange(buffer.Values);
        return values.ToArray();
    }

    static ParquetSchema CreateSchema(ParquetPhysicalType physicalType)
        => new([
            ColumnDefinition.Leaf("Value", physicalType,
                new ColumnOptions(encodings: ImmutableArray.Create(EncodingKind.Plain)))
        ]);

    static string NewPath()
        => Path.Combine(Path.GetTempPath(), $"plank-append-{Guid.NewGuid():N}.parquet");
}
