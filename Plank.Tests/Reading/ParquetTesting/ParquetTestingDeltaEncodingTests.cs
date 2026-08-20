using System.Globalization;
using System.Text;
using Plank.Reading;
using Plank.Reading.Logical;
using Plank.Schema;

namespace Plank.Tests.Reading.ParquetTesting;

/// <summary>
/// Checks Plank's DELTA_BINARY_PACKED and DELTA_BYTE_ARRAY decoders against the expected
/// contents apache/parquet-testing ships alongside the files.
/// </summary>
/// <remarks>
/// Every other decoder test in this repository is a round trip through Plank's own writer,
/// which cannot catch a decoder and an encoder that are wrong in the same way. These two
/// files were written by parquet-mr and the CSVs next to them are what parquet-mr, arrow-cpp
/// and arrow-rs all agree they decode to, so this is the first test in the suite that checks
/// Plank against something other than itself.
///
/// The corpus ships two more expectation pairs, delta_binary_packed and delta_byte_array,
/// which are the wide bit-width and long-string stress cases. Plank cannot decode either
/// yet -- both are recorded in <see cref="ParquetTestingCompatibilityTests"/> -- and they
/// belong here as soon as it can.
/// </remarks>
internal sealed class ParquetTestingDeltaEncodingTests
{
    // INT32 and BINARY, all columns required. parquet-mr 1.12.1.
    [Test]
    public void DeltaEncodingRequiredColumn_MatchesExpectedCsv()
        => AssertMatchesExpectation("data/delta_encoding_required_column.parquet",
            "data/delta_encoding_required_column_expect.csv");

    // INT64 and BINARY, all columns optional, so this one also covers the definition-level
    // interleaving the required file cannot reach.
    [Test]
    public void DeltaEncodingOptionalColumn_MatchesExpectedCsv()
        => AssertMatchesExpectation("data/delta_encoding_optional_column.parquet",
            "data/delta_encoding_optional_column_expect.csv");

    static void AssertMatchesExpectation(string parquetPath, string csvPath)
    {
        if (!ParquetTestingCorpus.IsAvailable)
            throw new InvalidOperationException(ParquetTestingCorpus.MissingMessage);

        var expected = ReadCsv(ParquetTestingCorpus.Resolve(csvPath));
        var actual = ReadColumns(ParquetTestingCorpus.ReadAllBytes(parquetPath));

        if (actual.Count != expected.Columns.Count)
            throw new InvalidOperationException(
                $"{parquetPath}: expected {expected.Columns.Count} columns but the file has {actual.Count}.");

        for (var column = 0; column < actual.Count; column++)
        {
            var expectedValues = expected.Columns[column];
            var actualValues = actual[column];
            // The CSV header names carry stray spaces upstream (" c_customer_id"), so it is
            // reported for context but never matched against the schema.
            var name = expected.Names[column].Trim();

            if (actualValues.Count != expectedValues.Count)
                throw new InvalidOperationException(
                    $"{parquetPath}: column '{name}' decoded {actualValues.Count} rows, expected {expectedValues.Count}.");

            for (var row = 0; row < actualValues.Count; row++)
                if (!string.Equals(actualValues[row], expectedValues[row], StringComparison.Ordinal))
                    throw new InvalidOperationException(
                        $"{parquetPath}: column '{name}' row {row} decoded to {Describe(actualValues[row])}, expected {Describe(expectedValues[row])}.");
        }
    }

    static string Describe(string? value)
        => value is null ? "null" : $"'{value}'";

    // Decodes every leaf column to its text form, concatenating row groups, so the result
    // lines up one-for-one with the CSV.
    static List<List<string?>> ReadColumns(byte[] file)
    {
        using var source = new MemoryReadSource(file);
        using var reader = new ParquetReader();
        reader.Reset(source);

        var columns = reader.Schema.LeafColumns
            .Select(_ => new List<string?>())
            .ToList();

        foreach (var group in reader.RowGroups)
            for (var i = 0; i < reader.Schema.LeafColumns.Length; i++)
            {
                var column = reader.Schema.LeafColumns[i];
                var optional = column.MaxDefinitionLevel > 0;
                switch (column.PhysicalType)
                {
                    case ParquetPhysicalType.Int32:
                        if (optional) ReadNumbers(group.Column<int?>(column), columns[i]);
                        else ReadNumbers(group.Column<int>(column), columns[i]);
                        break;
                    case ParquetPhysicalType.Int64:
                        if (optional) ReadNumbers(group.Column<long?>(column), columns[i]);
                        else ReadNumbers(group.Column<long>(column), columns[i]);
                        break;
                    case ParquetPhysicalType.ByteArray:
                        ReadStrings(group.Column<byte>(column), columns[i]);
                        break;
                    default:
                        throw new InvalidOperationException(
                            $"Column '{column.Path}' has unexpected physical type {column.PhysicalType}.");
                }
            }

        return columns;
    }

    static void ReadNumbers<T>(RowGroupColumn<T> buffers, List<string?> destination) where T : struct
    {
        foreach (var buffer in buffers)
        {
            var values = buffer.Values;
            for (var i = 0; i < values.Length; i++)
                destination.Add(Convert.ToString(values[i], CultureInfo.InvariantCulture));
        }
    }

    static void ReadNumbers<T>(RowGroupColumn<T?> buffers, List<string?> destination) where T : struct
    {
        foreach (var buffer in buffers)
        {
            var values = buffer.Values;
            for (var i = 0; i < values.Length; i++)
                destination.Add(values[i] is { } value
                    ? Convert.ToString(value, CultureInfo.InvariantCulture)
                    : null);
        }
    }

    static void ReadStrings(RowGroupColumn<byte> buffers, List<string?> destination)
    {
        foreach (var buffer in buffers)
            for (var i = 0; i < buffer.Count; i++)
                destination.Add(buffer.IsNull(i) ? null : Encoding.UTF8.GetString(buffer.GetValue(i)));
    }

    // The expectation files quote every present value and leave nulls as a bare empty
    // field, which is the only thing that distinguishes a null from an empty string. Values
    // do contain commas ("VIRGIN ISLANDS, U.S."), so this cannot be a split on ','.
    static (List<string> Names, List<List<string?>> Columns) ReadCsv(string path)
    {
        var lines = File.ReadAllLines(path);
        var names = ParseRow(lines[0]).Select(value => value ?? "").ToList();
        var columns = names.Select(_ => new List<string?>()).ToList();

        for (var line = 1; line < lines.Length; line++)
        {
            if (lines[line].Length == 0)
                continue;

            var row = ParseRow(lines[line]);
            if (row.Count != names.Count)
                throw new InvalidOperationException(
                    $"{path}: line {line + 1} has {row.Count} fields, expected {names.Count}.");

            for (var i = 0; i < row.Count; i++)
                columns[i].Add(row[i]);
        }

        return (names, columns);
    }

    static List<string?> ParseRow(string line)
    {
        var fields = new List<string?>();
        var index = 0;
        while (true)
        {
            if (index < line.Length && line[index] == '"')
            {
                var end = line.IndexOf('"', index + 1);
                if (end < 0)
                    throw new InvalidOperationException($"Unterminated quoted field in: {line}");
                fields.Add(line[(index + 1)..end]);
                index = end + 1;
            }
            else
            {
                // A bare field. Only ever empty in these files, which is how a null is spelled.
                var end = line.IndexOf(',', index);
                var raw = end < 0 ? line[index..] : line[index..end];
                fields.Add(raw.Length == 0 ? null : raw);
                index = end < 0 ? line.Length : end;
            }

            if (index >= line.Length)
                return fields;
            if (line[index] != ',')
                throw new InvalidOperationException($"Expected a field separator at offset {index} in: {line}");
            index++;
        }
    }
}
