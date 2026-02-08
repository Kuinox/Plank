using System.Reflection;
using Plank.Schema;
using Plank.Writing;
using PlankColumn = Plank.Schema.Column;

#pragma warning disable CA2007
namespace Plank.Tests;

internal sealed class WriterFailurePathTests
{
    static readonly int[] ManyInts = [.. Enumerable.Range(0, 2048)];
    static readonly int[] HugeInts = [.. Enumerable.Range(0, 1_100_000)];
    static readonly string[] HugeStrings = [.. Enumerable.Range(0, 80_000).Select(i => new string((char)('a' + (i % 26)), 64))];
    static readonly int[][] HugeRepeatedInts = [.. Enumerable.Range(0, 3_000).Select(static i => Enumerable.Range(i, 1_000).ToArray())];

    [Test]
    public async Task EncodedPayloadExceedingInternalBufferForInt32Throws()
    {
        using var stream = new MemoryStream();
        var schema = new ParquetSchema([
            new PlankColumn("A", ParquetPhysicalType.Int32, ColumnOptions.Default)
        ]);
        using var writer = ParquetWriter.Create(stream, schema);
        var rowGroup = writer.StartRowGroup();

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await rowGroup.WriteAsync(schema.Columns[0], HugeInts));
    }

    [Test]
    public async Task EncodedPayloadExceedingInternalBufferForStringThrows()
    {
        using var stream = new MemoryStream();
        var schema = new ParquetSchema([
            new PlankColumn("A", ParquetPhysicalType.ByteArray, ColumnOptions.Default)
        ]);
        using var writer = ParquetWriter.Create(stream, schema);
        var rowGroup = writer.StartRowGroup();

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await rowGroup.WriteAsync(schema.Columns[0], HugeStrings));
    }

    [Test]
    public async Task EncodedPayloadExceedingInternalBufferForRepeatedThrows()
    {
        using var stream = new MemoryStream();
        var schema = new ParquetSchema([
            new PlankColumn("A", ParquetPhysicalType.Int32, new ColumnOptions(ParquetRepetition.Repeated, []))
        ]);
        using var writer = ParquetWriter.Create(stream, schema);
        var rowGroup = writer.StartRowGroup();

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await rowGroup.WriteAsync(schema.Columns[0], HugeRepeatedInts));
    }

    [Test]
    [Arguments(CompressionKind.Brotli)]
    [Arguments(CompressionKind.Gzip)]
    [Arguments(CompressionKind.Snappy)]
    [Arguments(CompressionKind.Lz4)]
    [Arguments(CompressionKind.Zstd)]
    public async Task MaxCompressedBytesTooSmallThrows(CompressionKind compression)
    {
        using var stream = new MemoryStream();
        var schema = new ParquetSchema([
            new PlankColumn("A", ParquetPhysicalType.Int32, ColumnOptions.Default)
        ]);
        using var writer = ParquetWriter.Create(stream, schema, new ParquetWriterOptions
        {
            Compression = compression,
            RowGroupOptions = new RowGroupOptions
            {
                MaxCompressedBytes = 1
            }
        });
        var rowGroup = writer.StartRowGroup();

        if (compression == CompressionKind.Zstd)
        {
            await Assert.ThrowsAsync<Exception>(async () =>
                await rowGroup.WriteAsync(schema.Columns[0], ManyInts));
            return;
        }

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await rowGroup.WriteAsync(schema.Columns[0], ManyInts));
    }

    [Test]
    public async Task NamedMemoryPoolPrivateBranchesCanBeHit()
    {
        await Task.Run(() =>
        {
            var poolType = typeof(NamedMemoryPool);
            var bucketType = poolType.GetNestedType("Bucket", BindingFlags.NonPublic)!;
            var leaseType = poolType.GetNestedType("BufferLease", BindingFlags.NonPublic)!;
            var bucketCtor = bucketType.GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, null, [typeof(int), typeof(int)], null)!;
            var leaseCtor = leaseType.GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, null, [bucketType, typeof(byte[])], null)!;
            var returnMethod = bucketType.GetMethod("Return", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var markRented = leaseType.GetMethod("MarkRented", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var disposeMethod = leaseType.GetMethod("Dispose", BindingFlags.Instance | BindingFlags.Public)!;

            var bucket = bucketCtor.Invoke([32, 0]);
            var shortLease = leaseCtor.Invoke([bucket, new byte[8]]);
            AssertReflectionThrows<TargetInvocationException>(() => returnMethod.Invoke(bucket, [shortLease]));

            var okLease = leaseCtor.Invoke([bucket, new byte[32]]);
            markRented.Invoke(okLease, null);
            AssertReflectionThrows<TargetInvocationException>(() => markRented.Invoke(okLease, null));
            disposeMethod.Invoke(shortLease, null);

            var preload = bucketType.GetMethod("Preload", BindingFlags.Instance | BindingFlags.NonPublic)!;
            AssertReflectionThrows<TargetInvocationException>(() => preload.Invoke(bucket, [64, 1]));
            preload.Invoke(bucket, [32, 0]);
            preload.Invoke(bucket, [32, 4]);
        });
    }

    [Test]
    public async Task AdvancePositionRejectsNegative()
    {
        await Task.Run(() =>
        {
            using var stream = new MemoryStream();
            var schema = new ParquetSchema([
                new PlankColumn("A", ParquetPhysicalType.Int32, ColumnOptions.Default)
            ]);
            using var writer = ParquetWriter.Create(stream, schema);
            var method = typeof(ParquetWriter).GetMethod("AdvancePosition", BindingFlags.Instance | BindingFlags.NonPublic)!;
            AssertReflectionThrows<TargetInvocationException>(() => method.Invoke(writer, [-1]));
        });
    }

    [Test]
    public async Task RequiredInt32RejectsNullableIntPayload()
    {
        using var stream = new MemoryStream();
        var schema = new ParquetSchema([
            new PlankColumn("A", ParquetPhysicalType.Int32, ColumnOptions.Default)
        ]);
        using var writer = ParquetWriter.Create(stream, schema);
        var rowGroup = writer.StartRowGroup();
        int?[] values = [1, 2, null];

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await rowGroup.WriteAsync(schema.Columns[0], values));
    }

    [Test]
    public async Task RequiredInt64RejectsNullableTimeOnlyPayload()
    {
        using var stream = new MemoryStream();
        var schema = new ParquetSchema([
            new PlankColumn("A", ParquetPhysicalType.Int64, ColumnOptions.Default)
        ]);
        using var writer = ParquetWriter.Create(stream, schema);
        var rowGroup = writer.StartRowGroup();
        TimeOnly?[] values = [new TimeOnly(1, 2, 3), null];

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await rowGroup.WriteAsync(schema.Columns[0], values));
    }

    [Test]
    public async Task RequiredColumnsReportUnsupportedNullableValueKinds()
    {
        await AssertRequiredColumnMessage(
            new PlankColumn("B", ParquetPhysicalType.Boolean, ColumnOptions.Default),
            new bool?[] { true, null },
            "expects Boolean values");
        await AssertRequiredColumnMessage(
            new PlankColumn("L", ParquetPhysicalType.Int64, ColumnOptions.Default),
            new long?[] { 1, null },
            "expects Int64 values");
        await AssertRequiredColumnMessage(
            new PlankColumn("F", ParquetPhysicalType.Float, ColumnOptions.Default),
            new float?[] { 1.5f, null },
            "expects Float values");
        await AssertRequiredColumnMessage(
            new PlankColumn("D", ParquetPhysicalType.Double, ColumnOptions.Default),
            new double?[] { 1.5d, null },
            "expects Double values");
    }

    static async Task AssertRequiredColumnMessage<T>(PlankColumn column, T[] values, string expectedSnippet)
    {
        using var stream = new MemoryStream();
        var schema = new ParquetSchema([column]);
        using var writer = ParquetWriter.Create(stream, schema);
        var rowGroup = writer.StartRowGroup();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await rowGroup.WriteAsync(schema.Columns[0], values));

        await Assert.That(ex).IsNotNull();
        await Assert.That(ex!.Message.Contains(expectedSnippet, StringComparison.Ordinal)).IsTrue();
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
