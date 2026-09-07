using Plank.Reading;
using Plank.Reading.Physical;
using Plank.Tests.Reading.ParquetTesting;

namespace Plank.Tests.Reading;

internal sealed class GeospatialStatisticsTests
{
    [Test]
    public void UpstreamBoundsAndTypesAreExposedAndSurviveDispose()
    {
        ParquetGeospatialStatistics stats;
        using (var source = new MemoryReadSource(ParquetTestingCorpus.ReadAllBytes("data/geospatial/geospatial.parquet")))
        using (var reader = new ParquetFileReader())
        {
            reader.Reset(source);
            stats = reader.Metadata.ReadGeospatialStatistics(0, 2)!;
            if (reader.Metadata.ReadGeospatialStatistics(0, 0) is not null)
                throw new InvalidOperationException("WKT column should have no geospatial statistics.");
        }
        var box = stats.BoundingBox!;
        if (box.XMin != 10 || box.XMax != 40 || box.YMin != 10 || box.YMax != 40 ||
            box.ZMin != 30 || box.ZMax != 80 || box.MMin != 200 || box.MMax != 1600 ||
            stats.GeospatialTypes?.Count != 28 || !stats.GeospatialTypes.Contains(3007))
            throw new InvalidOperationException($"Bounds/types differ: {box.XMin},{box.XMax},{box.YMin},{box.YMax},{box.ZMin},{box.ZMax},{box.MMin},{box.MMax}; types {string.Join(",", stats.GeospatialTypes!)}");
    }

    [Test]
    public void AllUpstreamGeospatialStatisticsCanBeReadThroughBothApis()
    {
        var found = 0;
        var wraparound = false;
        foreach (var path in Directory.GetFiles(ParquetTestingCorpus.Resolve("data/geospatial"), "*.parquet"))
        {
            using var source = new MemoryReadSource(File.ReadAllBytes(path));
            using var reader = new Plank.Reading.Logical.ParquetReader();
            reader.Reset(source);
            foreach (var group in reader.RowGroups)
                for (var column = 0; column < reader.Schema.LeafColumns.Length; column++)
                {
                    var stats = group.GetColumnMetadata(column).ReadGeospatialStatistics();
                    var physical = reader.PhysicalReader.Metadata.ReadGeospatialStatistics(group.Index, column);
                    if ((stats is null) != (physical is null) || stats?.BoundingBox?.XMin != physical?.BoundingBox?.XMin)
                        throw new InvalidOperationException("Logical and physical metadata disagree.");
                    if (stats is not null) found++;
                    if (stats?.BoundingBox is { } box && box.XMin > box.XMax) wraparound = true;
                }
        }
        if (found == 0 || !wraparound)
            throw new InvalidOperationException("Expected geospatial bounds, including antimeridian wraparound.");
    }

    [Test]
    public void OptionalAndUnknownFieldsArePreserved()
    {
        var absent = ParquetGeospatialStatistics.Read([0]);
        var empty = ParquetGeospatialStatistics.Read([0x29, 0x05, 0]);
        var future = ParquetGeospatialStatistics.Read([0x39, 0x15, 84, 0]);
        if (absent.BoundingBox is not null || absent.GeospatialTypes is not null ||
            empty.GeospatialTypes?.Count != 0 || future.GeospatialTypes is not null)
            throw new InvalidOperationException("Optional/unknown fields lost their meaning.");
    }

    [Test]
    public void OptionalDimensionsAndNanArePreserved()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((byte)0x1c);
        foreach (var value in new[] { 170d, -170d, -10d, 10d, double.NaN })
        {
            writer.Write((byte)0x17);
            writer.Write(value);
        }
        writer.Write((byte)0);
        writer.Write((byte)0);
        var box = ParquetGeospatialStatistics.Read(stream.ToArray()).BoundingBox!;
        if (box.XMin != 170 || box.XMax != -170 || !double.IsNaN(box.ZMin!.Value) ||
            box.ZMax is not null || box.MMin is not null || box.MMax is not null)
            throw new InvalidOperationException("Optional dimensions, NaN or wraparound bounds were altered.");
    }

    [Test]
    public void MalformedStatisticsAreRejected()
    {
        Assert.Throws<CorruptParquetException>(() => ParquetGeospatialStatistics.Read([0x1c, 0, 0]));
        Assert.Throws<CorruptParquetException>(() => ParquetGeospatialStatistics.Read([0x29, 0x16, 0, 0]));
        Assert.Throws<CorruptParquetException>(() => ParquetGeospatialStatistics.Read([0x29, 0xf5, 0xff, 0xff, 0xff, 0xff, 0x07, 0]));
        Assert.Throws<CorruptParquetException>(() => ParquetGeospatialStatistics.Read([0x1c, 0x17, 0]));
    }
}
