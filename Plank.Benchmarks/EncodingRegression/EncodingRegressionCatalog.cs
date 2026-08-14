using System.Collections.Immutable;
using System.Text;
using Plank.Schema;
using Plank.Writing.PageStrategy;

namespace Plank.Benchmarks.EncodingRegression;

/// <summary>
/// Builds one column per supported (physical type, encoding, repetition) combination.
/// Every encoder in <c>Plank.Writing.Encoding</c> is reachable from at least one case here.
/// </summary>
static class EncodingRegressionCatalog
{
    internal static readonly string[] DataTypes =
        ["bool", "int32", "int64", "float", "double", "binary", "memory", "guid"];

    internal static readonly string[] Encodings =
    [
        "plain", "dictionary", "delta_binary_packed", "delta_length_byte_array", "delta_byte_array",
        "byte_stream_split"
    ];

    internal static readonly string[] Repetitions = ["required", "optional", "repeated"];

    const int NullEvery = 7;
    const int MaxListLength = 4;

    internal static IReadOnlyList<IEncodingRegressionColumn> Create(int rowCount)
    {
        // One input array per (data type, repetition), shared by every encoding of that shape: it keeps
        // the comparison fair across encodings and keeps a 200k-row run inside a sane memory budget.
        var payloads = new Dictionary<(string DataType, string Repetition), Payload>();
        var columns = new List<IEncodingRegressionColumn>();
        foreach (var dataType in DataTypes)
            foreach (var encoding in Encodings)
                foreach (var repetition in Repetitions)
                {
                    if (!IsSupported(dataType, encoding, repetition))
                        continue;

                    var key = (dataType, repetition);
                    if (!payloads.TryGetValue(key, out var payload))
                    {
                        payload = BuildPayload(dataType, repetition, rowCount);
                        payloads.Add(key, payload);
                    }

                    var regressionCase = new EncodingRegressionCase(dataType, encoding, repetition);
                    columns.Add(payload.CreateColumn(regressionCase, CreateSchema(regressionCase)));
                }

        return columns;
    }

    /// <summary>Input values plus the factory that pairs them with a schema.</summary>
    sealed record Payload(Func<EncodingRegressionCase, ParquetSchema, IEncodingRegressionColumn> CreateColumn);

    static Payload BuildPayload(string dataType, string repetition, int rowCount)
        => dataType switch
        {
            "bool" => BuildNumericPayload<bool>(repetition, rowCount, static i => (i % 3) != 0),
            "int32" => BuildNumericPayload<int>(repetition, rowCount, static i => i % 100_000),
            "int64" => BuildNumericPayload<long>(repetition, rowCount, static i => i * 37L),
            "float" => BuildNumericPayload<float>(repetition, rowCount, static i => (i % 10_000) / 3f),
            "double" => BuildNumericPayload<double>(repetition, rowCount, static i => (i % 10_000) / 7d),
            "guid" => BuildNumericPayload<Guid>(repetition, rowCount, CreateGuid),
            "binary" => BuildBinaryPayload(repetition, rowCount),
            "memory" => BuildMemoryPayload(repetition, rowCount),
            _ => throw new InvalidOperationException($"Unknown data type '{dataType}'.")
        };

    static Payload BuildNumericPayload<TValue>(string repetition, int rowCount, Func<int, TValue> factory)
        where TValue : struct
    {
        switch (repetition)
        {
            case "required":
            {
                var values = new TValue[rowCount];
                for (var i = 0; i < rowCount; i++)
                    values[i] = factory(i);
                return new Payload((regressionCase, schema)
                    => new EncodingRegressionColumn<TValue>(regressionCase, schema, values, rowCount));
            }
            case "optional":
            {
                var values = new TValue?[rowCount];
                var present = 0L;
                for (var i = 0; i < rowCount; i++)
                {
                    if (i % NullEvery == 0)
                        continue;
                    values[i] = factory(i);
                    present++;
                }

                return new Payload((regressionCase, schema)
                    => new EncodingRegressionColumn<TValue?>(regressionCase, schema, values, present));
            }
            case "repeated":
            {
                var rows = new TValue[rowCount][];
                var present = 0L;
                for (var i = 0; i < rowCount; i++)
                {
                    var length = i % (MaxListLength + 1);
                    var row = new TValue[length];
                    for (var j = 0; j < length; j++)
                        row[j] = factory(i + j);
                    rows[i] = row;
                    present += length;
                }

                return new Payload((regressionCase, schema)
                    => new EncodingRegressionColumn<TValue[]>(regressionCase, schema, rows, present));
            }
            default:
                throw new InvalidOperationException($"Unknown repetition '{repetition}'.");
        }
    }

    static Payload BuildBinaryPayload(string repetition, int rowCount)
    {
        switch (repetition)
        {
            case "required":
            {
                var values = new byte[rowCount][];
                for (var i = 0; i < rowCount; i++)
                    values[i] = CreateBytes(i);
                return new Payload((regressionCase, schema)
                    => new EncodingRegressionColumn<byte[]>(regressionCase, schema, values, rowCount));
            }
            case "optional":
            {
                var values = new byte[rowCount][];
                var present = 0L;
                for (var i = 0; i < rowCount; i++)
                {
                    if (i % NullEvery == 0)
                        continue;
                    values[i] = CreateBytes(i);
                    present++;
                }

                return new Payload((regressionCase, schema)
                    => new EncodingRegressionColumn<byte[]>(regressionCase, schema, values, present));
            }
            case "repeated":
            {
                var rows = new byte[rowCount][][];
                var present = 0L;
                for (var i = 0; i < rowCount; i++)
                {
                    var length = i % (MaxListLength + 1);
                    var row = new byte[length][];
                    for (var j = 0; j < length; j++)
                        row[j] = CreateBytes(i + j);
                    rows[i] = row;
                    present += length;
                }

                return new Payload((regressionCase, schema)
                    => new EncodingRegressionColumn<byte[][]>(regressionCase, schema, rows, present));
            }
            default:
                throw new InvalidOperationException($"Unknown repetition '{repetition}'.");
        }
    }

    static Payload BuildMemoryPayload(string repetition, int rowCount)
    {
        switch (repetition)
        {
            case "required":
            {
                var values = new ReadOnlyMemory<byte>[rowCount];
                for (var i = 0; i < rowCount; i++)
                    values[i] = CreateBytes(i);
                return new Payload((regressionCase, schema)
                    => new EncodingRegressionColumn<ReadOnlyMemory<byte>>(regressionCase, schema, values, rowCount));
            }
            case "optional":
            {
                var values = new ReadOnlyMemory<byte>?[rowCount];
                var present = 0L;
                for (var i = 0; i < rowCount; i++)
                {
                    if (i % NullEvery == 0)
                        continue;
                    values[i] = CreateBytes(i);
                    present++;
                }

                return new Payload((regressionCase, schema)
                    => new EncodingRegressionColumn<ReadOnlyMemory<byte>?>(regressionCase, schema, values, present));
            }
            default:
                throw new InvalidOperationException($"Unknown repetition '{repetition}'.");
        }
    }

    /// <summary>
    /// The combinations Plank's writer accepts. Cases outside this set throw by design, so they are
    /// excluded rather than reported as failures.
    /// </summary>
    internal static bool IsSupported(string dataType, string encoding, string repetition)
    {
        if (!IsEncodingSupported(dataType, encoding))
            return false;

        return repetition switch
        {
            "required" => true,
            // Optional byte-array columns dispatch through the optional-only encoder entry points,
            // which cover Plain and the two delta byte-array encodings.
            "optional" => dataType is not ("binary" or "memory")
                || encoding is "plain" or "dictionary" or "delta_length_byte_array" or "delta_byte_array",
            // Repeated rows are jagged arrays; ReadOnlyMemory and Guid have no array row mapping here.
            // Repeated columns return before any dictionary decision, so "dictionary" would silently
            // resolve to Plain and duplicate the plain case.
            "repeated" => dataType is not ("memory" or "guid") && encoding != "dictionary",
            _ => false
        };
    }

    static bool IsEncodingSupported(string dataType, string encoding)
        => encoding switch
        {
            "plain" => true,
            "dictionary" => true,
            "delta_binary_packed" => dataType is "int32" or "int64",
            "delta_length_byte_array" => dataType is "binary" or "memory",
            "delta_byte_array" => dataType is "binary" or "memory",
            "byte_stream_split" => dataType is "int32" or "int64" or "float" or "double" or "guid",
            _ => false
        };

    // Shared prefixes keep DELTA_BYTE_ARRAY on its prefix-compression path, and the bounded distinct
    // count keeps dictionary encoding representative rather than degenerate.
    static byte[] CreateBytes(int index)
        => Encoding.UTF8.GetBytes($"row-value-{index % 2048:D4}");

    static Guid CreateGuid(int index)
    {
        Span<byte> bytes = stackalloc byte[16];
        BitConverter.TryWriteBytes(bytes, index);
        BitConverter.TryWriteBytes(bytes[4..], index * 31);
        BitConverter.TryWriteBytes(bytes[8..], index * 131);
        BitConverter.TryWriteBytes(bytes[12..], index * 17);
        return new Guid(bytes);
    }

    static ParquetSchema CreateSchema(EncodingRegressionCase regressionCase)
    {
        var physicalType = MapPhysicalType(regressionCase.DataType);
        var encoding = MapEncoding(regressionCase.Encoding);
        var pageStrategy = string.Equals(regressionCase.Encoding, "dictionary", StringComparison.Ordinal)
            ? ForceDictionaryPageStrategy.Shared
            : null;
        var logicalType = regressionCase.DataType == "guid" ? new LogicalType.Uuid() : null;
        var typeLength = regressionCase.DataType == "guid" ? 16u : 0u;

        if (regressionCase.Repetition == "repeated")
        {
            var element = ColumnDefinition.RequiredLeaf("element", physicalType,
                new ColumnOptions(ParquetRepetition.Required, [encoding], typeLength), logicalType, pageStrategy);
            return new ParquetSchema([ColumnDefinition.List("value", element)]);
        }

        var repetition = regressionCase.Repetition == "optional"
            ? ParquetRepetition.Optional
            : ParquetRepetition.Required;
        return new ParquetSchema([
            ColumnDefinition.Leaf("value", physicalType,
                new ColumnOptions(repetition, [encoding], typeLength), logicalType, pageStrategy)
        ]);
    }

    static ParquetPhysicalType MapPhysicalType(string dataType)
        => dataType switch
        {
            "bool" => ParquetPhysicalType.Boolean,
            "int32" => ParquetPhysicalType.Int32,
            "int64" => ParquetPhysicalType.Int64,
            "float" => ParquetPhysicalType.Float,
            "double" => ParquetPhysicalType.Double,
            "binary" or "memory" => ParquetPhysicalType.ByteArray,
            "guid" => ParquetPhysicalType.FixedLenByteArray,
            _ => throw new InvalidOperationException($"Unknown data type '{dataType}'.")
        };

    static EncodingKind MapEncoding(string encoding)
        => encoding switch
        {
            "plain" => EncodingKind.Plain,
            "dictionary" => EncodingKind.RleDictionary,
            "delta_binary_packed" => EncodingKind.DeltaBinaryPacked,
            "delta_length_byte_array" => EncodingKind.DeltaLengthByteArray,
            "delta_byte_array" => EncodingKind.DeltaByteArray,
            "byte_stream_split" => EncodingKind.ByteStreamSplit,
            _ => throw new InvalidOperationException($"Unknown encoding '{encoding}'.")
        };
}
