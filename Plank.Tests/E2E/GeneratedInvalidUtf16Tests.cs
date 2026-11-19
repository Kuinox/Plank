using System.Text;
using Plank.Schema;

namespace Plank.Tests.E2E;

internal sealed class GeneratedInvalidUtf16Tests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public void FlatAndRepeatedStringWritersRejectUnpairedSurrogates(bool repeated)
    {
        const string invalid = "a\uD800b";
        if (!repeated)
        {
            using var flat = new MemoryStream();
            using var flatWriter = InvalidUtf16FlatRow.CreateRowWriter(flat, maxParallelism: 1);
            flatWriter.GetRow().Text = invalid;
            Assert.Throws<EncoderFallbackException>(flatWriter.Complete);
            return;
        }

        using var nested = new MemoryStream();
        using var nestedWriter = InvalidUtf16NestedRow.CreateRowWriter(nested, maxParallelism: 1);
        Assert.Throws<EncoderFallbackException>(() => nestedWriter.GetRow().Text = [invalid]);
    }
}

[ParquetSchema(AllowAllocatingValues = true)]
internal sealed partial class InvalidUtf16FlatRow
{
    public string Text { get; set; } = "";
}

[ParquetSchema(AllowAllocatingValues = true)]
internal sealed partial class InvalidUtf16NestedRow
{
    public string[] Text { get; set; } = [];
}
