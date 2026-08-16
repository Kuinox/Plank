using System.Collections.Immutable;
using Plank.Schema;
using Plank.Writing;

namespace Plank.Fuzzing;

/// <summary>
/// Writes a seed corpus of small, valid Parquet files for the reader fuzzer.
/// </summary>
/// <remarks>
/// The reader fuzzer plateaued at roughly 3,700 edges because it only ever saw
/// eight hand-written seeds, all uncompressed and all covering the same handful
/// of types. Reaching a Snappy or Zstd frame by mutation means inventing a valid
/// compressed stream *and* a matching codec field in the footer, which does not
/// happen. Seeding one valid file per combination puts the fuzzer inside each
/// decoder to begin with, and lets it spend its time corrupting the payloads
/// rather than trying to guess the envelope.
///
/// Every file is deliberately tiny. AFL spends time proportional to input size,
/// and the point is to reach a decoder, not to carry data.
/// </remarks>
public static class CorpusGenerator
{
    // Each seed is prefixed with the selector byte the target reads, so the file
    // is a complete test case rather than something AFL has to grow a byte onto.
    // Even selector = bind the file's own schema, which is what these exercise.
    const byte FileSchemaSelector = 0;

    public static int Generate(string outputDirectory)
    {
        ArgumentException.ThrowIfNullOrEmpty(outputDirectory);
        Directory.CreateDirectory(outputDirectory);

        var written = 0;
        foreach (var (name, bytes) in BuildCases())
        {
            var payload = new byte[bytes.Length + 1];
            payload[0] = FileSchemaSelector;
            bytes.CopyTo(payload, 1);
            File.WriteAllBytes(Path.Combine(outputDirectory, $"gen-{name}.bin"), payload);
            written++;
        }

        return written;
    }

    static IEnumerable<(string Name, byte[] Bytes)> BuildCases()
    {
        foreach (var compression in Compressions())
        {
            var tag = compression.ToString().ToLowerInvariant();

            // One file per physical type, so every value decoder is reachable.
            foreach (var (typeName, column, writer) in TypedColumns())
            {
                if (TryBuild($"{typeName}-{tag}", compression, column, writer, out var file))
                    yield return file;
            }

            // Encodings that have their own decoder paths.
            foreach (var (encName, column, writer) in EncodedColumns())
            {
                if (TryBuild($"{encName}-{tag}", compression, column, writer, out var file))
                    yield return file;
            }

            // Optional columns carry definition levels; that is a separate path
            // from the required case and the nulls exercise it.
            foreach (var (nullName, column, writer) in NullableColumns())
            {
                if (TryBuild($"{nullName}-{tag}", compression, column, writer, out var file))
                    yield return file;
            }

            // Bloom filters are a separate structure with its own offsets in the
            // footer, and nothing generated one, so BloomFilterReader had never
            // run. Only for one codec: the filter is stored uncompressed, so
            // repeating it per codec would add files without adding paths.
            if (compression != CompressionKind.None)
                continue;
            foreach (var (bloomName, column, writer) in BloomFilterColumns())
            {
                if (TryBuild(bloomName, compression, column, writer, out var file))
                    yield return file;
            }
        }
    }

    static CompressionKind[] Compressions()
        => [CompressionKind.None, CompressionKind.Snappy, CompressionKind.Gzip, CompressionKind.Zstd,
            CompressionKind.Lz4, CompressionKind.Brotli, CompressionKind.Lz4Legacy];

    static IEnumerable<(string, ColumnDefinition, Action<ParquetWriter, RowGroupWriter, LeafColumn>)> TypedColumns()
    {
        yield return ("bool", Leaf("c", ParquetPhysicalType.Boolean, EncodingKind.Plain),
            (w, g, c) => Write<bool>(w, g, c, [true, false, true, true, false]));
        yield return ("i32", Leaf("c", ParquetPhysicalType.Int32, EncodingKind.Plain),
            (w, g, c) => Write<int>(w, g, c, [0, 1, -1, int.MaxValue, int.MinValue]));
        yield return ("i64", Leaf("c", ParquetPhysicalType.Int64, EncodingKind.Plain),
            (w, g, c) => Write<long>(w, g, c, [0L, 1L, -1L, long.MaxValue, long.MinValue]));
        yield return ("f32", Leaf("c", ParquetPhysicalType.Float, EncodingKind.Plain),
            (w, g, c) => Write<float>(w, g, c, [0f, 1.5f, -1.5f, float.NaN, float.PositiveInfinity]));
        yield return ("f64", Leaf("c", ParquetPhysicalType.Double, EncodingKind.Plain),
            (w, g, c) => Write<double>(w, g, c, [0d, 1.5d, -1.5d, double.NaN, double.NegativeInfinity]));
        yield return ("bin", Leaf("c", ParquetPhysicalType.ByteArray, EncodingKind.Plain),
            (w, g, c) => Write<byte[]>(w, g, c, [[], [1], [1, 2, 3], [255, 0, 255], [7]]));
        yield return ("flba", LeafFixed("c", 4, EncodingKind.Plain),
            (w, g, c) => Write<byte[]>(w, g, c, [[1, 2, 3, 4], [0, 0, 0, 0], [255, 255, 255, 255], [9, 8, 7, 6], [1, 1, 1, 1]]));
    }

    static IEnumerable<(string, ColumnDefinition, Action<ParquetWriter, RowGroupWriter, LeafColumn>)> EncodedColumns()
    {
        yield return ("i32-delta", Leaf("c", ParquetPhysicalType.Int32, EncodingKind.DeltaBinaryPacked),
            (w, g, c) => Write<int>(w, g, c, [1, 2, 3, 100, -100]));
        yield return ("i64-delta", Leaf("c", ParquetPhysicalType.Int64, EncodingKind.DeltaBinaryPacked),
            (w, g, c) => Write<long>(w, g, c, [1L, 2L, 3L, 1000L, -1000L]));
        yield return ("i32-dict", Leaf("c", ParquetPhysicalType.Int32, EncodingKind.RleDictionary),
            (w, g, c) => Write<int>(w, g, c, [5, 5, 7, 5, 7]));
        yield return ("i64-dict", Leaf("c", ParquetPhysicalType.Int64, EncodingKind.RleDictionary),
            (w, g, c) => Write<long>(w, g, c, [5L, 5L, 7L, 5L, 7L]));
        yield return ("bin-deltalen", Leaf("c", ParquetPhysicalType.ByteArray, EncodingKind.DeltaLengthByteArray),
            (w, g, c) => Write<byte[]>(w, g, c, [[1], [2, 2], [3, 3, 3], [], [4]]));
        yield return ("bin-deltabyte", Leaf("c", ParquetPhysicalType.ByteArray, EncodingKind.DeltaByteArray),
            (w, g, c) => Write<byte[]>(w, g, c, [[1, 2], [1, 3], [1, 2, 4], [9], []]));
        yield return ("bin-dict", Leaf("c", ParquetPhysicalType.ByteArray, EncodingKind.RleDictionary),
            (w, g, c) => Write<byte[]>(w, g, c, [[1], [1], [2], [1], [2]]));
        yield return ("f64-bss", Leaf("c", ParquetPhysicalType.Double, EncodingKind.ByteStreamSplit),
            (w, g, c) => Write<double>(w, g, c, [1d, 2d, 3d, 4d, 5d]));
        yield return ("f32-bss", Leaf("c", ParquetPhysicalType.Float, EncodingKind.ByteStreamSplit),
            (w, g, c) => Write<float>(w, g, c, [1f, 2f, 3f, 4f, 5f]));
    }

    static IEnumerable<(string, ColumnDefinition, Action<ParquetWriter, RowGroupWriter, LeafColumn>)> NullableColumns()
    {
        yield return ("i32-opt", LeafOptional("c", ParquetPhysicalType.Int32, EncodingKind.Plain),
            (w, g, c) => Write<int?>(w, g, c, [1, null, 3, null, 5]));
        yield return ("i64-opt", LeafOptional("c", ParquetPhysicalType.Int64, EncodingKind.Plain),
            (w, g, c) => Write<long?>(w, g, c, [1L, null, 3L, null, 5L]));
        yield return ("f64-opt", LeafOptional("c", ParquetPhysicalType.Double, EncodingKind.Plain),
            (w, g, c) => Write<double?>(w, g, c, [1d, null, 3d, null, 5d]));
        yield return ("bool-opt", LeafOptional("c", ParquetPhysicalType.Boolean, EncodingKind.Plain),
            (w, g, c) => Write<bool?>(w, g, c, [true, null, false, null, true]));
        yield return ("bin-opt", LeafOptional("c", ParquetPhysicalType.ByteArray, EncodingKind.Plain),
            (w, g, c) => Write<byte[]?>(w, g, c, [[1], null, [3, 3], null, []]));
    }

    static IEnumerable<(string, ColumnDefinition, Action<ParquetWriter, RowGroupWriter, LeafColumn>)> BloomFilterColumns()
    {
        yield return ("bloom-i32", LeafBloom("c", ParquetPhysicalType.Int32),
            (w, g, c) => Write<int>(w, g, c, [1, 2, 3, 4, 5]));
        yield return ("bloom-i64", LeafBloom("c", ParquetPhysicalType.Int64),
            (w, g, c) => Write<long>(w, g, c, [1L, 2L, 3L, 4L, 5L]));
        yield return ("bloom-bin", LeafBloom("c", ParquetPhysicalType.ByteArray),
            (w, g, c) => Write<byte[]>(w, g, c, [[1], [2], [3], [4], [5]]));
    }

    static bool TryBuild(string name, CompressionKind compression, ColumnDefinition column,
        Action<ParquetWriter, RowGroupWriter, LeafColumn> write, out (string, byte[]) file)
    {
        try
        {
            var schema = new ParquetSchema([column]);
            using var stream = new MemoryStream();
            var writer = schema.CreateWriter(stream, new ParquetWriterOptions { Compression = compression });
            var group = writer.StartRowGroup();
            write(writer, group, schema.LeafColumns[0]);
            writer.CloseFile();
            file = (name, stream.ToArray());
            return true;
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or ArgumentException)
        {
            // A codec the build does not support, or an encoding this type cannot
            // use. Skipping keeps the generator honest about what it produced.
            file = default;
            return false;
        }
    }

    static void Write<T>(ParquetWriter writer, RowGroupWriter group, LeafColumn column, T[] values)
    {
        var serialized = writer.CreateSerializedColumn<T>(column);
        serialized.Serialize(values);
        group.Write(serialized);
    }

    static ColumnDefinition Leaf(string name, ParquetPhysicalType type, EncodingKind encoding)
        => ColumnDefinition.Leaf(name, type,
            new ColumnOptions(ParquetRepetition.Required, encodings: ImmutableArray.Create(encoding)));

    static ColumnDefinition LeafOptional(string name, ParquetPhysicalType type, EncodingKind encoding)
        => ColumnDefinition.Leaf(name, type,
            new ColumnOptions(ParquetRepetition.Optional, encodings: ImmutableArray.Create(encoding)));

    static ColumnDefinition LeafBloom(string name, ParquetPhysicalType type)
        => ColumnDefinition.Leaf(name, type,
            new ColumnOptions(ParquetRepetition.Required,
                encodings: ImmutableArray.Create(EncodingKind.Plain),
                bloomFilter: ParquetBloomFilterOptions.Default));

    static ColumnDefinition LeafFixed(string name, uint length, EncodingKind encoding)
        => ColumnDefinition.Leaf(name, ParquetPhysicalType.FixedLenByteArray,
            new ColumnOptions(ParquetRepetition.Required, encodings: ImmutableArray.Create(encoding),
                typeLength: length));
}
