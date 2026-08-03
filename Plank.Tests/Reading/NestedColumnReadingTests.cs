using Plank.Reading;
using Plank.Reading.Logical;
using Plank.Schema;
using Plank.Writing;

namespace Plank.Tests.Reading;

[NotInParallel]
internal sealed class NestedColumnReadingTests
{
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
        Action<ParquetReader> read)
    {
        var path = GetTempPath();
        try
        {
            using (var writeStream = File.Create(path))
            {
                var writer = schema.CreateWriter(writeStream, new ParquetWriterOptions
                {
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
