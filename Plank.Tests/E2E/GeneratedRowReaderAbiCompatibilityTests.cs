using System.Runtime.CompilerServices;
using Plank.RowApi;
using Plank.Schema;
using Plank.Writing;

namespace Plank.Tests.E2E;

internal sealed class GeneratedRowReaderAbiCompatibilityTests
{
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "GetCurrentByOrdinalV1")]
    static extern ref T GetCurrentByOrdinalV1<T>(RowReaderCore core, int columnIndex);

    [Test]
    public async Task VersionOneOrdinalAccessorRemainsCallable()
    {
        var schema = new ParquetSchema([
            ColumnDefinition.Leaf("value", ParquetPhysicalType.Int32)
        ]);
        byte[] file;
        using (var output = new MemoryStream())
        {
            using var writer = schema.CreateWriter(output);
            var rowGroup = writer.StartRowGroup();
            var column = rowGroup.CreateSerializedColumn<int>(schema.LeafColumns[0]);
            column.Serialize([42]);
            rowGroup.Write(column);
            writer.CloseFile();
            file = output.ToArray();
        }

        var descriptor = new RowApiColumnDescriptor<int>("Value", schema.LeafColumns[0]);
        using var input = new MemoryStream(file);
        using var reader = new RowReaderCore(input, schema, [descriptor], projection: null,
            RowReaderOptions.Default, schemaEvolution: null);

        await Assert.That(reader.MoveNext()).IsTrue();
        var actual = GetCurrentByOrdinalV1<int>(reader, 0);
        await Assert.That(actual).IsEqualTo(42);
    }
}
