using Plank.Schema;

namespace Plank.Tests.E2E;

internal sealed class GeneratedNullGroupWriteTests
{
    // Null required references are not checked in the setter: a null group throws when the setter reads its
    // fields, and a null required leaf is rejected by the encoder, which faults the writer before the footer.
    [Test]
    public void GeneratedWriterRejectsNullRequiredGroup()
    {
        using var stream = new MemoryStream();
        using var writer = NullGroupWriteRow.CreateRowWriter(stream, maxParallelism: 1);
        Assert.Throws<NullReferenceException>(() => writer.GetRow().Required = null!);
    }

    [Test]
    public async Task GeneratedWriterRejectsNullRequiredLeafInGroup()
    {
        using var stream = new MemoryStream();
        using var writer = NullGroupWriteRow.CreateRowWriter(stream, maxParallelism: 1);
        writer.GetRow().Optional = new RequiredGroupText { Text = null! };
        var exception = Assert.Throws<InvalidOperationException>(writer.Complete);
        await Assert.That(exception!.Message).Contains("does not support null values");
    }
}

[ParquetSchema(AllowAllocatingValues = true)]
internal sealed partial class NullGroupWriteRow
{
    public RequiredGroupText Required { get; set; } = new();
    public RequiredGroupText? Optional { get; set; }
}

internal sealed class RequiredGroupText
{
    public string Text { get; set; } = "";
}
