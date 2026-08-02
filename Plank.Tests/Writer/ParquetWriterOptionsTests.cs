using Plank.Schema;
using Plank.Writing;

namespace Plank.Tests.Writer;

internal sealed class ParquetWriterOptionsTests
{
    [Test]
    public async Task FormatVersionDefaultsPreserveCurrentOutput()
    {
        var options = new ParquetWriterOptions();

        await Assert.That(options.FileVersion).IsEqualTo(ParquetFileVersion.V1);
        await Assert.That(options.DataPageVersion).IsEqualTo(ParquetDataPageVersion.V2);
    }

    [Test]
    public void UndefinedFormatVersionsAreRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ParquetWriterOptions
        {
            FileVersion = (ParquetFileVersion)int.MaxValue
        }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new ParquetWriterOptions
        {
            DataPageVersion = (ParquetDataPageVersion)int.MaxValue
        }.Validate());
    }

    [Test]
    public void CompressionLevelsAreValidatedPerCodec()
    {
        (CompressionKind Compression, int Level)[] validLevels =
        [
            (CompressionKind.Gzip, 0),
            (CompressionKind.Gzip, 9),
            (CompressionKind.Zstd, -1),
            (CompressionKind.Zstd, 22),
            (CompressionKind.Lz4, 0),
            (CompressionKind.Lz4, 3),
            (CompressionKind.Lz4, 12),
            (CompressionKind.Brotli, 0),
            (CompressionKind.Brotli, 11)
        ];

        for (var i = 0; i < validLevels.Length; i++)
        {
            var (compression, level) = validLevels[i];
            new ParquetWriterOptions
            {
                Compression = compression,
                CompressionLevel = level
            }.Validate();
        }
    }

    [Test]
    public void InvalidCompressionLevelsAreRejected()
    {
        (CompressionKind Compression, int Level)[] invalidLevels =
        [
            (CompressionKind.None, 0),
            (CompressionKind.Snappy, 0),
            (CompressionKind.Gzip, -1),
            (CompressionKind.Gzip, 10),
            (CompressionKind.Lz4, 1),
            (CompressionKind.Lz4, 2),
            (CompressionKind.Lz4, 13),
            (CompressionKind.Brotli, -1),
            (CompressionKind.Brotli, 12)
        ];

        for (var i = 0; i < invalidLevels.Length; i++)
        {
            var (compression, level) = invalidLevels[i];
            var options = new ParquetWriterOptions
            {
                Compression = compression,
                CompressionLevel = level
            };

            try
            {
                options.Validate();
            }
            catch (ArgumentOutOfRangeException)
            {
                continue;
            }

            throw new InvalidOperationException(
                $"Expected compression level '{level}' to be rejected for '{compression}'.");
        }
    }
}
