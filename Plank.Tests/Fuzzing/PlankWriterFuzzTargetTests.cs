using Plank.Fuzzing;

namespace Plank.Tests.Fuzzing;

internal sealed class PlankWriterFuzzTargetTests
{
    [Test]
    public void DecodeCreatesBoundedValidCaseFromEmptyInput()
    {
        var fuzzCase = PlankWriterFuzzTarget.Decode([]);

        if (fuzzCase.Columns.Count is < 1 or > 5)
            throw new InvalidOperationException($"Unexpected column count '{fuzzCase.Columns.Count}'.");
        if (fuzzCase.RowGroups.Count is < 1 or > 3)
            throw new InvalidOperationException($"Unexpected row-group count '{fuzzCase.RowGroups.Count}'.");

        for (var rowGroupIndex = 0; rowGroupIndex < fuzzCase.RowGroups.Count; rowGroupIndex++)
        {
            var rowGroup = fuzzCase.RowGroups[rowGroupIndex];
            if (rowGroup.Count != fuzzCase.Columns.Count)
                throw new InvalidOperationException("Decoded row group column count does not match schema.");

            var rowCount = rowGroup[0].Length;
            if (rowCount is < 1 or > 64)
                throw new InvalidOperationException($"Unexpected row count '{rowCount}'.");

            for (var columnIndex = 1; columnIndex < rowGroup.Count; columnIndex++)
                if (rowGroup[columnIndex].Length != rowCount)
                    throw new InvalidOperationException(
                        $"Row-group {rowGroupIndex} column {columnIndex} row count does not match first column.");
        }
    }

    [Test]
    public void SmokeCaseWritesAndReadsWithPlankAndParquetSharp()
        => PlankWriterFuzzTarget.Execute([
            0x31, 0xD2, 0x8B, 0x6A, 0x02, 0x7F, 0x10, 0x99,
            0x00, 0x44, 0xCE, 0x71, 0x22, 0x8E, 0x5D, 0x13,
            0xF0, 0x01, 0x2B, 0x3C, 0x4D, 0x5E, 0x6F, 0x70
        ]);

    [Test]
    public void DictionaryLiteralRunBeforeRleRunWritesReadableIndexes()
        => PlankWriterFuzzTarget.Execute([
            0x63, 0x00, 0x00, 0x00, 0x14, 0x02, 0x02, 0x00
        ]);
}
