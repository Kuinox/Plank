using System.Text;
using Plank.Schema;

namespace Plank.Tests.E2E;

internal sealed class GeneratedInvalidUtf16Tests
{
    [Test]
    [Arguments(false, 1)]
    [Arguments(false, 300)]
    [Arguments(true, 1)]
    public void FlatAndRepeatedStringWritersRejectUnpairedSurrogates(bool repeated, int prefixLength)
    {
        var invalid = new string('a', prefixLength) + "\uD800b";
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

    [Test]
    public async Task FlatStringsRoundTripWithAsciiPrefixesAndUnicodeSuffixes()
    {
        string[] values = ["", new string('a', 64), "aé東京🌸", new string('x', 300) + "🌸"];
        using var stream = new MemoryStream();
        using (var writer = InvalidUtf16FlatRow.CreateRowWriter(stream, maxParallelism: 1))
        {
            foreach (var value in values)
                writer.GetRow().Text = value;
            writer.Complete();
        }

        using var reader = InvalidUtf16FlatRow.CreateRowReader(new MemoryStream(stream.ToArray()));
        foreach (var value in values)
        {
            await Assert.That(reader.MoveNext()).IsTrue();
            await Assert.That(reader.Current.Text).IsEqualTo(value);
        }
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
