using Plank.Schema;
using Plank.Writing;

namespace Plank.Tests.Writer;

internal sealed class DeltaBinaryPackedInt32InteropTests
{
    [Test]
    [Arguments(ParquetDataPageVersion.V1)]
    [Arguments(ParquetDataPageVersion.V2)]
    public void OverflowingInt32DeltasAreReadableByParquetSharp(ParquetDataPageVersion dataPageVersion)
    {
        // Include scalar tails, SIMD lanes, and the first value of subsequent blocks.
        foreach (var count in new[] { 2, 3, 7, 8, 9, 127, 128, 129, 257 })
        {
            var values = new int[count];
            for (var i = 0; i < values.Length; i++)
                values[i] = (i % 4) switch
                {
                    0 => int.MaxValue,
                    1 => int.MinValue,
                    2 => 0,
                    _ => -1
                };
            var unsigned = Array.ConvertAll(values, static value => unchecked((uint)value));
            var optional = values.Select(static (value, index) => index % 5 == 4 ? (int?)null : value).ToArray();
            var optionalUnsigned = optional.Select(static value => value.HasValue ? (uint?)unchecked((uint)value.Value) : null).ToArray();
            var options = new ColumnOptions(encodings: [EncodingKind.DeltaBinaryPacked]);
            var unsignedType = new LogicalType.Int(32, isSigned: false);
            var schema = new ParquetSchema([
                ColumnDefinition.RequiredLeaf("signed", ParquetPhysicalType.Int32, options),
                ColumnDefinition.OptionalLeaf("optional_signed", ParquetPhysicalType.Int32, options),
                ColumnDefinition.RequiredLeaf("unsigned", ParquetPhysicalType.Int32, options, logicalType: unsignedType),
                ColumnDefinition.OptionalLeaf("optional_unsigned", ParquetPhysicalType.Int32, options, logicalType: unsignedType)
            ]);
            using var stream = new MemoryStream();
            using (var writer = schema.CreateWriter(stream, new ParquetWriterOptions
            {
                Compression = CompressionKind.None,
                DataPageVersion = dataPageVersion
            }))
            {
                var signedColumn = writer.CreateSerializedColumn<int>(schema.LeafColumns[0]);
                var optionalSignedColumn = writer.CreateSerializedColumn<int?>(schema.LeafColumns[1]);
                var unsignedColumn = writer.CreateSerializedColumn<uint>(schema.LeafColumns[2]);
                var optionalUnsignedColumn = writer.CreateSerializedColumn<uint?>(schema.LeafColumns[3]);
                signedColumn.Serialize(values);
                optionalSignedColumn.Serialize(optional);
                unsignedColumn.Serialize(unsigned);
                optionalUnsignedColumn.Serialize(optionalUnsigned);
                var rowGroup = writer.StartRowGroup();
                rowGroup.Write(signedColumn);
                rowGroup.Write(optionalSignedColumn);
                rowGroup.Write(unsignedColumn);
                rowGroup.Write(optionalUnsignedColumn);
                writer.CloseFile();
            }

            using var input = new MemoryStream(stream.ToArray(), writable: false);
            using var reader = new ParquetSharp.ParquetFileReader(input);
            using var group = reader.RowGroup(0);
            AssertColumn(group, 0, values);
            AssertColumn(group, 1, optional);
            AssertColumn(group, 2, unsigned);
            AssertColumn(group, 3, optionalUnsigned);
        }
    }

    static void AssertColumn<T>(ParquetSharp.RowGroupReader group, int index, T[] expected)
    {
        using var column = group.Column(index).LogicalReader<T>();
        var actual = column.ReadAll(expected.Length);
        if (!actual.AsSpan().SequenceEqual(expected))
            throw new InvalidOperationException(
                $"Column {index} with {expected.Length} values changed across an Int32 overflow: " +
                $"expected [{string.Join(", ", expected)}], got [{string.Join(", ", actual)}].");
    }
}
