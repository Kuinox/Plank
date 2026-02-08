using System.Reflection;
using Plank.Schema;
using Plank.Writing;
using PlankColumn = Plank.Schema.Column;

#pragma warning disable CA2007
namespace Plank.Tests;

internal sealed class WriterEdgeCaseTests
{
    [Test]
    public async Task RowGroupWriterEqualityOperatorsWork()
    {
        var schema = new ParquetSchema([
            new PlankColumn("A", ParquetPhysicalType.Int32, ColumnOptions.Default),
            new PlankColumn("B", ParquetPhysicalType.Int32, ColumnOptions.Default)
        ]);
        using var stream = new MemoryStream();
        using var writer = ParquetWriter.Create(stream, schema, new ParquetWriterOptions
        {
            ExpectedRowGroupCount = 2
        });
        using var otherStream = new MemoryStream();
        using var otherWriter = ParquetWriter.Create(otherStream, schema);

        var rg1 = writer.StartRowGroup();
        await rg1.WriteAsync(schema.Columns[0], [1, 2]);
        await rg1.WriteAsync(schema.Columns[1], [3, 4]);

        var rg2 = writer.StartRowGroup();
        var rgOther = otherWriter.StartRowGroup();

        await Assert.That(rg1.Equals(rg1)).IsTrue();
        var rg1Copy = rg1;
        await Assert.That(rg1 == rg1Copy).IsTrue();
        await Assert.That(rg1 != rg1Copy).IsFalse();
        await Assert.That(rg1.Equals((object)rg1)).IsTrue();
        await Assert.That(rg1.Equals(rg2)).IsTrue();
        await Assert.That(rg1 == rg2).IsTrue();
        await Assert.That(rg1 != rg2).IsFalse();
        await Assert.That(rg1.Equals(rgOther)).IsFalse();
        await Assert.That(rg1 == rgOther).IsFalse();
        await Assert.That(rg1 != rgOther).IsTrue();
        await Assert.That(rg1.GetHashCode()).IsNotEqualTo(0);
        await Assert.That(rg1.Equals(new object())).IsFalse();
    }

    [Test]
    public async Task StartRowGroupAcceptsSameOptionsReference()
    {
        using var stream = new MemoryStream();
        var schema = new ParquetSchema([
            new PlankColumn("A", ParquetPhysicalType.Int32, ColumnOptions.Default)
        ]);
        var rowGroupOptions = new RowGroupOptions
        {
            MaxCompressedBytes = 2048
        };
        using var writer = ParquetWriter.Create(stream, schema, new ParquetWriterOptions
        {
            RowGroupOptions = rowGroupOptions
        });

        var rowGroup = writer.StartRowGroup(rowGroupOptions);
        await rowGroup.WriteAsync(schema.Columns[0], [1, 2]);
        writer.CloseFile();
    }

    [Test]
    public async Task OptionalInt32AllDefinedRoundTrips()
    {
        var path = Path.Combine(Path.GetTempPath(), $"plank-opt-int-all-defined-{Guid.NewGuid():N}.parquet");
        try
        {
            var schema = new ParquetSchema([
                new PlankColumn("A", ParquetPhysicalType.Int32, new ColumnOptions(ParquetRepetition.Optional, []))
            ]);
            var values = new[] { 1, 2, 3, 4, 5 };
            await using (var stream = File.Create(path))
            using (var writer = ParquetWriter.Create(stream, schema))
            {
                var rowGroup = writer.StartRowGroup();
                await rowGroup.WriteAsync(schema.Columns[0], values).ConfigureAwait(false);
                writer.CloseFile();
            }

            using var reader = new ParquetSharp.ParquetFileReader(path);
            using var rg = reader.RowGroup(0);
            using var col = rg.Column(0).LogicalReader<int?>();
            var read = col.ReadAll(values.Length);
            await Assert.That(read.SequenceEqual(values.Select(v => (int?)v))).IsTrue();
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Test]
    public async Task OptionalByteArrayRoundTripsWithNulls()
    {
        var path = Path.Combine(Path.GetTempPath(), $"plank-opt-bytes-{Guid.NewGuid():N}.parquet");
        try
        {
            var schema = new ParquetSchema([
                new PlankColumn("A", ParquetPhysicalType.ByteArray, new ColumnOptions(ParquetRepetition.Optional, []))
            ]);
            byte[]?[] values = [[0x1, 0x2], null, [], [0xFF]];
            await using (var stream = File.Create(path))
            using (var writer = ParquetWriter.Create(stream, schema))
            {
                var rowGroup = writer.StartRowGroup();
                await rowGroup.WriteAsync<byte[]?>(schema.Columns[0], values).ConfigureAwait(false);
                writer.CloseFile();
            }

            using var reader = new ParquetSharp.ParquetFileReader(path);
            using var rg = reader.RowGroup(0);
            using var col = rg.Column(0).LogicalReader<byte[]>();
            var read = col.ReadAll(values.Length);
            await Assert.That(read.Length).IsEqualTo(values.Length);
            for (var i = 0; i < read.Length; i++)
                if (values[i] is null)
                    await Assert.That(read[i]).IsNull();
                else
                    await Assert.That(read[i].SequenceEqual(values[i]!)).IsTrue();
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Test]
    public async Task OptionalDateOnlyIsRejected()
    {
        using var stream = new MemoryStream();
        var schema = new ParquetSchema([
            new PlankColumn("A", ParquetPhysicalType.Int32, new ColumnOptions(ParquetRepetition.Optional, []))
        ]);
        using var writer = ParquetWriter.Create(stream, schema);
        var rowGroup = writer.StartRowGroup();
        var values = new[] { new DateOnly(2026, 2, 1) };

        await Assert.ThrowsAsync<NotSupportedException>(async () =>
            await rowGroup.WriteAsync(schema.Columns[0], values));
    }

    [Test]
    public async Task PreserializedInt64LogicalTypesRegisterAcrossRowGroups()
    {
        var schema = new ParquetSchema([
            new PlankColumn("A", ParquetPhysicalType.Int64, ColumnOptions.Default)
        ]);
        using var stream = new MemoryStream();
        using var writer = ParquetWriter.Create(stream, schema, new ParquetWriterOptions
        {
            ExpectedRowGroupCount = 2,
            RowGroupRowCountHint = 2
        });
        var dtBuffer = new byte[128];
        var timeBuffer = new byte[128];
        var dt = writer.SerializeColumn(schema.Columns[0], new[]
        {
            new DateTime(2026, 2, 1, 10, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 2, 1, 10, 0, 1, DateTimeKind.Utc)
        }, dtBuffer);
        var t = writer.SerializeColumn(schema.Columns[0], new[]
        {
            new TimeOnly(1, 2, 3),
            new TimeOnly(4, 5, 6)
        }, timeBuffer);

        var rg1 = writer.StartRowGroup();
        await rg1.WriteAsync(dt);
        var rg2 = writer.StartRowGroup();
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await rg2.WriteAsync(t));
    }

    [Test]
    public async Task MappingSwitchesCoverAllKnownValuesAndThrowOnUnknown()
    {
        await Task.Run(() =>
        {
            var thrift = typeof(ParquetWriter).Assembly.GetType("Plank.Writing.ParquetThriftWriter")!;
            var getType = thrift.GetMethod("GetType", BindingFlags.NonPublic | BindingFlags.Static)!;
            var getCompression = thrift.GetMethod("GetCompression", BindingFlags.NonPublic | BindingFlags.Static)!;
            var getEncoding = thrift.GetMethod("GetEncoding", BindingFlags.NonPublic | BindingFlags.Static)!;
            var getRepetition = thrift.GetMethod("GetRepetition", BindingFlags.NonPublic | BindingFlags.Static)!;

            foreach (var p in Enum.GetValues<ParquetPhysicalType>())
                _ = (int)getType.Invoke(null, [p])!;
            foreach (var c in Enum.GetValues<CompressionKind>())
                _ = (int)getCompression.Invoke(null, [c])!;
            foreach (var e in Enum.GetValues<EncodingKind>())
                _ = (int)getEncoding.Invoke(null, [e])!;
            foreach (var r in Enum.GetValues<ParquetRepetition>())
                _ = (int)getRepetition.Invoke(null, [r])!;

            AssertReflectionThrows<TargetInvocationException>(() => _ = getType.Invoke(null, [(ParquetPhysicalType)999]));
            AssertReflectionThrows<TargetInvocationException>(() => _ = getCompression.Invoke(null, [(CompressionKind)999]));
            AssertReflectionThrows<TargetInvocationException>(() => _ = getEncoding.Invoke(null, [(EncodingKind)999]));
            AssertReflectionThrows<TargetInvocationException>(() => _ = getRepetition.Invoke(null, [(ParquetRepetition)999]));
        });
    }

    [Test]
    public async Task ParquetWriterPrivateHelpersCoverEnumBranches()
    {
        await Task.Run(() =>
        {
            var writerType = typeof(ParquetWriter);
            var getInt64SerializedState = writerType.GetMethod("GetInt64SerializedState", BindingFlags.NonPublic | BindingFlags.Static)!;
            var resolveSerializedLogicalType = writerType.GetMethod("ResolveSerializedLogicalType", BindingFlags.NonPublic | BindingFlags.Static)!;
            var logicalType = writerType.Assembly.GetType("Plank.Writing.ColumnLogicalType")!;
            var timestampMicrosUtc = Enum.Parse(logicalType, "TimestampMicrosUtc");
            var timeMicros = Enum.Parse(logicalType, "TimeMicros");
            var none = Enum.Parse(logicalType, "None");

            _ = (byte)getInt64SerializedState.Invoke(null, [timestampMicrosUtc])!;
            _ = (byte)getInt64SerializedState.Invoke(null, [timeMicros])!;
            _ = (byte)getInt64SerializedState.Invoke(null, [none])!;

            var int64Column = new PlankColumn("A", ParquetPhysicalType.Int64, ColumnOptions.Default);
            var int32Column = new PlankColumn("B", ParquetPhysicalType.Int32, ColumnOptions.Default);
            var bytesColumn = new PlankColumn("C", ParquetPhysicalType.ByteArray, ColumnOptions.Default);
            var boolColumn = new PlankColumn("D", ParquetPhysicalType.Boolean, ColumnOptions.Default);

            _ = resolveSerializedLogicalType.Invoke(null, [typeof(DateTime), int64Column]);
            _ = resolveSerializedLogicalType.Invoke(null, [typeof(DateTimeOffset), int64Column]);
            _ = resolveSerializedLogicalType.Invoke(null, [typeof(TimeOnly), int64Column]);
            _ = resolveSerializedLogicalType.Invoke(null, [typeof(long), int64Column]);
            _ = resolveSerializedLogicalType.Invoke(null, [typeof(DateOnly), int32Column]);
            _ = resolveSerializedLogicalType.Invoke(null, [typeof(int), int32Column]);
            _ = resolveSerializedLogicalType.Invoke(null, [typeof(string), bytesColumn]);
            _ = resolveSerializedLogicalType.Invoke(null, [typeof(byte[]), bytesColumn]);
            _ = resolveSerializedLogicalType.Invoke(null, [typeof(bool), boolColumn]);
        });
    }

    [Test]
    public async Task LargeSchemaWithLongNamesExercicesCompactWriters()
    {
        var columns = new List<PlankColumn>(20);
        for (var i = 0; i < 20; i++)
            columns.Add(new PlankColumn($"column_{i}_{new string('x', 40)}", ParquetPhysicalType.Int32, ColumnOptions.Default));

        var schema = new ParquetSchema([.. columns]);
        var path = Path.Combine(Path.GetTempPath(), $"plank-large-schema-{Guid.NewGuid():N}.parquet");
        try
        {
            await using (var stream = File.Create(path))
            using (var writer = ParquetWriter.Create(stream, schema))
            {
                var rowGroup = writer.StartRowGroup();
                for (var i = 0; i < schema.Columns.Length; i++)
                    await rowGroup.WriteAsync(schema.Columns[i], [i + 1]);
                writer.CloseFile();
            }

            using var reader = new ParquetSharp.ParquetFileReader(path);
            await Assert.That(reader.FileMetaData.NumColumns).IsEqualTo(20);
            await Assert.That(reader.FileMetaData.NumRows).IsEqualTo(1L);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Test]
    public async Task CompactSizeCounterReflectionCoversEdgeBranches()
    {
        await Task.Run(() =>
        {
            var asm = typeof(ParquetWriter).Assembly;
            var counterType = asm.GetType("Plank.Writing.ParquetThriftWriter+CompactSizeCounter", throwOnError: true)!;
            var compactType = asm.GetType("Plank.Writing.ParquetThriftWriter+CompactType", throwOnError: true)!;
            var i32Type = Enum.Parse(compactType, "I32");
            var boolType = Enum.Parse(compactType, "BooleanTrue");
            object counter = Activator.CreateInstance(counterType)!;

            counterType.GetMethod("WriteVarInt32", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
                .Invoke(counter, [200u]);
            counterType.GetMethod("WriteListHeader", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
                .Invoke(counter, [4, i32Type]);
            counterType.GetMethod("WriteListHeader", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
                .Invoke(counter, [40, i32Type]);
            counterType.GetMethod("WriteFieldHeader", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
                .Invoke(counter, [1, boolType]);
            counterType.GetMethod("WriteFieldHeader", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
                .Invoke(counter, [40, boolType]);
            counterType.GetMethod("WriteFieldBool", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
                .Invoke(counter, [2, true]);
            counterType.GetMethod("WriteI16", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
                .Invoke(counter, [1234]);
        });
    }

    static void AssertReflectionThrows<T>(Action action)
        where T : Exception
    {
        try
        {
            action();
        }
        catch (T)
        {
            return;
        }

        throw new InvalidOperationException($"Expected exception of type {typeof(T).Name}.");
    }
}
#pragma warning restore CA2007
