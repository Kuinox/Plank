using System.Buffers.Binary;
using Plank.Reading;
using Plank.Reading.Logical;
using Plank.Reading.Physical;
using Plank.Schema;
using Plank.Tests.Reading.ParquetTesting;
using Plank.Writing;

namespace Plank.Tests.Reading;

[NotInParallel]
internal sealed class NestedColumnReadingTests
{
    [Test]
    public void RepeatedWriterRejectsNullRequiredListsAndCanRetryWithoutAllocating()
    {
        var schema = new ParquetSchema([
            ColumnDefinition.List("items", ColumnDefinition.OptionalGroup("entry",
                ColumnDefinition.OptionalLeaf("price", ParquetPhysicalType.Int32))),
            ColumnDefinition.List("outer", ColumnDefinition.List("inner",
                ColumnDefinition.RequiredLeaf("v", ParquetPhysicalType.Int32), ParquetRepetition.Required),
                ParquetRepetition.Optional)
        ]);
        using var stream = new MemoryStream();
        using var writer = schema.CreateWriter(stream, new ParquetWriterOptions { Compression = CompressionKind.None });
        var items = writer.CreateSerializedColumn<int?[]>(schema.LeafColumns[0]);
        var grid = writer.CreateSerializedColumn<int[][]>(schema.LeafColumns[1]);
        Assert.Throws<InvalidOperationException>(() => grid.Serialize([[null!]]));
        // A null element of the direct column writer is the missing record.
        int?[][] rows = [[null, 17]];
        int[][][] grids = [[[2], []]];
        for (var i = 0; i < 10; i++)
        {
            stream.SetLength(0);
            writer.Reset(stream);
            var group = writer.StartRowGroup();
            var before = GC.GetAllocatedBytesForCurrentThread();
            items.Serialize(rows);
            grid.Serialize(grids);
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            if (i == 9 && allocated != 0)
                throw new InvalidOperationException($"Warmed nested serialization allocated {allocated} bytes.");
            if (items.Statistics.NullCount != 1 || items.Statistics.MinBits != 17 || items.Statistics.MaxBits != 17)
                throw new InvalidOperationException("A missing record must count as an absent physical value.");
            group.Write(items);
            group.Write(grid);
        }
    }

    [Test]
    [Arguments(ParquetDataPageVersion.V1)]
    [Arguments(ParquetDataPageVersion.V2)]
    public void NullInnerListsWriteDistinctLevels(ParquetDataPageVersion version)
    {
        var schema = new ParquetSchema([
            ColumnDefinition.List("outer", ColumnDefinition.List("inner",
                ColumnDefinition.OptionalLeaf("value", ParquetPhysicalType.Int32), ParquetRepetition.Optional),
                ParquetRepetition.Optional)
        ]);
        WithPlankFile(schema, group =>
        {
            var inner = group.CreateSerializedColumn<int?[][]>(schema.LeafColumns[0]);
            inner.Serialize([null!, [], [null!, [], [null, 7], null!]]);
            group.Write(inner);
        }, reader =>
        {
            AssertSnapshot(Read(reader.RowGroups[0].NestedColumn<int>(0)), [7],
                [0, 0, 0, 1, 1, 2, 1], [0, 1, 2, 3, 4, 5, 2], 3, 2, 5);
        }, version);
    }

    [Test]
    [Arguments(ParquetDataPageVersion.V1)]
    [Arguments(ParquetDataPageVersion.V2)]
    public void GeneratedRowsWriteMissingRecordsAndNullFieldsAsDistinctLevels(ParquetDataPageVersion version)
    {
        using var stream = new MemoryStream();
        using (var writer = Plank.Tests.E2E.GeneratedOptionalElementsSchema.CreateRowWriter(stream, maxParallelism: 1,
                   new ParquetWriterOptions { DataPageVersion = version, Compression = CompressionKind.None }))
        {
            writer.GetRow().Items = null;
            writer.GetRow().Items = [];
            writer.GetRow().Items = [null, new() { Label = "x" }, new() { Price = 7 }];
            writer.Complete();
        }

        using var input = new MemoryStream(stream.ToArray());
        using var reader = new ParquetReader();
        reader.Reset(input);
        // items.list.element.{price,label}: 0 null list, 1 empty list, 2 missing record, 3 record without the
        // field, 4 field present.
        AssertSnapshot(Read(reader.RowGroups[0].NestedColumn<int>(0)), [7],
            [0, 0, 0, 1, 1], [0, 1, 2, 3, 4], 3, 1, 4);
        var labels = Read(reader.RowGroups[0].NestedColumn<byte>(1));
        if (!labels.DefinitionLevels.AsSpan().SequenceEqual([0, 1, 2, 4, 3]))
            throw new InvalidOperationException(
                $"Label definition levels do not match: [{string.Join(", ", labels.DefinitionLevels)}].");
    }

    [Test]
    public void ReadsDataPageV2TopLevelRepeatedPrimitive()
    {
        var schema = new ParquetSchema([
            ColumnDefinition.Leaf("values", ParquetPhysicalType.Int32,
                new ColumnOptions(ParquetRepetition.Repeated, [EncodingKind.Plain]))
        ]);
        int[][] rows = [[1, 2], [3], [4, 5]];

        WithPlankFile(schema, rowGroup =>
        {
            var serialized = rowGroup.CreateSerializedColumn<int[]>(schema.LeafColumns[0]);
            serialized.Serialize(rows);
            rowGroup.Write(serialized);
        }, reader =>
        {
            var snapshot = Read(reader.RowGroups[0].NestedColumn<int>(0));
            AssertSnapshot(snapshot, [1, 2, 3, 4, 5], [0, 1, 0, 0, 1], [1, 1, 1, 1, 1],
                rowCount: 3, maxRepetitionLevel: 1, maxDefinitionLevel: 1);
        });
    }

    [Test]
    public void FlatColumnApiDirectsRepeatedLeavesToNestedColumnApi()
    {
        var schema = new ParquetSchema([
            ColumnDefinition.Leaf("values", ParquetPhysicalType.Int32,
                new ColumnOptions(ParquetRepetition.Repeated, [EncodingKind.Plain]))
        ]);
        int[][] rows = [[1, 2], [3]];

        WithPlankFile(schema, rowGroup =>
        {
            var serialized = rowGroup.CreateSerializedColumn<int[]>(schema.LeafColumns[0]);
            serialized.Serialize(rows);
            rowGroup.Write(serialized);
        }, reader =>
        {
            try
            {
                _ = reader.RowGroups[0].Column<int>(0);
            }
            catch (NotSupportedException exception) when (exception.Message.Contains("NestedColumn<T>",
                       StringComparison.Ordinal))
            {
                return;
            }
            throw new InvalidOperationException("The flat column API did not direct the caller to NestedColumn<T>.");
        });
    }

    [Test]
    public void ReadsDataPageV2OptionalListWithOptionalElements()
    {
        var schema = new ParquetSchema([
            ColumnDefinition.List("values",
                ColumnDefinition.OptionalLeaf("value", ParquetPhysicalType.Int32,
                    new ColumnOptions(encodings: [EncodingKind.Plain])),
                ParquetRepetition.Optional)
        ]);
        int?[][] rows = [[10, null], null!, [], [20]];

        WithPlankFile(schema, rowGroup =>
        {
            var serialized = rowGroup.CreateSerializedColumn<int?[]>(schema.LeafColumns[0]);
            serialized.Serialize(rows);
            rowGroup.Write(serialized);
        }, reader =>
        {
            var snapshot = Read(reader.RowGroups[0].NestedColumn<int>(schema.LeafColumns[0]));
            AssertSnapshot(snapshot, [10, 20], [0, 1, 0, 0, 0], [3, 2, 0, 1, 3],
                rowCount: 4, maxRepetitionLevel: 1, maxDefinitionLevel: 3);
        });
    }

    [Test]
    public void RuntimeWriterPreservesRepeatedValuesUnderOptionalElementGroup()
    {
        var schema = new ParquetSchema([
            ColumnDefinition.List("items",
                ColumnDefinition.OptionalGroup("element",
                    ColumnDefinition.OptionalLeaf("value", ParquetPhysicalType.Int32)),
                ParquetRepetition.Required)
        ]);
        int?[][] rows = [[null, 10], []];

        WithPlankFile(schema, rowGroup =>
        {
            var serialized = rowGroup.CreateSerializedColumn<int?[]>(schema.LeafColumns[0]);
            serialized.Serialize(rows);
            rowGroup.Write(serialized);
        }, reader =>
        {
            var snapshot = Read(reader.RowGroups[0].NestedColumn<int>(0));
            // 1: absent optional element group; 3: group and leaf both present; 0: empty list.
            AssertSnapshot(snapshot, [10], [0, 1, 0], [1, 3, 0],
                rowCount: 2, maxRepetitionLevel: 1, maxDefinitionLevel: 3);
        });
    }

    [Test]
    public void ReadsDataPageV2OptionalMapLeaves()
    {
        var schema = new ParquetSchema([
            ColumnDefinition.Map("scores",
                ColumnDefinition.RequiredLeaf("key", ParquetPhysicalType.Int32,
                    new ColumnOptions(encodings: [EncodingKind.Plain])),
                ColumnDefinition.OptionalLeaf("value", ParquetPhysicalType.Int32,
                    new ColumnOptions(encodings: [EncodingKind.Plain])),
                ParquetRepetition.Optional)
        ]);
        int[][] keyRows = [[1, 2], null!, [], [3]];
        int?[][] valueRows = [[10, null], null!, [], [30]];

        WithPlankFile(schema, rowGroup =>
        {
            var keys = rowGroup.CreateSerializedColumn<int[]>(schema.LeafColumns[0]);
            keys.Serialize(keyRows);
            rowGroup.Write(keys);
            var values = rowGroup.CreateSerializedColumn<int?[]>(schema.LeafColumns[1]);
            values.Serialize(valueRows);
            rowGroup.Write(values);
        }, reader =>
        {
            var keys = Read(reader.RowGroups[0].NestedColumn<int>(0));
            AssertSnapshot(keys, [1, 2, 3], [0, 1, 0, 0, 0], [2, 2, 0, 1, 2],
                rowCount: 4, maxRepetitionLevel: 1, maxDefinitionLevel: 2);

            var values = Read(reader.RowGroups[0].NestedColumn<int>(1));
            AssertSnapshot(values, [10, 30], [0, 1, 0, 0, 0], [3, 2, 0, 1, 3],
                rowCount: 4, maxRepetitionLevel: 1, maxDefinitionLevel: 3);
        });
    }

    [Test]
    public void ReadsDataPageV2NestedLists()
    {
        var schema = new ParquetSchema([
            ColumnDefinition.List("outer",
                ColumnDefinition.List("inner",
                    ColumnDefinition.RequiredLeaf("value", ParquetPhysicalType.Int32,
                        new ColumnOptions(encodings: [EncodingKind.Plain])),
                    ParquetRepetition.Required),
                ParquetRepetition.Required)
        ]);
        int[][][] rows =
        [
            [[1, 2], [], [3]],
            [],
            [[4]]
        ];

        WithPlankFile(schema, rowGroup =>
        {
            var serialized = rowGroup.CreateSerializedColumn<int[][]>(schema.LeafColumns[0]);
            serialized.Serialize(rows);
            rowGroup.Write(serialized);
        }, reader =>
        {
            var snapshot = Read(reader.RowGroups[0].NestedColumn<int>(0));
            AssertSnapshot(snapshot, [1, 2, 3, 4], [0, 2, 1, 1, 0, 0], [2, 2, 1, 2, 0, 2],
                rowCount: 3, maxRepetitionLevel: 2, maxDefinitionLevel: 2);
        });
    }

    [Test]
    public void ReadsDataPageV2OptionalBinaryValues()
    {
        var schema = new ParquetSchema([
            ColumnDefinition.OptionalLeaf("values", ParquetPhysicalType.ByteArray,
                new ColumnOptions(encodings: [EncodingKind.DeltaLengthByteArray]))
        ]);
        byte[][] rows =
        [
            [1, 2],
            null!,
            [4, 5, 6]
        ];

        WithPlankFile(schema, rowGroup =>
        {
            var serialized = rowGroup.CreateSerializedColumn<byte[]>(schema.LeafColumns[0]);
            serialized.Serialize(rows);
            rowGroup.Write(serialized);
        }, reader =>
        {
            var values = new List<byte[]>();
            var repetitions = new List<int>();
            var definitions = new List<int>();
            foreach (var buffer in reader.RowGroups[0].NestedColumn<byte>(0))
            {
                for (var i = 0; i < buffer.Values.Count; i++)
                    values.Add(buffer.Values.GetValue(i).ToArray());
                repetitions.AddRange(buffer.RepetitionLevels);
                definitions.AddRange(buffer.DefinitionLevels);
            }

            AssertBinaryValues(values, [[1, 2], [4, 5, 6]]);
            if (!repetitions.ToArray().AsSpan().SequenceEqual([0, 0, 0]))
                throw new InvalidOperationException("Binary repetition levels do not match.");
            if (!definitions.ToArray().AsSpan().SequenceEqual([1, 0, 1]))
                throw new InvalidOperationException("Binary definition levels do not match.");
        });
    }

    [Test]
    public void NestedColumnEnumerationDoesNotAllocateAfterWarmup()
    {
        var schema = new ParquetSchema([
            ColumnDefinition.List("values",
                ColumnDefinition.RequiredLeaf("value", ParquetPhysicalType.Int32,
                    new ColumnOptions(encodings: [EncodingKind.Plain])),
                ParquetRepetition.Required)
        ]);
        var rows = new int[4096][];
        for (var i = 0; i < rows.Length; i++)
            rows[i] = [i, i + 1];

        WithPlankFile(schema, rowGroup =>
        {
            var serialized = rowGroup.CreateSerializedColumn<int[]>(schema.LeafColumns[0]);
            serialized.Serialize(rows);
            rowGroup.Write(serialized);
        }, reader =>
        {
            var rowGroup = reader.RowGroups[0];
            for (var i = 0; i < 8; i++)
                _ = SumNestedValues(rowGroup, schema.LeafColumns[0]);

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            var before = GC.GetAllocatedBytesForCurrentThread();
            var sum = SumNestedValues(rowGroup, schema.LeafColumns[0]);
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            if (sum == 0)
                throw new InvalidOperationException("Expected nested values.");
            if (allocated != 0)
                throw new InvalidOperationException(
                    $"Expected zero allocations for nested column enumeration but saw {allocated} bytes.");
        });
    }

    [Test]
    public void RetainedLevelsSurviveEnumeratorDisposal()
    {
        var schema = new ParquetSchema([
            ColumnDefinition.List("values",
                ColumnDefinition.RequiredLeaf("value", ParquetPhysicalType.Int32,
                    new ColumnOptions(encodings: [EncodingKind.Plain])),
                ParquetRepetition.Required)
        ]);
        int[][] rows = [[1, 2], [], [3]];

        WithPlankFile(schema, rowGroup =>
        {
            var serialized = rowGroup.CreateSerializedColumn<int[]>(schema.LeafColumns[0]);
            serialized.Serialize(rows);
            rowGroup.Write(serialized);
        }, reader =>
        {
            var enumerator = reader.RowGroups[0].NestedColumn<int>(0).GetEnumerator();
            try
            {
                if (!enumerator.MoveNext())
                    throw new InvalidOperationException("Expected a nested column buffer.");
                using var retained = enumerator.Current.RetainLevels();
                enumerator.Dispose();
                if (!retained.AsSpan<int>().SequenceEqual([0, 1, 0, 0, 1, 1, 0, 1]))
                    throw new InvalidOperationException("Retained repetition and definition levels changed.");
            }
            finally
            {
                enumerator.Dispose();
            }
        });
    }

    [Test]
    public void ReadsParquetSharpDataPageV1ListLevels()
        => AssertParquetSharpList(ParquetSharp.ParquetDataPageVersion.V1, PageHeaderType.DataPage);

    [Test]
    public void ReadsParquetSharpDataPageV2ListLevels()
        => AssertParquetSharpList(ParquetSharp.ParquetDataPageVersion.V2, PageHeaderType.DataPageV2);

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public void RejectsChunkStartingWithRowContinuation(bool acceptAllPruner)
    {
        if (!ParquetTestingCorpus.IsAvailable)
            throw new InvalidOperationException(ParquetTestingCorpus.MissingMessage);

        using var stream = File.OpenRead(ParquetTestingCorpus.Resolve("bad_data/ARROW-GH-45185.parquet"));
        using var reader = new ParquetReader();
        reader.Reset(stream, acceptAllPruner ? static (in ParquetDataPageMetadata _) => true : null);
        Assert.Throws<CorruptParquetException>(() =>
        {
            foreach (var buffer in reader.RowGroups[0].NestedColumn<int>(0))
                _ = buffer.Count;
        });
    }

    [Test]
    public void ReadsDataPageV1RowContinuedOnLaterPage()
    {
        var path = GetTempPath();
        byte[] file;
        try
        {
            using (var builder = new ParquetSharp.WriterPropertiesBuilder())
            using (var properties = builder
                       .Compression(ParquetSharp.Compression.Uncompressed)
                       .DataPageVersion(ParquetSharp.ParquetDataPageVersion.V1)
                       .DisableDictionary()
                       .DisableWritePageIndex()
                       .DataPagesize(1)
                       .WriteBatchSize(2)
                       .Build())
            using (var output = File.Create(path))
            using (var writer = new ParquetSharp.ParquetFileWriter(output,
                       [new ParquetSharp.Column<int[]>("items")], null, properties, null, true))
            using (var rowGroup = writer.AppendRowGroup())
            using (var logical = rowGroup.NextColumn().LogicalWriter<int[]>())
                logical.WriteBatch([[1, 2], [3, 4]]);
            file = File.ReadAllBytes(path);
        }
        finally
        {
            File.Delete(path);
        }

        // Writers split pages at rows, so turn the second page's levels [0, 1] into [1, 0]: rows [1, 2, 3], [4].
        int offset;
        using (var physical = new ParquetFileReader())
        {
            physical.Reset(new MemoryStream(file, writable: false));
            offset = checked((int)physical.Metadata.ColumnChunk(0, 0).DataPageOffset);
        }
        var first = PageHeaderReader.Read(file.AsSpan(offset));
        offset += first.HeaderLength + checked((int)first.CompressedPageSize);
        var second = PageHeaderReader.Read(file.AsSpan(offset));
        var levels = offset + second.HeaderLength;
        if (first.Type != PageHeaderType.DataPage || second.Type != PageHeaderType.DataPage ||
            BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(levels)) != 2 ||
            file[levels + sizeof(int)] != 0x03 || file[levels + sizeof(int) + 1] != 0x02)
            throw new InvalidOperationException("Expected two V1 pages, the second with bit-packed levels [0, 1].");
        file[levels + sizeof(int) + 1] = 0x01;

        using var reader = new ParquetReader();
        reader.Reset(new MemoryStream(file, writable: false));
        var continuations = new List<bool>();
        foreach (var buffer in reader.RowGroups[0].NestedColumn<int>(0))
            continuations.Add(buffer.StartsWithContinuation);
        if (!continuations.SequenceEqual([false, true]))
            throw new InvalidOperationException(
                $"Expected the second of two pages to continue a row: [{string.Join(", ", continuations)}].");

        var snapshot = Read(reader.RowGroups[0].NestedColumn<int>(0));
        AssertSnapshot(snapshot, [1, 2, 3, 4], [0, 1, 1, 0], [2, 2, 2, 2],
            rowCount: 2, maxRepetitionLevel: 1, maxDefinitionLevel: 2);

        reader.Reset(new MemoryStream(file, writable: false),
            static (in ParquetDataPageMetadata page) => page.PageOrdinal != 0);
        var pruned = Read(reader.RowGroups[0].NestedColumn<int>(0));
        AssertSnapshot(pruned, [3, 4], [1, 0], [2, 2],
            rowCount: 1, maxRepetitionLevel: 1, maxDefinitionLevel: 2);
    }

    static void AssertParquetSharpList(ParquetSharp.ParquetDataPageVersion pageVersion,
        PageHeaderType expectedPageType)
    {
        var path = GetTempPath();
        int[][] rows = [[1, 2], [], [3]];
        try
        {
            WriteParquetSharpList(path, rows, pageVersion);

            using var stream = File.OpenRead(path);
            using var reader = new ParquetReader();
            reader.Reset(stream);
            using (var pages = reader.PhysicalReader.OpenPages(0, 0))
            {
                while (pages.MoveNext() && pages.CurrentHeader.Type == PageHeaderType.DictionaryPage)
                {
                }
                if (pages.CurrentHeader.Type != expectedPageType)
                    throw new InvalidOperationException(
                        $"Expected a {expectedPageType} page, got {pages.CurrentHeader.Type}.");
            }

            var snapshot = Read(reader.RowGroups[0].NestedColumn<int>(0));
            AssertSnapshot(snapshot, [1, 2, 3], [0, 1, 0, 0], [2, 2, 1, 2],
                rowCount: 3, maxRepetitionLevel: 1, maxDefinitionLevel: 2);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    [Arguments(ParquetRepetition.Optional)]
    [Arguments(ParquetRepetition.Required)]
    public void RuntimeWriterAccountsForOptionalGroupAroundList(ParquetRepetition listRepetition)
    {
        var schema = new ParquetSchema([
            ColumnDefinition.OptionalGroup("s",
                ColumnDefinition.List("items", ColumnDefinition.OptionalLeaf("element", ParquetPhysicalType.Int32),
                    listRepetition))
        ]);
        int?[][] rows = [[7, null], [], null!];

        WithPlankFile(schema, rowGroup =>
        {
            var serialized = rowGroup.CreateSerializedColumn<int?[]>(schema.LeafColumns[0]);
            serialized.Serialize(rows);
            rowGroup.Write(serialized);
        }, reader =>
        {
            var snapshot = Read(reader.RowGroups[0].NestedColumn<int>(0));
            // A null row leaves the deepest optional node undefined (the list or its parent).
            var max = listRepetition == ParquetRepetition.Optional ? 4 : 3;
            AssertSnapshot(snapshot, [7], [0, 1, 0, 0], [max, max - 1, max - 2, max - 3],
                rowCount: 3, maxRepetitionLevel: 1, maxDefinitionLevel: max);
        });
    }

    [Test]
    public void RuntimeWriterAccountsForOptionalGroupAroundNestedLists()
    {
        var schema = new ParquetSchema([
            ColumnDefinition.OptionalGroup("s",
                ColumnDefinition.List("outer",
                    ColumnDefinition.List("element", ColumnDefinition.OptionalLeaf("element", ParquetPhysicalType.Int32),
                        ParquetRepetition.Required),
                    ParquetRepetition.Required))
        ]);
        int?[][][] rows = [[[1, null], []], []];

        WithPlankFile(schema, rowGroup =>
        {
            var serialized = rowGroup.CreateSerializedColumn<int?[][]>(schema.LeafColumns[0]);
            serialized.Serialize(rows);
            rowGroup.Write(serialized);
        }, reader =>
        {
            var snapshot = Read(reader.RowGroups[0].NestedColumn<int>(0));
            // 2: empty inner list; 1: empty outer list with s still defined.
            AssertSnapshot(snapshot, [1], [0, 2, 1, 0], [4, 3, 2, 1],
                rowCount: 2, maxRepetitionLevel: 2, maxDefinitionLevel: 4);
        });
    }

    static Snapshot<T> Read<T>(NestedRowGroupColumn<T> column)
    {
        var values = new List<T>();
        var repetitionLevels = new List<int>();
        var definitionLevels = new List<int>();
        var rowCount = 0;
        var maxRepetitionLevel = column.Definition.MaxRepetitionLevel;
        var maxDefinitionLevel = column.Definition.MaxDefinitionLevel;

        using var enumerator = column.GetEnumerator();
        while (enumerator.MoveNext())
        {
            var buffer = enumerator.Current;
            for (var i = 0; i < buffer.Values.Count; i++)
                values.Add(buffer.Values.Values[i]);
            for (var i = 0; i < buffer.Count; i++)
            {
                repetitionLevels.Add(buffer.RepetitionLevels[i]);
                definitionLevels.Add(buffer.DefinitionLevels[i]);
            }
            rowCount += buffer.RowCount;
            if (buffer.MaxRepetitionLevel != maxRepetitionLevel ||
                buffer.MaxDefinitionLevel != maxDefinitionLevel)
                throw new InvalidOperationException("Buffer maximum levels differ from the leaf definition.");
        }

        return new Snapshot<T>(values.ToArray(), repetitionLevels.ToArray(), definitionLevels.ToArray(),
            rowCount, maxRepetitionLevel, maxDefinitionLevel);
    }

    static long SumNestedValues(RowGroup rowGroup, LeafColumn column)
    {
        var sum = 0L;
        foreach (var buffer in rowGroup.NestedColumn<int>(column))
        {
            foreach (var value in buffer.Values.Values)
                sum += value;
            foreach (var repetitionLevel in buffer.RepetitionLevels)
                sum += repetitionLevel;
            foreach (var definitionLevel in buffer.DefinitionLevels)
                sum += definitionLevel;
        }
        return sum;
    }

    static void AssertBinaryValues(List<byte[]> actual, byte[][] expected)
    {
        if (actual.Count != expected.Length)
            throw new InvalidOperationException($"Expected {expected.Length} binary values, got {actual.Count}.");
        for (var i = 0; i < expected.Length; i++)
            if (!actual[i].AsSpan().SequenceEqual(expected[i]))
                throw new InvalidOperationException($"Binary value {i} does not match.");
    }

    static void AssertSnapshot<T>(Snapshot<T> actual, T[] expectedValues, int[] expectedRepetitionLevels,
        int[] expectedDefinitionLevels, int rowCount, int maxRepetitionLevel, int maxDefinitionLevel)
    {
        if (!actual.Values.AsSpan().SequenceEqual(expectedValues))
            throw new InvalidOperationException(
                $"Dense values do not match: [{string.Join(", ", actual.Values)}].");
        if (!actual.RepetitionLevels.AsSpan().SequenceEqual(expectedRepetitionLevels))
            throw new InvalidOperationException(
                $"Repetition levels do not match: [{string.Join(", ", actual.RepetitionLevels)}].");
        if (!actual.DefinitionLevels.AsSpan().SequenceEqual(expectedDefinitionLevels))
            throw new InvalidOperationException(
                $"Definition levels do not match: [{string.Join(", ", actual.DefinitionLevels)}].");
        if (actual.RowCount != rowCount)
            throw new InvalidOperationException($"Expected {rowCount} rows, got {actual.RowCount}.");
        if (actual.MaxRepetitionLevel != maxRepetitionLevel)
            throw new InvalidOperationException(
                $"Expected max repetition level {maxRepetitionLevel}, got {actual.MaxRepetitionLevel}.");
        if (actual.MaxDefinitionLevel != maxDefinitionLevel)
            throw new InvalidOperationException(
                $"Expected max definition level {maxDefinitionLevel}, got {actual.MaxDefinitionLevel}.");
    }

    static void WithPlankFile(ParquetSchema schema, Action<RowGroupWriter> write,
        Action<ParquetReader> read, ParquetDataPageVersion version = ParquetDataPageVersion.V2)
    {
        var path = GetTempPath();
        try
        {
            using (var writeStream = File.Create(path))
            {
                var writer = schema.CreateWriter(writeStream, new ParquetWriterOptions
                {
                    DataPageVersion = version,
                    Compression = CompressionKind.None
                });
                write(writer.StartRowGroup());
                writer.CloseFile();
            }

            using var readStream = File.OpenRead(path);
            using var reader = schema.CreateReader(readStream);
            read(reader);
        }
        finally
        {
            File.Delete(path);
        }
    }

    static void WriteParquetSharpList(string path, int[][] rows,
        ParquetSharp.ParquetDataPageVersion pageVersion)
    {
        using var builder = new ParquetSharp.WriterPropertiesBuilder();
        using var properties = builder
            .Compression(ParquetSharp.Compression.Uncompressed)
            .DataPageVersion(pageVersion)
            .Build();
        using var stream = File.Create(path);
        using var writer = new ParquetSharp.ParquetFileWriter(stream,
            [new ParquetSharp.Column<int[]>("items")], null, properties, null, true);
        using var rowGroup = writer.AppendRowGroup();
        using var logical = rowGroup.NextColumn().LogicalWriter<int[]>();
        logical.WriteBatch(rows);
    }

    static string GetTempPath()
        => Path.Combine(Path.GetTempPath(), $"plank-nested-read-{Guid.NewGuid():N}.parquet");

    sealed record Snapshot<T>(T[] Values, int[] RepetitionLevels, int[] DefinitionLevels, int RowCount,
        int MaxRepetitionLevel, int MaxDefinitionLevel);
}
