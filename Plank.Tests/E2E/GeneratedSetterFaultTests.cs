using System.Text;
using Plank.Schema;

namespace Plank.Tests.E2E;

// A generated setter that throws part way through a row leaves a partially assigned row behind. The writer must
// not write it: every later call throws and no Parquet footer is ever written.
internal sealed class GeneratedSetterFaultTests
{
    [Test]
    public async Task ThrowingGroupGetterFaultsThePipelineWriter()
    {
        using var stream = new MemoryStream();
        using var writer = FaultingSetterRow.CreateRowWriter(stream, maxParallelism: 1);
        writer.GetRow().Group = new FaultingSetterGroup { First = 11, Second = 12 };

        var thrown = Assert.Throws<ArgumentOutOfRangeException>(() =>
            writer.GetRow().Group = new FaultingSetterGroup { First = 21, Second = -1 });

        await AssertFaulted(writer, stream, thrown);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task InvalidListElementFaultsThePipelineWriter(bool invalidUtf16)
    {
        using var stream = new MemoryStream();
        using var writer = FaultingListSetterRow.CreateRowWriter(stream, maxParallelism: 1);
        writer.GetRow().Items = [new FaultingSetterGroup { First = 1, Text = "original" }];
        var invalid = new FaultingSetterGroup { First = 2, Text = invalidUtf16 ? "\uD800" : null! };

        Exception thrown = invalidUtf16
            ? Assert.Throws<EncoderFallbackException>(() => writer.GetRow().Items = [invalid])
            : Assert.Throws<InvalidOperationException>(() => writer.GetRow().Items = [invalid]);

        await AssertFaulted(writer, stream, thrown);
    }

    [Test]
    public async Task ThrowingCursorAssignmentFaultsThePipelineWriter()
    {
        using var stream = new MemoryStream();
        using var writer = FaultingSetterRow.CreateRowWriter(stream, maxParallelism: 1);
        var (thrown, rethrown) = AssignThroughCursor(writer);

        await Assert.That(thrown).IsTypeOf<ArgumentOutOfRangeException>();
        await AssertInterrupted(rethrown);
        await AssertFaulted(writer, stream, thrown!);
    }

    static (Exception? Thrown, Exception? Rethrown) AssignThroughCursor(FaultingSetterRow.PipelineWriter writer)
    {
        var cursor = writer.CreateCursor();
        cursor.NextRow();
        cursor.Group = new FaultingSetterGroup { First = 11, Second = 12 };
        cursor.NextRow();
        Exception? thrown = null;
        try
        {
            cursor.Group = new FaultingSetterGroup { First = 21, Second = -1 };
        }
        catch (Exception ex)
        {
            thrown = ex;
        }
        Exception? rethrown = null;
        try
        {
            cursor.NextRow();
        }
        catch (Exception ex)
        {
            rethrown = ex;
        }
        return (thrown, rethrown);
    }

    [Test]
    public async Task ThrowingGroupGetterFaultsTheRowGroupWriter()
    {
        using var stream = new MemoryStream();
        using var fileWriter = FaultingSetterRow.Schema.CreateWriter(stream);
        var writer = FaultingSetterRow.CreateRowWriter(fileWriter.StartRowGroup());
        writer.GetRow().Group = new FaultingSetterGroup { First = 11, Second = 12 };

        var thrown = Assert.Throws<ArgumentOutOfRangeException>(() =>
            writer.GetRow().Group = new FaultingSetterGroup { First = 21, Second = -1 });

        await Assert.That(thrown).IsTypeOf<ArgumentOutOfRangeException>();
        await AssertInterrupted(Capture(() => writer.GetRow()));
        await AssertInterrupted(Capture(writer.Write));
    }

    static async Task AssertFaulted(FaultingSetterRow.PipelineWriter writer, MemoryStream stream, Exception thrown)
    {
        await Assert.That(thrown).IsNotNull();
        var fault = Capture(() => writer.GetRow());
        await AssertInterrupted(fault);
        // The writer stays faulted with the same failure, and Complete does not finish the file.
        await Assert.That(Capture(() => writer.GetRow())).IsSameReferenceAs(fault);
        await Assert.That(Capture(writer.Complete)).IsSameReferenceAs(fault);
        await AssertNoFooter(stream);
    }

    static async Task AssertFaulted(FaultingListSetterRow.PipelineWriter writer, MemoryStream stream, Exception thrown)
    {
        await Assert.That(thrown).IsNotNull();
        var fault = Capture(() => writer.GetRow());
        await AssertInterrupted(fault);
        // The writer stays faulted with the same failure, and Complete does not finish the file.
        await Assert.That(Capture(() => writer.GetRow())).IsSameReferenceAs(fault);
        await Assert.That(Capture(writer.Complete)).IsSameReferenceAs(fault);
        await AssertNoFooter(stream);
    }

    static async Task AssertInterrupted(Exception? exception)
    {
        await Assert.That(exception).IsTypeOf<InvalidOperationException>();
        await Assert.That(exception!.Message).Contains("threw part way through a row");
    }

    static Exception? Capture(Action action)
    {
        try
        {
            action();
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    static async Task AssertNoFooter(MemoryStream stream)
    {
        var bytes = stream.ToArray();
        var endsWithMagic = bytes.Length >= 8 && bytes.AsSpan(bytes.Length - 4).SequenceEqual("PAR1"u8);
        await Assert.That(endsWithMagic).IsFalse();
    }
}

[ParquetSchema(AllowAllocatingValues = true)]
internal sealed partial class FaultingSetterRow
{
    public FaultingSetterGroup Group { get; set; } = new();
}

[ParquetSchema(AllowAllocatingValues = true)]
internal sealed partial class FaultingListSetterRow
{
    public FaultingSetterGroup[] Items { get; set; } = [];
}

internal sealed class FaultingSetterGroup
{
    public int First { get; set; }
    public int Second
    {
        get => field >= 0 ? field : throw new ArgumentOutOfRangeException(nameof(Second));
        set;
    }
    public string Text { get; set; } = "";
}
