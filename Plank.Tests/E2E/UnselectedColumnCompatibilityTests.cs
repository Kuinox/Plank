using Plank.Schema;
using Plank.Writing;

namespace Plank.Tests.E2E;

internal sealed class UnselectedColumnCompatibilityTests
{
    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    public void ProjectionIgnoresIncompatibleUnusedColumns(int mismatch)
    {
        var file = CreateFile(mismatch);
        using var stream = new MemoryStream(file);
        using var reader = ProjectionCompatibilitySchema.CreateRowReader(stream,
            ProjectionCompatibilitySchema.Projection.Id);
        AssertIds(reader);

        using var resetStream = new MemoryStream(file);
        reader.Reset(resetStream, ProjectionCompatibilitySchema.Projection.Id);
        AssertIds(reader);
    }

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    public void SelectingIncompatibleColumnStillRejectsIt(int mismatch)
    {
        using var stream = new MemoryStream(CreateFile(mismatch));
        try
        {
            using var reader = ProjectionCompatibilitySchema.CreateRowReader(stream,
                ProjectionCompatibilitySchema.Projection.Unused);
        }
        catch (InvalidOperationException exception) when (
            exception.Message.Contains("Unused", StringComparison.Ordinal))
        {
            return;
        }

        throw new InvalidOperationException("Selecting an incompatible column must fail.");
    }

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    public void ResetValidatesNewlySelectedColumn(int mismatch)
    {
        var file = CreateFile(mismatch);
        using var stream = new MemoryStream(file);
        using var reader = ProjectionCompatibilitySchema.CreateRowReader(stream,
            ProjectionCompatibilitySchema.Projection.Id);
        using var resetStream = new MemoryStream(file);
        try
        {
            reader.Reset(resetStream, ProjectionCompatibilitySchema.Projection.Unused);
        }
        catch (InvalidOperationException exception) when (
            exception.Message.Contains("Unused", StringComparison.Ordinal))
        {
            return;
        }

        throw new InvalidOperationException("Reset must validate the newly selected column.");
    }

    static void AssertIds(ProjectionCompatibilitySchema.RowReader reader)
    {
        for (var id = 1; id <= 2; id++)
        {
            if (!reader.MoveNext() || reader.Current.Id != id)
                throw new InvalidOperationException($"Expected projected Id {id}.");
            try
            {
                _ = reader.Current.Unused;
            }
            catch (InvalidOperationException exception) when (
                exception.Message.Contains("was not selected", StringComparison.Ordinal))
            {
                continue;
            }

            throw new InvalidOperationException("Unselected properties must remain inaccessible.");
        }

        if (reader.MoveNext())
            throw new InvalidOperationException("Expected exactly two rows.");
    }

    static byte[] CreateFile(int mismatch)
    {
        var unused = mismatch switch
        {
            0 => ColumnDefinition.RequiredLeaf("Unused", ParquetPhysicalType.Int32),
            1 => ColumnDefinition.OptionalLeaf("Unused", ParquetPhysicalType.Int64),
            2 => ColumnDefinition.OptionalLeaf("Unused", ParquetPhysicalType.Int32,
                logicalType: new LogicalType.Date()),
            _ => throw new ArgumentOutOfRangeException(nameof(mismatch))
        };
        var schema = new ParquetSchema([
            ColumnDefinition.RequiredLeaf("Id", ParquetPhysicalType.Int32), unused
        ]);
        using var stream = new MemoryStream();
        using var writer = schema.CreateWriter(stream, new ParquetWriterOptions { Compression = CompressionKind.None });
        var group = writer.StartRowGroup();
        var ids = group.CreateSerializedColumn<int>(schema.LeafColumns[0]);
        ids.Serialize([1, 2]);
        group.Write(ids);
        if (mismatch == 1)
        {
            var column = group.CreateSerializedColumn<long?>(schema.LeafColumns[1]);
            column.Serialize([10, 20]);
            group.Write(column);
        }
        else if (mismatch == 2)
        {
            var column = group.CreateSerializedColumn<int?>(schema.LeafColumns[1]);
            column.Serialize([10, 20]);
            group.Write(column);
        }
        else
        {
            var column = group.CreateSerializedColumn<int>(schema.LeafColumns[1]);
            column.Serialize([10, 20]);
            group.Write(column);
        }

        writer.CloseFile();
        return stream.ToArray();
    }
}

[ParquetSchema]
public sealed partial class ProjectionCompatibilitySchema
{
    public int Id { get; init; }
    public int? Unused { get; init; }
}
