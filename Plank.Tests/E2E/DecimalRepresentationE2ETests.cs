using Plank.Schema;
using Plank.Writing;

namespace Plank.Tests.E2E;

internal sealed class DecimalRepresentationE2ETests
{
    [Test]
    [Arguments(ParquetDataPageVersion.V1)]
    [Arguments(ParquetDataPageVersion.V2)]
    public async Task DecimalRepresentationsRoundTripAcrossPagesAndAnIndependentReader(ParquetDataPageVersion pageVersion)
    {
        foreach (var physicalType in new[] { ParquetPhysicalType.Int32, ParquetPhysicalType.Int64,
                     ParquetPhysicalType.ByteArray, ParquetPhysicalType.FixedLenByteArray })
        foreach (var encoding in new[] { EncodingKind.Plain, EncodingKind.RleDictionary })
        foreach (var allNull in new[] { false, true })
        {
            var options = new ColumnOptions(encodings: [encoding],
                typeLength: physicalType == ParquetPhysicalType.FixedLenByteArray ? 12U : 0U);
            var schema = new ParquetSchema([
                ColumnDefinition.Leaf("required", physicalType, options, new LogicalType.Decimal(9, 2)),
                ColumnDefinition.OptionalLeaf("optional", physicalType, options, new LogicalType.Decimal(9, 2))
            ]);
            var required = Enumerable.Range(0, 259).Select(i => (i % 11 - 5) * 1.25m).ToArray();
            var optional = required.Select((v, i) => allNull || i % 3 == 1 ? (decimal?)null : v).ToArray();
            using var output = new MemoryStream();
            using (var writer = schema.CreateWriter(output, new ParquetWriterOptions
                   { DataPageVersion = pageVersion, TargetDataPageSizeBytes = 128, Compression = CompressionKind.None }))
            {
                var rowGroup = writer.StartRowGroup();
                var requiredColumn = writer.CreateSerializedColumn<decimal>(schema.LeafColumns[0]);
                requiredColumn.Serialize(required);
                rowGroup.Write(requiredColumn);
                var optionalColumn = writer.CreateSerializedColumn<decimal?>(schema.LeafColumns[1]);
                optionalColumn.Serialize(optional);
                rowGroup.Write(optionalColumn);
                writer.CloseFile();
            }
            using (var reader = schema.CreateReader(new MemoryStream(output.ToArray())))
            {
                var actualRequired = new List<decimal>();
                foreach (var buffer in reader.RowGroups[0].Column<decimal>(0))
                    actualRequired.AddRange(buffer.Values);
                var actualOptional = new List<decimal?>();
                foreach (var buffer in reader.RowGroups[0].Column<decimal?>(1))
                    actualOptional.AddRange(buffer.Values);
                if (!actualRequired.SequenceEqual(required) || !actualOptional.SequenceEqual(optional))
                    throw new InvalidOperationException($"Decimal round trip failed for {physicalType}/{encoding}/{pageVersion}/{allNull}.");
            }

            // Parquet.Net 6.0.3 rejects BYTE_ARRAY decimals while reading statistics.
            // That representation has an independent byte/hash oracle in RepresentationContractTests.
            if (physicalType == ParquetPhysicalType.ByteArray)
                continue;

            using var input = new MemoryStream(output.ToArray());
            var independent = await Parquet.ParquetReader.CreateAsync(input).ConfigureAwait(false);
            await using (independent.ConfigureAwait(false))
            {
                using var independentGroup = independent.OpenRowGroupReader(0);
                var actual = new decimal[required.Length];
                await independentGroup.ReadAsync<decimal>(independent.Schema.GetDataFields()[0], actual, null, default)
                    .ConfigureAwait(false);
                if (!actual.SequenceEqual(required))
                    throw new InvalidOperationException($"Independent decimal read failed for {physicalType}/{encoding}/{pageVersion}.");
            }
        }
    }
}
