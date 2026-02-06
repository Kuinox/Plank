using System.Reflection;
using Plank.Schema;
using Plank.Writing;
using PlankColumn = Plank.Schema.Column;

#pragma warning disable CA2007
namespace Plank.Tests;

internal sealed class FailurePathCoverageTests
{
    static readonly int[] ManyInts = [.. Enumerable.Range(0, 2048)];
    static readonly string[] ManyStrings = [.. Enumerable.Range(0, 256).Select(i => new string((char)('a' + (i % 26)), (i % 37) + 5))];
    static readonly int[][] ManyRepeatedInts = [.. Enumerable.Range(0, 128).Select(i => Enumerable.Range(0, (i % 8) + 1).ToArray())];

    [Test]
    public async Task MaxEncodedBytesTooSmallForInt32Throws()
    {
        using var stream = new MemoryStream();
        var schema = new ParquetSchema([
            new PlankColumn("A", ParquetPhysicalType.Int32, ColumnOptions.Default)
        ]);
        using var writer = ParquetWriter.Create(stream, schema, new ParquetWriterOptions
        {
            RowGroupOptions = new RowGroupOptions
            {
                MaxEncodedBytes = 8,
                MaxCompressedBytes = 1024
            }
        });
        var rowGroup = writer.StartRowGroup();

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await rowGroup.WriteAsync(schema.Columns[0], ManyInts));
    }

    [Test]
    public async Task MaxEncodedBytesTooSmallForStringThrows()
    {
        using var stream = new MemoryStream();
        var schema = new ParquetSchema([
            new PlankColumn("A", ParquetPhysicalType.ByteArray, ColumnOptions.Default)
        ]);
        using var writer = ParquetWriter.Create(stream, schema, new ParquetWriterOptions
        {
            RowGroupOptions = new RowGroupOptions
            {
                MaxEncodedBytes = 32,
                MaxCompressedBytes = 1024
            }
        });
        var rowGroup = writer.StartRowGroup();

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await rowGroup.WriteAsync(schema.Columns[0], ManyStrings));
    }

    [Test]
    public async Task MaxEncodedBytesTooSmallForRepeatedThrows()
    {
        using var stream = new MemoryStream();
        var schema = new ParquetSchema([
            new PlankColumn("A", ParquetPhysicalType.Int32, new ColumnOptions(ParquetRepetition.Repeated, []))
        ]);
        using var writer = ParquetWriter.Create(stream, schema, new ParquetWriterOptions
        {
            RowGroupOptions = new RowGroupOptions
            {
                MaxEncodedBytes = 32,
                MaxCompressedBytes = 1024
            }
        });
        var rowGroup = writer.StartRowGroup();

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await rowGroup.WriteAsync(schema.Columns[0], new RepeatedValues<int>(ManyRepeatedInts)));
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
                MaxEncodedBytes = 4 * 1024 * 1024,
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
