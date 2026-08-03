using System.Buffers.Binary;
using System.Collections.Immutable;
using Plank.Reading;
using Plank.Reading.Logical;
using Plank.Reading.Physical;
using Plank.Schema;
using Plank.Writing;
using PhysicalFileMetadata = Plank.Reading.Physical.ParquetFileMetadata;

namespace Plank.Tests.E2E;

internal sealed class FieldIdSortingMetadataTests
{
    [Test]
    public void PlankMetadataRoundTripsAndIsReadableByParquetSharp()
    {
        var path = TempPath();
        var schema = ComplexSchema();
        try
        {
            using (var stream = File.Create(path))
            {
                var writer = schema.CreateWriter(stream, new ParquetWriterOptions
                {
                    SortingColumns =
                    [
                        new ParquetSortingColumn(0),
                        new ParquetSortingColumn(3, descending: true, nullsFirst: true)
                    ]
                });
                WriteComplexRowGroup(writer, schema);
                writer.CloseFile();
            }

            using (var stream = File.OpenRead(path))
            using (var reader = new ParquetFileReader())
            {
                reader.Reset(stream);
                var metadata = reader.Metadata;
                AssertFieldId(metadata, "identity", 10);
                AssertFieldId(metadata, "id", 11);
                AssertFieldId(metadata, "tags", 20);
                AssertFieldId(metadata, "element", 21);
                AssertFieldId(metadata, "scores", 30);
                AssertFieldId(metadata, "key", 31);
                AssertFieldId(metadata, "value", 32);

                var sortingColumns = metadata.RowGroupSortingColumns(0);
                AssertSortingColumn(sortingColumns, 0, 0, descending: false, nullsFirst: false);
                AssertSortingColumn(sortingColumns, 1, 3, descending: true, nullsFirst: true);
            }

            using (var stream = File.OpenRead(path))
            using (var reader = new ParquetReader())
            {
                reader.Reset(stream);
                if (reader.Schema.Definitions[0].FieldId != 10 ||
                    reader.Schema.Definitions[1].FieldId != 20 ||
                    reader.Schema.Definitions[2].FieldId != 30)
                    throw new InvalidOperationException("Logical schema discovery did not preserve group field IDs.");
                int?[] actualLeafIds = reader.Schema.LeafColumns.Select(static column => column.FieldId).ToArray();
                int?[] expectedLeafIds = [11, 21, 31, 32];
                if (!actualLeafIds.AsSpan().SequenceEqual(expectedLeafIds))
                    throw new InvalidOperationException("Logical schema discovery did not preserve leaf field IDs.");
            }

            using var sharpReader = new ParquetSharp.ParquetFileReader(path);
            using var sharpRoot = sharpReader.FileMetaData.Schema.GroupNode;
            AssertSharpFieldId(sharpRoot, 0, 10);
            AssertSharpFieldId(sharpRoot, 1, 20);
            AssertSharpFieldId(sharpRoot, 2, 30);
            int[] expectedSharpLeafIds = [11, 21, 31, 32];
            for (var i = 0; i < expectedSharpLeafIds.Length; i++)
            {
                using var node = sharpReader.FileMetaData.Schema.Column(i).SchemaNode;
                if (node.FieldId != expectedSharpLeafIds[i])
                    throw new InvalidOperationException(
                        $"ParquetSharp read field ID {node.FieldId} for leaf {i}; expected {expectedSharpLeafIds[i]}.");
            }

            using var sharpRowGroup = sharpReader.RowGroup(0);
            var sharpSorting = sharpRowGroup.MetaData.SortingColumns();
            if (sharpSorting.Length != 2 ||
                sharpSorting[0].ColumnIndex != 0 || sharpSorting[0].IsDescending || sharpSorting[0].NullsFirst ||
                sharpSorting[1].ColumnIndex != 3 || !sharpSorting[1].IsDescending || !sharpSorting[1].NullsFirst)
                throw new InvalidOperationException("ParquetSharp did not read Plank sorting metadata correctly.");
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Test]
    public void ReadsFieldIdsAndSortingColumnsWrittenByParquetSharp()
    {
        var path = TempPath();
        try
        {
            using var propertiesBuilder = new ParquetSharp.WriterPropertiesBuilder();
            using (var properties = propertiesBuilder
                       .SortingColumns([new ParquetSharp.WriterProperties.SortingColumn(0, true, false)]).Build())
            using (var writeStream = File.Create(path))
            using (var writer = new ParquetSharp.ParquetFileWriter(writeStream,
                       [new ParquetSharp.Column<int>("value", fieldId: 91)], null, properties, null, true))
            {
                using (var rowGroup = writer.AppendRowGroup())
                using (var column = rowGroup.NextColumn().LogicalWriter<int>())
                    column.WriteBatch([3, 2, 1]);
                writer.Close();
            }

            using var stream = File.OpenRead(path);
            using var reader = new ParquetFileReader();
            reader.Reset(stream);
            if (reader.Metadata.SchemaNodes[1].FieldId != 91)
                throw new InvalidOperationException("Plank did not read ParquetSharp's field ID.");
            var sorting = reader.Metadata.RowGroupSortingColumns(0);
            AssertSortingColumn(sorting, 0, 0, descending: true, nullsFirst: false);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Test]
    public void GeneratedSchemaPreservesFieldId()
    {
        var definition = FieldIdGeneratedRow.Schema.Definitions.Single();
        if (definition.FieldId != 73 || FieldIdGeneratedRow.Schema.LeafColumns.Single().FieldId != 73)
            throw new InvalidOperationException("The source generator did not preserve [ParquetColumn(FieldId)].");
    }

    [Test]
    public void SortingConfigurationRejectsInvalidOrdinalsAndDuplicates()
    {
        var schema = new ParquetSchema([
            ColumnDefinition.RequiredLeaf("value", ParquetPhysicalType.Int32)
        ]);

        Expect<ArgumentOutOfRangeException>(() => schema.CreateWriter(new MemoryStream(), new ParquetWriterOptions
        {
            SortingColumns = [new ParquetSortingColumn(1)]
        }));
        Expect<ArgumentException>(() => schema.CreateWriter(new MemoryStream(), new ParquetWriterOptions
        {
            SortingColumns = [new ParquetSortingColumn(0), new ParquetSortingColumn(0, descending: true)]
        }));
    }

    [Test]
    public void MalformedSortingColumnOrdinalIsRejected()
    {
        var schema = new ParquetSchema([
            ColumnDefinition.RequiredLeaf("value", ParquetPhysicalType.Int32)
        ]);
        using var stream = new MemoryStream();
        var writer = schema.CreateWriter(stream, new ParquetWriterOptions
        {
            SortingColumns = [new ParquetSortingColumn(0)]
        });
        var rowGroup = writer.StartRowGroup();
        var column = rowGroup.CreateSerializedColumn<int>(schema.LeafColumns[0]);
        column.Serialize([1]);
        rowGroup.Write(column);
        writer.CloseFile();
        var file = stream.ToArray();
        var footerLength = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(file.Length - 8, sizeof(int)));
        var footerOffset = file.Length - 8 - footerLength;
        var relativeIndexOffset = FindFirstSortingColumnIndexOffset(file.AsSpan(footerOffset, footerLength));
        file[footerOffset + relativeIndexOffset] = 2;

        Expect<CorruptParquetException>(() =>
        {
            using var malformed = new MemoryStream(file);
            using var reader = new ParquetFileReader();
            reader.Reset(malformed);
        });
    }

    static ParquetSchema ComplexSchema()
        => new([
            ColumnDefinition.RequiredGroup("identity", 10,
                ColumnDefinition.RequiredLeaf("id", ParquetPhysicalType.Int32, fieldId: 11,
                    logicalType: new LogicalType.Int(32, isSigned: true))),
            ColumnDefinition.List("tags",
                ColumnDefinition.RequiredLeaf("element", ParquetPhysicalType.Int32, fieldId: 21), fieldId: 20),
            ColumnDefinition.Map("scores",
                ColumnDefinition.RequiredLeaf("key", ParquetPhysicalType.Int32, fieldId: 31),
                ColumnDefinition.RequiredLeaf("value", ParquetPhysicalType.Int32, fieldId: 32), fieldId: 30)
        ]);

    static void WriteComplexRowGroup(ParquetWriter writer, ParquetSchema schema)
    {
        var rowGroup = writer.StartRowGroup();
        var id = rowGroup.CreateSerializedColumn<int>(schema.LeafColumns[0]);
        id.Serialize([1, 2]);
        rowGroup.Write(id);
        var tags = rowGroup.CreateSerializedColumn<int[]>(schema.LeafColumns[1]);
        tags.Serialize([[4, 5], [6]]);
        rowGroup.Write(tags);
        var keys = rowGroup.CreateSerializedColumn<int[]>(schema.LeafColumns[2]);
        keys.Serialize([[1, 2], [3]]);
        rowGroup.Write(keys);
        var values = rowGroup.CreateSerializedColumn<int[]>(schema.LeafColumns[3]);
        values.Serialize([[10, 20], [30]]);
        rowGroup.Write(values);
    }

    static void AssertFieldId(PhysicalFileMetadata metadata, string name, int expected)
    {
        for (var i = 0; i < metadata.SchemaNodeCount; i++)
        {
            var node = metadata.SchemaNodes[i];
            if (!metadata.SchemaNodeNameUtf8(i).SequenceEqual(System.Text.Encoding.UTF8.GetBytes(name)))
                continue;
            if (node.FieldId != expected)
                throw new InvalidOperationException($"Schema node '{name}' had field ID {node.FieldId}; expected {expected}.");
            return;
        }
        throw new InvalidOperationException($"Schema node '{name}' was not found.");
    }

    static void AssertSortingColumn(ReadOnlySpan<ParquetSortingColumn> sortingColumns, int index, int columnOrdinal,
        bool descending, bool nullsFirst)
    {
        if ((uint)index >= (uint)sortingColumns.Length)
            throw new InvalidOperationException($"Sorting column {index} was not present.");
        var actual = sortingColumns[index];
        if (actual.ColumnOrdinal != columnOrdinal || actual.Descending != descending || actual.NullsFirst != nullsFirst)
            throw new InvalidOperationException($"Sorting column {index} did not match the expected declaration.");
    }

    static void AssertSharpFieldId(ParquetSharp.Schema.GroupNode root, int index, int expected)
    {
        using var node = root.Field(index);
        if (node.FieldId != expected)
            throw new InvalidOperationException(
                $"ParquetSharp read field ID {node.FieldId} for root field {index}; expected {expected}.");
    }

    static int FindFirstSortingColumnIndexOffset(ReadOnlySpan<byte> footer)
    {
        var reader = new CompactProtocolReader(footer);
        reader.BeginStruct();
        while (reader.TryReadFieldHeader(out var fieldId, out var type, out var inlineBool))
        {
            if (fieldId != 4)
            {
                reader.Skip(type, inlineBool);
                continue;
            }

            var (rowGroupCount, rowGroupType) = reader.ReadListHeader();
            if (rowGroupCount == 0 || rowGroupType != CompactProtocolType.Struct)
                break;
            reader.BeginStruct();
            while (reader.TryReadFieldHeader(out var rowGroupFieldId, out var rowGroupFieldType,
                       out var rowGroupInlineBool))
            {
                if (rowGroupFieldId != 4)
                {
                    reader.Skip(rowGroupFieldType, rowGroupInlineBool);
                    continue;
                }

                var (sortingCount, sortingType) = reader.ReadListHeader();
                if (sortingCount == 0 || sortingType != CompactProtocolType.Struct)
                    break;
                reader.BeginStruct();
                while (reader.TryReadFieldHeader(out var sortingFieldId, out var sortingFieldType,
                           out var sortingInlineBool))
                {
                    if (sortingFieldId == 1)
                        return reader.Offset;
                    reader.Skip(sortingFieldType, sortingInlineBool);
                }
                break;
            }
            break;
        }
        throw new InvalidOperationException("Could not locate sorting column_idx in the generated footer.");
    }

    static void Expect<TException>(Action action) where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }
        throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
    }

    static string TempPath()
        => Path.Combine(Path.GetTempPath(), $"plank-field-id-sorting-{Guid.NewGuid():N}.parquet");
}
