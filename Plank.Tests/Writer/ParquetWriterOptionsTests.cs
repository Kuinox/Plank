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
        await Assert.That(options.TargetRowGroupSizeBytes).IsEqualTo(128UL * 1024 * 1024);
        await Assert.That(options.TargetFileSizeBytes).IsEqualTo(512UL * 1024 * 1024);
    }

    [Test]
    public void ZeroSizeTargetsAreRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ParquetWriterOptions
        {
            TargetRowGroupSizeBytes = 0
        }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new ParquetWriterOptions
        {
            TargetFileSizeBytes = 0
        }.Validate());
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

    [Test]
    public void InvalidPerColumnCompressionLevelsAreRejected()
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
            var schema = CreateSchema(new ColumnOptions(compression: compression, compressionLevel: level));
            using var stream = new MemoryStream();

            Assert.Throws<ArgumentOutOfRangeException>(() =>
                schema.CreateWriter(stream, new ParquetWriterOptions()));
        }
    }

    [Test]
    public void UndefinedPerColumnCompressionIsRejected()
    {
        var schema = CreateSchema(new ColumnOptions(compression: (CompressionKind)int.MaxValue));
        using var stream = new MemoryStream();

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            schema.CreateWriter(stream, new ParquetWriterOptions()));
    }

    [Test]
    public void PerColumnLevelIsValidatedAgainstInheritedCompression()
    {
        var schema = CreateSchema(new ColumnOptions(compressionLevel: 10));
        using var stream = new MemoryStream();

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            schema.CreateWriter(stream, new ParquetWriterOptions
            {
                Compression = CompressionKind.Gzip
            }));
    }

    [Test]
    public void ExplicitPerColumnCompressionUsesItsDefaultLevel()
    {
        var schema = CreateSchema(new ColumnOptions(compression: CompressionKind.Gzip));
        using var stream = new MemoryStream();
        var writer = schema.CreateWriter(stream, new ParquetWriterOptions
        {
            Compression = CompressionKind.Brotli,
            CompressionLevel = 11
        });

        writer.CloseFile();
    }

    static ParquetSchema CreateSchema(ColumnOptions options)
        => new([ColumnDefinition.RequiredLeaf("value", ParquetPhysicalType.Int32, options)]);

    [Test]
    public void LegacyLz4IsReadOnly()
    {
        var options = new ParquetWriterOptions { Compression = CompressionKind.Lz4Legacy };

        Assert.Throws<NotSupportedException>(options.Validate);
    }
}
