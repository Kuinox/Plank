using Plank.Reading;
using Plank.Reading.Logical;
using Plank.Schema;
using Plank.Writing;

namespace Plank.Tests.Reading;

internal sealed class ReaderResetInvalidationTests
{
    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task FailedResetInvalidatesLogicalHandlesAndCanRecover(bool useSource, bool corruptFile)
    {
        var schema = CreateSchema("Value");
        using var initial = new MemoryStream(CreateFile("Value", 42));
        using var reader = schema.CreateReader(initial);
        var groups = reader.RowGroups;
        var groupEnumerator = groups.GetEnumerator();
        var group = groups[0];
        var column = group.Column<int>(0);
        using var flatEnumerator = column.GetEnumerator();
        using var nestedEnumerator = group.NestedColumn<int>(0).GetEnumerator();
        var rejectedBytes = corruptFile ? new byte[] { 0, 1, 2, 3 } : CreateFile("Other", 99);
        using var rejectedStream = new MemoryStream(rejectedBytes);
        using var rejectedSource = new MemoryReadSource(rejectedBytes);

        void Reset()
        {
            if (useSource)
                reader.Reset(rejectedSource);
            else
                reader.Reset(rejectedStream);
        }

        if (corruptFile)
            AssertThrows<CorruptParquetException>(Reset);
        else
            AssertThrows<InvalidOperationException>(Reset);

        await Assert.That(reader.RowGroups.Count).IsEqualTo(0);
        await Assert.That(reader.Schema.LeafColumns.Length).IsEqualTo(0);
        await Assert.That(reader.Metadata.Version).IsEqualTo(0);
        AssertThrows<InvalidOperationException>(() => _ = groups.Count);
        AssertThrows<InvalidOperationException>(() => groupEnumerator.MoveNext());
        AssertThrows<ArgumentException>(() => group.Column<int>(0));
        AssertThrows<ArgumentException>(() => column.GetEnumerator().Dispose());
        AssertThrows<InvalidOperationException>(() => flatEnumerator.MoveNext());
        AssertThrows<InvalidOperationException>(() => nestedEnumerator.MoveNext());

        using var recovery = new MemoryStream(CreateFile("Value", 7));
        reader.Reset(recovery);
        var values = new List<int>();
        foreach (var buffer in reader.RowGroups[0].Column<int>(0))
            values.AddRange(buffer.Values.ToArray());
        await Assert.That(values.ToArray()).IsEquivalentTo([7]);
        AssertThrows<ArgumentException>(() => group.Column<int>(0));
        AssertThrows<InvalidOperationException>(() => flatEnumerator.MoveNext());
        AssertThrows<InvalidOperationException>(() => nestedEnumerator.MoveNext());
    }

    [Test]
    public async Task StreamValidationFailureInvalidatesLogicalStateBeforePhysicalReset()
    {
        using var initial = new MemoryStream(CreateFile("Value", 42));
        using var reader = CreateSchema("Value").CreateReader(initial);
        var groups = reader.RowGroups;
        var group = groups[0];
        using var rejected = new NonSeekableStream(CreateFile("Value", 99));

        AssertThrows<InvalidOperationException>(() => reader.Reset(rejected));

        await Assert.That(reader.RowGroups.Count).IsEqualTo(0);
        await Assert.That(reader.Schema.LeafColumns.Length).IsEqualTo(0);
        await Assert.That(reader.Metadata.Version).IsEqualTo(0);
        AssertThrows<InvalidOperationException>(() => _ = groups.Count);
        AssertThrows<ArgumentException>(() => group.Column<int>(0));
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public void DeferredBinaryEnumeratorsRejectReset(bool nested)
    {
        var schema = new ParquetSchema([ColumnDefinition.RequiredLeaf("Value", ParquetPhysicalType.ByteArray)]);
        using var output = new MemoryStream();
        var writer = schema.CreateWriter(output, new ParquetWriterOptions { Compression = CompressionKind.None });
        var serialized = writer.CreateSerializedColumn<byte[]>(schema.LeafColumns[0]);
        serialized.Serialize([[42]]);
        writer.StartRowGroup().Write(serialized);
        writer.CloseFile();
        using var initial = new MemoryStream(output.ToArray());
        using var reader = schema.CreateReader(initial);
        var group = reader.RowGroups[0];
        using var flatEnumerator = group.Column<byte>(0).GetEnumerator();
        using var nestedEnumerator = group.NestedColumn<byte>(0).GetEnumerator();
        using var rejected = new MemoryStream(CreateFile("Other", 99));

        AssertThrows<InvalidOperationException>(() => reader.Reset(rejected));

        if (nested)
            AssertThrows<InvalidOperationException>(() => nestedEnumerator.MoveNext());
        else
            AssertThrows<InvalidOperationException>(() => flatEnumerator.MoveNext());
    }

    static void AssertThrows<TException>(Action action) where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }
        throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
    }

    static ParquetSchema CreateSchema(string columnName)
        => new([ColumnDefinition.RequiredLeaf(columnName, ParquetPhysicalType.Int32)]);

    static byte[] CreateFile(string columnName, int value)
    {
        var schema = CreateSchema(columnName);
        using var output = new MemoryStream();
        var writer = schema.CreateWriter(output, new ParquetWriterOptions { Compression = CompressionKind.None });
        var column = writer.CreateSerializedColumn<int>(schema.LeafColumns[0]);
        column.Serialize([value]);
        writer.StartRowGroup().Write(column);
        writer.CloseFile();
        return output.ToArray();
    }

    sealed class NonSeekableStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }
}
