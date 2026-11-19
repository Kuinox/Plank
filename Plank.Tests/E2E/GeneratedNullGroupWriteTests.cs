using Plank.Schema;

namespace Plank.Tests.E2E;

internal sealed class GeneratedNullGroupWriteTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public void GeneratedWriterRejectsNullRequiredGroupReferences(bool nullChild)
    {
        using var stream = new MemoryStream();
        using var writer = NullGroupWriteRow.CreateRowWriter(stream, maxParallelism: 1);
        if (nullChild)
            Assert.Throws<InvalidOperationException>(() => writer.GetRow().Optional = new RequiredGroupText { Text = null! });
        else
            Assert.Throws<InvalidOperationException>(() => writer.GetRow().Required = null!);
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
