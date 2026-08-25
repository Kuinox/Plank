using Plank.Schema;

namespace Plank.Tests.E2E;

internal sealed class CommonClrRowSchemaTests
{
    [Test]
    public async Task GeneratedSchemaMapsAndRoundTripsStringsAndGuids()
    {
        var path = Path.Combine(Path.GetTempPath(), $"plank-common-clr-{Guid.NewGuid():N}.parquet");
        var firstId = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");
        var secondId = Guid.Parse("ffeeddcc-bbaa-9988-7766-554433221100");

        try
        {
            using (var stream = File.Create(path))
            {
                var writer = CommonClrRowSchema.CreateWriter(stream);
                var rowGroup = writer.StartRowGroup();

                rowGroup.Name.Serialize(["alpha", "héllo"]);
                rowGroup.Write(rowGroup.Name);
                rowGroup.Alias.Serialize(["a", null]);
                rowGroup.Write(rowGroup.Alias);
                rowGroup.Id.Serialize([firstId, secondId]);
                rowGroup.Write(rowGroup.Id);
                rowGroup.ParentId.Serialize([null, firstId]);
                rowGroup.Write(rowGroup.ParentId);
                writer.CloseFile();
            }

            var columns = CommonClrRowSchema.Schema.LeafColumns;
            await Assert.That(columns[0].PhysicalType).IsEqualTo(ParquetPhysicalType.ByteArray);
            await Assert.That(columns[0].LogicalType).IsTypeOf<LogicalType.String>();
            await Assert.That(columns[2].PhysicalType).IsEqualTo(ParquetPhysicalType.FixedLenByteArray);
            await Assert.That(columns[2].Options.TypeLength).IsEqualTo(16U);
            await Assert.That(columns[2].LogicalType).IsTypeOf<LogicalType.Uuid>();

            using var readStream = File.OpenRead(path);
            using var reader = CommonClrRowSchema.CreateRowReader(readStream);
            var rows = new List<(string Name, string? Alias, Guid Id, Guid? ParentId)>();
            while (reader.MoveNext())
            {
                var row = reader.Current;
                rows.Add((row.Name, row.Alias, row.Id, row.ParentId));
            }

            await Assert.That(rows.Count).IsEqualTo(2);
            await Assert.That(rows[0]).IsEqualTo(("alpha", "a", firstId, null));
            await Assert.That(rows[1]).IsEqualTo(("héllo", null, secondId, firstId));
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }
}
