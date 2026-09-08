using System.Collections.Immutable;
using System.Text;
using Plank.Reading.Physical;
using Plank.Schema;
using Plank.Writing;

namespace Plank.Tests.Writer;

internal sealed class AppendFileTests
{
    [Test]
    [Arguments(false, 0)]
    [Arguments(true, 0)]
    [Arguments(false, 1)]
    [Arguments(true, 1)]
    [Arguments(false, 2)]
    [Arguments(true, 2)]
    [Arguments(false, 3)]
    [Arguments(true, 3)]
    public async Task AppendingToLatestChecksIncomingOrderAndBoundary(bool descending, int scenario)
    {
        var path = NewPath();
        try
        {
            var schema = CreateSchema(ParquetPhysicalType.Int32);
            var sorting = new ParquetSortingColumn(0, descending: descending);
            var options = new ParquetWriterOptions { SortingColumns = [sorting] };
            int[] first = descending ? [4, 2] : [1, 3];
            int[] appended = scenario switch
            {
                0 => descending ? [3, 1] : [2, 4], // Sorted input, wrong boundary.
                1 => descending ? [1, 2] : [4, 3], // Correct boundary, unsorted input.
                2 => descending ? [2, 1] : [3, 4], // Equal boundary, sorted input.
                _ => []
            };
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var writer = schema.CreateWriter(stream, options);
                WriteRowGroup(writer, schema, first);
                WriteRowGroup(writer, schema, first);
                writer.CloseFile();
            }
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                var writer = schema.CreateAppender(stream, new ParquetAppendOptions
                {
                    AppendToLatestRowGroup = true,
                    WriterOptions = options
                });
                WriteRowGroup(writer, schema, appended);
                WriteRowGroup(writer, schema, first);
                writer.CloseFile();
            }
            await Assert.That(ReadValues(path, schema))
                .IsEquivalentTo(first.Concat(first).Concat(appended).Concat(first).ToArray());
            using var reader = new ParquetFileReader();
            using var readStream = File.OpenRead(path);
            reader.Reset(readStream);
            await Assert.That(reader.Metadata.RowGroupCount).IsEqualTo(3);
            await Assert.That(reader.Metadata.RowGroupSortingColumns(0).ToArray())
                .IsEquivalentTo(new[] { sorting });
            await Assert.That(reader.Metadata.RowGroupSortingColumns(1).Length).IsEqualTo(scenario >= 2 ? 1 : 0);
            await Assert.That(reader.Metadata.RowGroupSortingColumns(2).ToArray())
                .IsEquivalentTo(new[] { sorting });
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    public async Task AppendingChecksLexicographicKeysInDeclaredOrder(int scenario)
    {
        var path = NewPath();
        try
        {
            var schema = new ParquetSchema([
                ColumnDefinition.RequiredLeaf("Sequence", ParquetPhysicalType.Int32),
                ColumnDefinition.RequiredLeaf("Id", ParquetPhysicalType.Int32)
            ]);
            var sorting = new[] { new ParquetSortingColumn(1), new ParquetSortingColumn(0, descending: true) };
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var writer = schema.CreateWriter(stream, new ParquetWriterOptions { SortingColumns = [.. sorting] });
                WriteTwoColumns(writer, schema, [20, 10], [1, 1]);
                writer.CloseFile();
            }
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                var writer = schema.CreateAppender(stream, new ParquetAppendOptions { AppendToLatestRowGroup = true });
                int[] sequence = scenario switch { 0 => [5, 100, 90], 1 => [15, 100, 90], _ => [5, 90, 100] };
                WriteTwoColumns(writer, schema, sequence, [1, 2, 2]);
                writer.CloseFile();
            }
            using var reader = new ParquetFileReader();
            using var readStream = File.OpenRead(path);
            reader.Reset(readStream);
            await Assert.That(reader.Metadata.RowGroupCount).IsEqualTo(1);
            await Assert.That(reader.Metadata.RowGroups[0].RowCount).IsEqualTo(5UL);
            await Assert.That(reader.Metadata.RowGroupSortingColumns(0).ToArray())
                .IsEquivalentTo(scenario == 0 ? sorting : []);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    static void WriteTwoColumns(ParquetWriter writer, ParquetSchema schema, int[] sequence, int[] ids)
    {
        var first = writer.CreateSerializedColumn<int>(schema.LeafColumns[0]);
        var second = writer.CreateSerializedColumn<int>(schema.LeafColumns[1]);
        first.Serialize(sequence);
        second.Serialize(ids);
        var group = writer.StartRowGroup();
        group.Write(first);
        group.Write(second);
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task AppendingHonorsNullPlacementIndependentlyOfDirection(bool descending, bool nullsFirst)
    {
        var schema = new ParquetSchema([ColumnDefinition.OptionalLeaf("Value", ParquetPhysicalType.Int32)]);
        var sorting = new ParquetSortingColumn(0, descending, nullsFirst);
        int?[] numbers = descending ? [3, 2] : [2, 3];
        int?[] nulls = [null, null];
        await CheckAppendedSorting(schema, sorting, nullsFirst ? nulls : numbers,
            nullsFirst ? numbers : nulls, true).ConfigureAwait(false);
        await CheckAppendedSorting(schema, sorting, nullsFirst ? numbers : nulls,
            nullsFirst ? nulls : numbers, false).ConfigureAwait(false);
    }

    [Test]
    public async Task AppendingUsesUnsignedIntegerOrder()
    {
        var schema = new ParquetSchema([ColumnDefinition.RequiredLeaf("Value", ParquetPhysicalType.Int32,
            logicalType: new LogicalType.Int(32, isSigned: false))]);
        await CheckAppendedSorting<int>(schema, new ParquetSortingColumn(0), [int.MaxValue], [int.MinValue], true).ConfigureAwait(false);
        await CheckAppendedSorting<int>(schema, new ParquetSortingColumn(0), [int.MinValue], [int.MaxValue], false).ConfigureAwait(false);
    }

    [Test]
    public async Task AppendingUsesUtf8StringOrder()
    {
        var schema = new ParquetSchema([ColumnDefinition.RequiredLeaf("Value", ParquetPhysicalType.ByteArray,
            logicalType: new LogicalType.String())]);
        // UTF-16 ordinal comparison puts the surrogate pair before U+E000; UTF-8 does not.
        await CheckAppendedSorting<string>(schema, new ParquetSortingColumn(0), ["\uE000"], ["\U00010000"], true).ConfigureAwait(false);
        await CheckAppendedSorting<string>(schema, new ParquetSortingColumn(0), ["\U00010000"], ["\uE000"], false).ConfigureAwait(false);
    }

    [Test]
    public async Task AppendingDropsAmbiguousFloatingOrder()
    {
        var schema = CreateSchema(ParquetPhysicalType.Double);
        await CheckAppendedSorting<double>(schema, new ParquetSortingColumn(0), [1], [2, 3], true).ConfigureAwait(false);
        await CheckAppendedSorting<double>(schema, new ParquetSortingColumn(0), [1], [double.NaN], false).ConfigureAwait(false);
    }

    [Test]
    public async Task AppendingDoesNotInventAnOrderForRetainedRows()
    {
        var schema = CreateSchema(ParquetPhysicalType.Int32);
        var ascending = new ParquetSortingColumn(0);
        await CheckAppendedSorting<int>(schema, ascending, [3, 1], [4, 5], false,
            sourceOptions: new ParquetWriterOptions(),
            appendWriterOptions: new ParquetWriterOptions { SortingColumns = [ascending] }).ConfigureAwait(false);
        await CheckAppendedSorting<int>(schema, ascending, [1, 3], [2, 1], false,
            appendWriterOptions: new ParquetWriterOptions { SortingColumns = [new ParquetSortingColumn(0, descending: true)] }).ConfigureAwait(false);
    }

    static async Task CheckAppendedSorting<T>(ParquetSchema schema, ParquetSortingColumn sorting,
        T[] retained, T[] incoming, bool expectedSorting,
        ParquetWriterOptions? sourceOptions = null, ParquetWriterOptions? appendWriterOptions = null)
    {
        var path = NewPath();
        try
        {
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var writer = schema.CreateWriter(stream, sourceOptions ?? new ParquetWriterOptions { SortingColumns = [sorting] });
                var column = writer.CreateSerializedColumn<T>(schema.LeafColumns[0]);
                column.Serialize(retained);
                writer.StartRowGroup().Write(column);
                writer.CloseFile();
            }
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                var writer = schema.CreateAppender(stream, new ParquetAppendOptions
                {
                    AppendToLatestRowGroup = true,
                    WriterOptions = appendWriterOptions ?? ParquetWriterOptions.Default
                });
                var column = writer.CreateSerializedColumn<T>(schema.LeafColumns[0]);
                column.Serialize(incoming);
                writer.StartRowGroup().Write(column);
                writer.CloseFile();
            }
            using var reader = new ParquetFileReader();
            using var readStream = File.OpenRead(path);
            reader.Reset(readStream);
            await Assert.That(reader.Metadata.RowGroupCount).IsEqualTo(1);
            await Assert.That(reader.Metadata.RowGroups[0].RowCount).IsEqualTo((ulong)(retained.Length + incoming.Length));
            await Assert.That(reader.Metadata.RowGroupSortingColumns(0).Length).IsEqualTo(expectedSorting ? 1 : 0);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Test]
    public async Task ClosingWithoutAppendingPreservesSortingDeclaration()
    {
        var path = NewPath();
        try
        {
            var schema = CreateSchema(ParquetPhysicalType.Int32);
            var sorting = new ParquetSortingColumn(0);
            WriteNewFile(path, schema, [1, 3], new ParquetWriterOptions { SortingColumns = [sorting] });
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                var writer = schema.CreateAppender(stream, new ParquetAppendOptions
                {
                    AppendToLatestRowGroup = true
                });
                writer.CloseFile();
            }
            using var reader = new ParquetFileReader();
            using var readStream = File.OpenRead(path);
            reader.Reset(readStream);
            await Assert.That(reader.Metadata.RowGroupSortingColumns(0).ToArray())
                .IsEquivalentTo(new[] { sorting });
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

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
