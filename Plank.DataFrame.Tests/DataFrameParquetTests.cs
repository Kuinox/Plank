using Microsoft.Data.Analysis;
using ParquetSharp;
using Plank.DataFrame;
using DataFrameModel = Microsoft.Data.Analysis.DataFrame;

namespace Plank.DataFrame.Tests;

internal sealed class DataFrameParquetTests
{
    [Test]
    public void RoundTripSupportsPrimitiveStringBinaryAndTemporalColumnsAcrossRowGroups()
    {
        DateTime?[] localTimes =
        [
            new(1969, 12, 31, 23, 59, 59, DateTimeKind.Unspecified),
            null,
            new(2026, 8, 3, 10, 11, 12, DateTimeKind.Unspecified),
            new(2040, 1, 2, 3, 4, 5, DateTimeKind.Unspecified),
            new(1970, 1, 1, 0, 0, 0, DateTimeKind.Unspecified)
        ];
        DateTimeOffset?[] instants =
        [
            new DateTimeOffset(1969, 12, 31, 23, 59, 59, TimeSpan.Zero),
            null,
            new DateTimeOffset(2026, 8, 3, 12, 11, 12, TimeSpan.FromHours(2)),
            new DateTimeOffset(2040, 1, 2, 3, 4, 5, TimeSpan.Zero),
            DateTimeOffset.UnixEpoch
        ];
        var id = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");
        var frame = new DataFrameModel(
        [
            new PrimitiveDataFrameColumn<bool>("enabled", new bool?[] { true, null, false, true, false }),
            new PrimitiveDataFrameColumn<byte>("octet", new byte?[] { 0, null, byte.MaxValue, 4, 5 }),
            new PrimitiveDataFrameColumn<sbyte>("tiny", new sbyte?[] { -8, null, 7, 0, 1 }),
            new PrimitiveDataFrameColumn<short>("small", new short?[] { short.MinValue, null, 7, 0, short.MaxValue }),
            new PrimitiveDataFrameColumn<ushort>("code", new ushort?[] { 1, 2, null, ushort.MaxValue, 7 }),
            new PrimitiveDataFrameColumn<int>("count", new List<int> { 10, 20, 30, 40, 50 }),
            new PrimitiveDataFrameColumn<uint>("unsigned", new uint?[] { 0, uint.MaxValue, null, 4, 5 }),
            new PrimitiveDataFrameColumn<long>("distance", new long?[] { -1, 2, null, long.MaxValue, 5 }),
            new PrimitiveDataFrameColumn<ulong>("large", new ulong?[] { 1, 2, null, ulong.MaxValue, 5 }),
            new PrimitiveDataFrameColumn<float>("ratio", new float?[] { 1.25f, null, -2.5f, 0, 4.5f }),
            new PrimitiveDataFrameColumn<double>("score", new double?[] { 1.5, null, -2.25, 0, 9.75 }),
            new StringDataFrameColumn("name", new string?[] { "alpha", null, "", "delta", "éclair" }),
            new BinaryDataFrameColumn("payload", new byte[]?[] { [1, 2], null, [], [0xff], [3, 4, 5] }),
            new PrimitiveDataFrameColumn<Guid>("key", new Guid?[] { id, null, Guid.Empty, id, Guid.Empty }),
            new PrimitiveDataFrameColumn<DateOnly>("day", new DateOnly?[]
            {
                new(1969, 12, 31), null, new(2026, 8, 3), new(2040, 1, 2), new(1970, 1, 1)
            }),
            new PrimitiveDataFrameColumn<TimeOnly>("time", new TimeOnly?[]
            {
                new(0, 0, 0), null, new(10, 11, 12, 345), new(23, 59, 59), new(1, 2, 3)
            }),
            new PrimitiveDataFrameColumn<DateTime>("local_time", localTimes),
            new PrimitiveDataFrameColumn<DateTimeOffset>("instant", instants)
        ]);

        using var stream = new MemoryStream();
        frame.WriteParquet(stream, rowGroupSize: 2);
        using var input = new MemoryStream(stream.ToArray());
        var actual = input.ReadDataFrame();

        AssertScalarColumn(actual, "enabled", new bool?[] { true, null, false, true, false });
        AssertScalarColumn(actual, "octet", new byte?[] { 0, null, byte.MaxValue, 4, 5 });
        AssertScalarColumn(actual, "tiny", new sbyte?[] { -8, null, 7, 0, 1 });
        AssertScalarColumn(actual, "small", new short?[] { short.MinValue, null, 7, 0, short.MaxValue });
        AssertScalarColumn(actual, "code", new ushort?[] { 1, 2, null, ushort.MaxValue, 7 });
        AssertScalarColumn(actual, "count", new int?[] { 10, 20, 30, 40, 50 });
        AssertScalarColumn(actual, "unsigned", new uint?[] { 0, uint.MaxValue, null, 4, 5 });
        AssertScalarColumn(actual, "distance", new long?[] { -1, 2, null, long.MaxValue, 5 });
        AssertScalarColumn(actual, "large", new ulong?[] { 1, 2, null, ulong.MaxValue, 5 });
        AssertScalarColumn(actual, "ratio", new float?[] { 1.25f, null, -2.5f, 0, 4.5f });
        AssertScalarColumn(actual, "score", new double?[] { 1.5, null, -2.25, 0, 9.75 });
        AssertStringColumn(actual, "name", ["alpha", null, "", "delta", "éclair"]);
        AssertBinaryColumn(actual, "payload", [[1, 2], null, [], [0xff], [3, 4, 5]]);
        AssertScalarColumn(actual, "key", new Guid?[] { id, null, Guid.Empty, id, Guid.Empty });
        AssertScalarColumn(actual, "day", new DateOnly?[]
        {
            new(1969, 12, 31), null, new(2026, 8, 3), new(2040, 1, 2), new(1970, 1, 1)
        });
        AssertScalarColumn(actual, "time", new TimeOnly?[]
        {
            new(0, 0, 0), null, new(10, 11, 12, 345), new(23, 59, 59), new(1, 2, 3)
        });
        AssertScalarColumn(actual, "local_time", localTimes);
        AssertScalarColumn(actual, "instant", instants);
    }

    [Test]
    public void AdapterOutputIsReadableByParquetSharp()
    {
        var path = GetPath("write-interop");
        int?[] expectedIds = [1, null, 3, 4, 5];
        string?[] expectedNames = ["one", null, "three", "four", "five"];
        byte[]?[] expectedPayloads = [[1], null, [], [4, 4], [5]];
        DateTime[] expectedTimes =
        [
            new(2026, 8, 3, 1, 2, 3, DateTimeKind.Unspecified),
            new(2026, 8, 3, 2, 3, 4, DateTimeKind.Unspecified),
            new(2026, 8, 3, 3, 4, 5, DateTimeKind.Unspecified),
            new(2026, 8, 3, 4, 5, 6, DateTimeKind.Unspecified),
            new(2026, 8, 3, 5, 6, 7, DateTimeKind.Unspecified)
        ];
        var frame = new DataFrameModel(
        [
            new PrimitiveDataFrameColumn<int>("id", expectedIds),
            new StringDataFrameColumn("name", expectedNames),
            new BinaryDataFrameColumn("payload", expectedPayloads),
            new PrimitiveDataFrameColumn<DateTime>("when", expectedTimes)
        ]);

        try
        {
            using (var output = File.Create(path))
                frame.WriteParquet(output, rowGroupSize: 2);

            using var reader = new ParquetFileReader(path);
            if (reader.FileMetaData.NumRowGroups != 3)
                throw new InvalidOperationException($"Expected 3 row groups, got {reader.FileMetaData.NumRowGroups}.");

            var ids = new List<int?>();
            var names = new List<string?>();
            var payloads = new List<byte[]?>();
            var times = new List<DateTime>();
            for (var rowGroupIndex = 0; rowGroupIndex < reader.FileMetaData.NumRowGroups; rowGroupIndex++)
            {
                using var rowGroup = reader.RowGroup(rowGroupIndex);
                var count = checked((int)rowGroup.MetaData.NumRows);
                ids.AddRange(rowGroup.Column(0).LogicalReader<int?>().ReadAll(count));
                names.AddRange(rowGroup.Column(1).LogicalReader<string>().ReadAll(count));
                payloads.AddRange(rowGroup.Column(2).LogicalReader<byte[]?>().ReadAll(count));
                times.AddRange(rowGroup.Column(3).LogicalReader<DateTime>().ReadAll(count));
            }

            AssertSequence(ids, expectedIds, "ParquetSharp ids");
            AssertSequence(names, expectedNames, "ParquetSharp names");
            AssertBinaryValues(payloads, expectedPayloads, "ParquetSharp payloads");
            AssertSequence(times, expectedTimes, "ParquetSharp timestamps");
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Test]
    public void ReadsParquetSharpPrimitiveStringAndBinaryColumnsAcrossRowGroups()
    {
        var path = GetPath("read-interop");
        int?[] expectedIds = [10, null, 30, 40];
        string?[] expectedNames = ["ten", null, "thirty", "forty"];
        byte[]?[] expectedPayloads = [[10], null, [30, 31], []];

        try
        {
            WriteParquetSharp(path, expectedIds, expectedNames, expectedPayloads);
            using var stream = File.OpenRead(path);
            var actual = stream.ReadDataFrame();

            AssertScalarColumn(actual, "id", expectedIds);
            AssertStringColumn(actual, "name", expectedNames);
            AssertBinaryColumn(actual, "payload", expectedPayloads);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Test]
    public void EmptyColumnsRoundTripWithTheirSchema()
    {
        var frame = new DataFrameModel(
        [
            new PrimitiveDataFrameColumn<int>("id"),
            new StringDataFrameColumn("name"),
            new BinaryDataFrameColumn("payload")
        ]);

        using var stream = new MemoryStream();
        frame.WriteParquet(stream, rowGroupSize: 16);
        using var input = new MemoryStream(stream.ToArray());
        var actual = input.ReadDataFrame();

        if (actual.Columns.Count != 3 || actual.Rows.Count != 0)
            throw new InvalidOperationException("The empty DataFrame schema or row count did not round-trip.");
        if (actual.Columns[2] is not BinaryDataFrameColumn)
            throw new InvalidOperationException("The binary column did not retain its DataFrame representation.");
    }

    [Test]
    public void UnsupportedScalarColumnsAreRejectedExplicitly()
    {
        var frame = new DataFrameModel(
        [
            new PrimitiveDataFrameColumn<decimal>("amount", new List<decimal> { 1.25m, 2.5m })
        ]);
        using var stream = new MemoryStream();

        AssertThrows<NotSupportedException>(() => frame.WriteParquet(stream));
    }

    [Test]
    public void NestedSchemasAreRejectedInsteadOfSilentlyFlattened()
    {
        var schema = new Plank.Schema.ParquetSchema(
        [
            Plank.Schema.ColumnDefinition.RequiredGroup("record",
                Plank.Schema.ColumnDefinition.RequiredLeaf("value", Plank.Schema.ParquetPhysicalType.Int32))
        ]);
        using var output = new MemoryStream();
        var writer = schema.CreateWriter(output);
        var serialized = writer.CreateSerializedColumn<int>(schema.LeafColumns[0]);
        serialized.Serialize([1, 2, 3]);
        writer.StartRowGroup().Write(serialized);
        writer.CloseFile();

        using var input = new MemoryStream(output.ToArray());
        using var reader = new Plank.Reading.Logical.ParquetReader();
        reader.Reset(input);
        AssertThrows<NotSupportedException>(() => reader.ToDataFrame());
    }

    [Test]
    public void BinaryColumnParticipatesInDataFrameColumnOperations()
    {
        var column = new BinaryDataFrameColumn("payload", new byte[]?[] { [2], null, [1] });
        if (column.NullCount != 1)
            throw new InvalidOperationException($"Expected one null, got {column.NullCount}.");

        var clone = (BinaryDataFrameColumn)column.Clone();
        AssertBinaryValues(clone, [[2], null, [1]], "binary clone");

        var filled = (BinaryDataFrameColumn)column.FillNulls(new byte[] { 0 });
        AssertBinaryValues(filled, [[2], [0], [1]], "filled binary column");

        var dropped = (BinaryDataFrameColumn)column.DropNulls();
        AssertBinaryValues(dropped, [[2], [1]], "binary column without nulls");

        var sorted = (BinaryDataFrameColumn)column.Sort();
        AssertBinaryValues(sorted, [[1], [2], null], "sorted binary column");

        var intMap = new PrimitiveDataFrameColumn<int>("map", new List<int> { 2, 0, 1 });
        var invertedIntMap = (BinaryDataFrameColumn)column.Clone(intMap, invertMapIndices: true);
        AssertBinaryValues(invertedIntMap, [null, [2], [1]], "inverted int map");

        var longMap = new PrimitiveDataFrameColumn<long>("map", new long?[] { 2, null, 0 });
        var invertedLongMap = (BinaryDataFrameColumn)column.Clone(longMap, invertMapIndices: true);
        AssertBinaryValues(invertedLongMap, [[2], null, [1]], "inverted long map");
    }

    static void WriteParquetSharp(string path, int?[] ids, string?[] names, byte[]?[] payloads)
    {
        using var stream = File.Create(path);
        using var propertiesBuilder = new WriterPropertiesBuilder();
        using var properties = propertiesBuilder.Build();
        using var writer = new ParquetFileWriter(stream,
        [
            new Column<int?>("id"),
            new Column<string>("name"),
            new Column<byte[]>("payload")
        ], null, properties, null, true);
        WriteParquetSharpRowGroup(writer, ids.AsSpan(0, 2), names.AsSpan(0, 2), payloads.AsSpan(0, 2));
        WriteParquetSharpRowGroup(writer, ids.AsSpan(2, 2), names.AsSpan(2, 2), payloads.AsSpan(2, 2));
        writer.Close();
    }

    static void WriteParquetSharpRowGroup(ParquetFileWriter writer, ReadOnlySpan<int?> ids,
        ReadOnlySpan<string?> names, ReadOnlySpan<byte[]?> payloads)
    {
        using var rowGroup = writer.AppendRowGroup();
        using (var idWriter = rowGroup.NextColumn().LogicalWriter<int?>())
            idWriter.WriteBatch(ids);
        using (var nameWriter = rowGroup.NextColumn().LogicalWriter<string>())
            nameWriter.WriteBatch(names!);
        using (var payloadWriter = rowGroup.NextColumn().LogicalWriter<byte[]?>())
            payloadWriter.WriteBatch(payloads);
    }

    static void AssertScalarColumn<T>(DataFrameModel frame, string name, IReadOnlyList<T?> expected)
        where T : struct
    {
        var column = frame.Columns[name];
        if (column.DataType != typeof(T))
            throw new InvalidOperationException(
                $"Column '{name}' has DataFrame type '{column.DataType}', expected '{typeof(T)}'.");
        if (column.Length != expected.Count)
            throw new InvalidOperationException(
                $"Column '{name}' has {column.Length} rows, expected {expected.Count}.");

        for (var i = 0; i < expected.Count; i++)
        {
            var actual = column[i] is T value ? value : (T?)null;
            if (!EqualityComparer<T?>.Default.Equals(actual, expected[i]))
                throw new InvalidOperationException(
                    $"Column '{name}' differs at row {i}: expected '{expected[i]}', got '{actual}'.");
        }
    }

    static void AssertStringColumn(DataFrameModel frame, string name, string?[] expected)
    {
        var column = frame.Columns[name];
        if (column.DataType != typeof(string))
            throw new InvalidOperationException(
                $"Column '{name}' has DataFrame type '{column.DataType}', expected '{typeof(string)}'.");
        for (var i = 0; i < expected.Length; i++)
            if (!string.Equals((string?)column[i], expected[i], StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"Column '{name}' differs at row {i}: expected '{expected[i]}', got '{column[i]}'.");
    }

    static void AssertBinaryColumn(DataFrameModel frame, string name, IReadOnlyList<byte[]?> expected)
    {
        if (frame.Columns[name] is not BinaryDataFrameColumn column)
            throw new InvalidOperationException($"Column '{name}' is not a BinaryDataFrameColumn.");
        AssertBinaryValues(column, expected, name);
    }

    static void AssertBinaryValues(IEnumerable<byte[]?> actualValues, IReadOnlyList<byte[]?> expected, string label)
    {
        var actual = actualValues.ToArray();
        if (actual.Length != expected.Count)
            throw new InvalidOperationException($"{label} has {actual.Length} rows, expected {expected.Count}.");
        for (var i = 0; i < expected.Count; i++)
            if (actual[i] is null != (expected[i] is null) ||
                actual[i] is not null && expected[i] is not null && !actual[i].AsSpan().SequenceEqual(expected[i]))
                throw new InvalidOperationException($"{label} differs at row {i}.");
    }

    static void AssertSequence<T>(IReadOnlyList<T> actual, IReadOnlyList<T> expected, string label)
    {
        if (actual.Count != expected.Count)
            throw new InvalidOperationException($"{label} has {actual.Count} rows, expected {expected.Count}.");
        for (var i = 0; i < expected.Count; i++)
            if (!EqualityComparer<T>.Default.Equals(actual[i], expected[i]))
                throw new InvalidOperationException(
                    $"{label} differs at row {i}: expected '{expected[i]}', got '{actual[i]}'.");
    }

    static void AssertThrows<TException>(Action action)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException($"Expected an exception of type '{typeof(TException)}'.");
    }

    static string GetPath(string name)
        => Path.Combine(Path.GetTempPath(), $"plank-dataframe-{name}-{Guid.NewGuid():N}.parquet");
}
