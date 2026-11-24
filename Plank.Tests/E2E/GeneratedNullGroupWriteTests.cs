using Plank.Schema;

namespace Plank.Tests.E2E;

internal sealed class GeneratedNullGroupWriteTests
{
    // A null required group is rejected by the setter. A null required leaf is rejected by its column encoder, which
    // faults the writer before the footer.
    [Test]
    public async Task GeneratedWriterRejectsNullRequiredGroup()
    {
        using var stream = new MemoryStream();
        using var writer = NullGroupWriteRow.CreateRowWriter(stream, maxParallelism: 1);
        var exception = Assert.Throws<InvalidOperationException>(() => writer.GetRow().Required = null!);
        await Assert.That(exception!.Message).Contains("'Required' cannot be null");
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
