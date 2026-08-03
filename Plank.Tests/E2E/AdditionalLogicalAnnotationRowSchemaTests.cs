using Plank.Schema;

namespace Plank.Tests.E2E;

internal sealed class AdditionalLogicalAnnotationRowSchemaTests
{
    [Test]
    public void GeneratedSchemaSupportsMarkerAnnotationsAndFixedLengths()
    {
        var columns = AdditionalLogicalAnnotationRowSchema.Schema.LeafColumns;
        if (columns[0].LogicalType is not LogicalType.Enum ||
            columns[1].LogicalType is not LogicalType.Bson ||
            columns[2].LogicalType is not LogicalType.Float16 || columns[2].Options.TypeLength != 2 ||
            columns[3].LogicalType is not LogicalType.Interval || columns[3].Options.TypeLength != 12 ||
            columns[4].LogicalType is not LogicalType.Geometry { Crs: null } ||
            columns[5].LogicalType is not LogicalType.Geography { Crs: null, Algorithm: null } ||
            columns[6].LogicalType is not LogicalType.Unknown ||
            columns[6].Options.Repetition != ParquetRepetition.Optional)
            throw new InvalidOperationException("Generated schema did not retain additional logical annotations.");

        using var stream = new MemoryStream();
        var writer = AdditionalLogicalAnnotationRowSchema.CreateWriter(stream);
        var rowGroup = writer.StartRowGroup();
        rowGroup.EnumValue.Serialize([[1]]);
        rowGroup.Write(rowGroup.EnumValue);
        rowGroup.BsonValue.Serialize([[5, 0, 0, 0, 0]]);
        rowGroup.Write(rowGroup.BsonValue);
        rowGroup.Float16Value.Serialize([[0, 0]]);
        rowGroup.Write(rowGroup.Float16Value);
        rowGroup.IntervalValue.Serialize([new byte[12]]);
        rowGroup.Write(rowGroup.IntervalValue);
        rowGroup.GeometryValue.Serialize([[1]]);
        rowGroup.Write(rowGroup.GeometryValue);
        rowGroup.GeographyValue.Serialize([[1]]);
        rowGroup.Write(rowGroup.GeographyValue);
        rowGroup.UnknownValue.Serialize([null]);
        rowGroup.Write(rowGroup.UnknownValue);
        writer.CloseFile();
    }
}
