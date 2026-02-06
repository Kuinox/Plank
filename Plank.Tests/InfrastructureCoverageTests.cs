using System.Collections.Immutable;
using System.Linq.Expressions;
using Microsoft.Extensions.Logging;
using Plank.Schema;
using Plank.Writing;
using PlankColumn = Plank.Schema.Column;

#pragma warning disable CA2007
namespace Plank.Tests;

internal sealed class InfrastructureCoverageTests
{
    [Test]
    public async Task ParquetLogNoneNoOpsAreCallable()
    {
        ParquetLog.None.RowGroupMetadataCapacityGrown(1, 2, 3);
        ParquetLog.None.RowGroupMetadataCapacityGrown(1, 2, null);
        ParquetLog.None.FooterBufferCapacityGrown(10, 20, 30);
        await Assert.That(ParquetLog.None).IsNotNull();
    }

    [Test]
    public async Task LoggerParquetLogThrowsOnNullLogger()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await Task.Run(() => _ = new LoggerParquetLog(logger: null!)));
    }

    [Test]
    public async Task LoggerParquetLogEmitsMessagesForBothMetadataModes()
    {
        var logger = new CapturingLogger();
        var log = new LoggerParquetLog(logger);

        log.RowGroupMetadataCapacityGrown(1, 2, 4);
        log.RowGroupMetadataCapacityGrown(2, 3, null);
        log.FooterBufferCapacityGrown(128, 256, 300);

        await Assert.That(logger.Entries.Count).IsEqualTo(3);
        await Assert.That(logger.Entries[0].Message.Contains("expected row group count", StringComparison.Ordinal)).IsTrue();
        await Assert.That(logger.Entries[1].Message.Contains("no row group count was specified", StringComparison.Ordinal)).IsTrue();
        await Assert.That(logger.Entries[2].Message.Contains("Footer buffer capacity grew", StringComparison.Ordinal)).IsTrue();
    }

    [Test]
    public async Task NamedMemoryPoolValidatesRegistrationArguments()
    {
        var pool = new NamedMemoryPool();
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await Task.Run(() => pool.Register("", 64, 1)));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await Task.Run(() => pool.Register("x", 0, 1)));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await Task.Run(() => pool.Register("x", 64, -1)));
    }

    [Test]
    public async Task NamedMemoryPoolValidatesRentArguments()
    {
        var pool = new NamedMemoryPool();
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await Task.Run(() => pool.Rent("", 1)));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await Task.Run(() => pool.Rent("x", 0)));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await Task.Run(() => pool.Rent("x", 1)));
    }

    [Test]
    public async Task NamedMemoryPoolRejectsGrowingRegisteredBucketLength()
    {
        var pool = new NamedMemoryPool();
        pool.Register("bucket", 64, 1);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await Task.Run(() => pool.Register("bucket", 128, 1)));
    }

    [Test]
    public async Task NamedMemoryPoolRejectsRentPastBucketCapacity()
    {
        var pool = new NamedMemoryPool();
        pool.Register("bucket", 64, 0);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await Task.Run(() => pool.Rent("bucket", 65)));
    }

    [Test]
    public async Task NamedMemoryPoolCanDropExtraReturnedLeaseWhenFull()
    {
        var pool = new NamedMemoryPool();
        pool.Register("bucket", 64, 1);

        var a = pool.Rent("bucket", 32);
        var b = pool.Rent("bucket", 32);
        a.Dispose();
        b.Dispose();

        using var c = pool.Rent("bucket", 32);
        await Assert.That(c.Memory.Length).IsEqualTo(64);
    }

    [Test]
    public async Task NamedMemoryPoolLeaseMemoryThrowsAfterDispose()
    {
        var pool = new NamedMemoryPool();
        pool.Register("bucket", 32, 1);
        var lease = pool.Rent("bucket", 8);
        lease.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
            await Task.Run(() => _ = lease.Memory.Span.Length));
    }

    [Test]
    public async Task RowSchemaAndRowColumnDefinitionStoreProvidedValues()
    {
        _ = new TestRow();
        Expression<Func<TestRow, int>> expr = x => x.A;
        var col = new RowColumnDefinition<TestRow>(expr, ParquetPhysicalType.Int32, ColumnOptions.Default)
        {
            Name = "A"
        };
        var schema = new RowSchema<TestRow>(ImmutableArray.Create(col));

        await Assert.That(schema.Columns.Length).IsEqualTo(1);
        await Assert.That(schema.Columns[0].Name).IsEqualTo("A");
        await Assert.That(schema.Columns[0].PhysicalType).IsEqualTo(ParquetPhysicalType.Int32);
    }

    [Test]
    public async Task SerializedColumnEqualityAndHashCodeWork()
    {
        var schema = new ParquetSchema([
            new PlankColumn("A", ParquetPhysicalType.Int32, ColumnOptions.Default)
        ]);
        using var stream = new MemoryStream();
        using var writer = ParquetWriter.Create(stream, schema, new ParquetWriterOptions
        {
            Compression = CompressionKind.None
        });
        var values = new[] { 1, 2, 3 };
        var sharedBuffer = new byte[128];
        var cBuffer = new byte[128];

        var a = writer.SerializeColumn(schema.Columns[0], values, sharedBuffer);
        var b = writer.SerializeColumn(schema.Columns[0], values, sharedBuffer);
        var c = writer.SerializeColumn(schema.Columns[0], [4, 5, 6], cBuffer);

        await Assert.That(a == b).IsTrue();
        await Assert.That(a != b).IsFalse();
        await Assert.That(a.Equals((object)b)).IsTrue();
        await Assert.That(a.GetHashCode()).IsEqualTo(b.GetHashCode());
        await Assert.That(a == c).IsFalse();
    }

    sealed class CapturingLogger : ILogger
    {
        public readonly List<(LogLevel Level, string Message)> Entries = [];

        public IDisposable BeginScope<TState>(TState state)
            where TState : notnull
            => NoopDisposable.Instance;

        public bool IsEnabled(LogLevel logLevel)
            => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }

    sealed class NoopDisposable : IDisposable
    {
        public static readonly NoopDisposable Instance = new();

        public void Dispose()
        {
        }
    }

    sealed class TestRow
    {
        public int A { get; set; }
    }
}
#pragma warning restore CA2007
