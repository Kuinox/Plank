using Plank.Schema;
using Plank.Writing;

namespace Plank.Tests.E2E;

internal sealed class InheritedSchemaPropertiesTests
{
    [Test]
    public async Task DatasetPreservesInheritedPropertiesAndUsesMostDerivedMembers()
    {
        using var file = new Destination();
        using (var writer = InheritedScalarRow.CreateDatasetWriter(Route, FilePath, [file]))
            writer.Queue(new InheritedScalarRow { Id = 123456789, Version = 7, Value = 42L });

        await Assert.That(InheritedScalarRow.Schema.LeafColumns.Select(column => column.Path).Order()
            .SequenceEqual(new[] { "Value", "Version", "row_id" }.Order())).IsTrue();
        using var stream = new MemoryStream(file.Stream.ToArray());
        using var reader = ExpectedInheritedScalarRow.CreateRowReader(stream);
        await Assert.That(reader.MoveNext()).IsTrue();
        await Assert.That(reader.Current.Id).IsEqualTo(123456789);
        await Assert.That(reader.Current.Version).IsEqualTo(7);
        await Assert.That(reader.Current.Value).IsEqualTo(42L);
        await Assert.That(reader.MoveNext()).IsFalse();
    }

    [Test]
    public async Task InheritedNestedPropertyAndGroupMembersSurviveDatasetOutput()
    {
        using var file = new Destination();
        using (var writer = InheritedNestedRow.CreateDatasetWriter(NestedRoute, FilePath, [file]))
            writer.Queue(new InheritedNestedRow
            {
                Value = 42,
                Details = new InheritedDetails { Id = 123456789, Version = 7, Value = 99L }
            });

        await Assert.That(InheritedNestedRow.Schema.LeafColumns.Select(column => column.Path).Order()
            .SequenceEqual(new[] { "Value", "Details.row_id", "Details.Version", "Details.Value" }.Order())).IsTrue();
        using var stream = new MemoryStream(file.Stream.ToArray());
        using var reader = ExpectedInheritedNestedRow.CreateRowReader(stream);
        await Assert.That(reader.MoveNext()).IsTrue();
        await Assert.That(reader.Current.Value).IsEqualTo(42);
        await Assert.That(reader.Current.Details.Id).IsEqualTo(123456789);
        await Assert.That(reader.Current.Details.Version).IsEqualTo(7);
        await Assert.That(reader.Current.Details.Value).IsEqualTo(99L);
        await Assert.That(reader.MoveNext()).IsFalse();
    }

    static ReadOnlySpan<byte> Route(InheritedScalarRow row, IParquetBufferPool pool, out ParquetBuffer? owner)
    {
        owner = null;
        return "partition"u8;
    }

    static ReadOnlySpan<byte> NestedRoute(InheritedNestedRow row, IParquetBufferPool pool, out ParquetBuffer? owner)
    {
        owner = null;
        return "partition"u8;
    }

    static ReadOnlySpan<byte> FilePath(ReadOnlySpan<byte> key, ulong index, IParquetBufferPool pool,
        out ParquetBuffer? owner)
    {
        owner = null;
        return "part.parquet"u8;
    }

    sealed class Destination : IParquetWriteSource, IDisposable
    {
        internal MemoryStream Stream { get; } = new();
        public void Open(ReadOnlySpan<byte> path, FileMode mode) { }
        public void Close() { }
        public void Flush() { }
        public void SetLength(ulong length) => Stream.SetLength(checked((long)length));
        public void Write(ulong offset, ReadOnlySpan<byte> source)
        {
            Stream.Position = checked((long)offset);
            Stream.Write(source);
        }
        public void Dispose() => Stream.Dispose();
    }
}

internal class InheritedPropertiesBase
{
    [ParquetColumn("row_id")]
    public int Id { get; set; }
    public virtual int Version { get; set; }
    public int Value { get; set; }
    protected int ProtectedValue { get; set; }
}

[ParquetSchema]
internal sealed partial class InheritedScalarRow : InheritedPropertiesBase
{
    public override int Version { get; set; }
    public new long Value { get; set; }
}

internal sealed class InheritedDetails : InheritedPropertiesBase
{
    public override int Version { get; set; }
    public new long Value { get; set; }
}

internal class InheritedNestedBase
{
    public InheritedDetails Details { get; set; } = new();
}

[ParquetSchema(AllowAllocatingValues = true)]
internal sealed partial class InheritedNestedRow : InheritedNestedBase
{
    public int Value { get; set; }
}

// Independent, flat declarations let the output checks run even if inheritance is omitted.
[ParquetSchema]
internal sealed partial class ExpectedInheritedScalarRow
{
    [ParquetColumn("row_id")]
    public int Id { get; set; }
    public int Version { get; set; }
    public long Value { get; set; }
}

[ParquetSchema(AllowAllocatingValues = true)]
internal sealed partial class ExpectedInheritedNestedRow
{
    public int Value { get; set; }
    public ExpectedInheritedDetails Details { get; set; } = new();
}

internal sealed class ExpectedInheritedDetails
{
    [ParquetColumn("row_id")]
    public int Id { get; set; }
    public int Version { get; set; }
    public long Value { get; set; }
}
