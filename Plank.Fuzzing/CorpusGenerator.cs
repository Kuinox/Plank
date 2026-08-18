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

    // The target routes a selector whose low two bits are 2 to the row-oriented
    // reader. Seeds carry that byte, so without a variant that spells it the row
    // API is only reached if a mutation happens to guess it.
    const byte RowApiSelector = 2;

    // Bit 4 turns on VerifyPageCrc in the target. A CRC-bearing file read with
    // verification off is just a normal file — the reader never hashes it — so
    // the CRC seeds have to spell the bit themselves rather than wait for a
    // mutation to find it.
    const byte VerifyCrcSelector = 0x10;

    // Bit 7 sends the input to a decompressor instead of the Parquet reader.
    const byte DecompressorSelector = 0x80;

    public static int Generate(string outputDirectory)
    {
        ArgumentException.ThrowIfNullOrEmpty(outputDirectory);
        Directory.CreateDirectory(outputDirectory);

        var written = 0;
        foreach (var (name, selector, bytes) in BuildCases())
        {
            written += WriteSeed(outputDirectory, $"gen-{name}", selector, bytes);

            // Uncompressed cases get a row-API twin. Repeating it per codec would
            // add files without adding paths: the row reader sits above the
            // decompressor and cannot tell them apart. The twin keeps whatever
            // reader options the original asked for — the row reader has its own
            // page cursor, so its CRC call sites are not the columnar ones.
            if (IsUncompressed(name))
                written += WriteSeed(outputDirectory, $"gen-rowapi-{name}",
                    (byte)(RowApiSelector | (selector & VerifyCrcSelector)), bytes);
        }

        // The decompressors get fed directly rather than through a file, because
        // a file can only carry a codec the writer can produce and it cannot
        // produce Lz4Legacy at all.
        foreach (var (name, bytes) in DecompressorCorpus.BuildCases())
            written += WriteSeed(outputDirectory, $"gen-codec-{name}", DecompressorSelector, bytes);

        return written;
    }

    // A case is uncompressed unless its name carries a codec tag. Testing for the
    // absence of a compressed tag rather than the presence of "-none" matters:
    // the bloom, logical and nested families are built only for CompressionKind
    // .None and so have no tag at all, and the old check (EndsWith("-none") or
    // no dash at all) matched none of them — every one of those families went
    // without a row-API twin, which is part of why the nested row reader was
    // never driven.
    static bool IsUncompressed(string name)
    {
        foreach (var compression in Compressions())
        {
            if (compression == CompressionKind.None)
                continue;
            if (name.Contains($"-{compression.ToString().ToLowerInvariant()}", StringComparison.Ordinal))
                return false;
        }

        return true;
    }

    static int WriteSeed(string directory, string name, byte selector, byte[] bytes)
    {
        var payload = new byte[bytes.Length + 1];
        payload[0] = selector;
        bytes.CopyTo(payload, 1);
        File.WriteAllBytes(Path.Combine(directory, $"{name}.bin"), payload);
        return 1;
    }

    static IEnumerable<(string Name, byte Selector, byte[] Bytes)> BuildCases()
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

            // Logical types route through a whole parallel decode family — the
            // converted path — that the physical-only cases never enter.
            // DecodeNullablePlainDateTimes and
            // TryDecodeConvertedRequiredByPhysicalType both measured 0%.
            // (This used to cite TryDecodePlainIntoBuffer too. That one turned
            // out to be unreachable rather than merely unseeded, and has since
            // been deleted — no seed could ever have moved it.)
            foreach (var (logicalName, column, writer) in LogicalTypeColumns())
            {
                if (TryBuild(logicalName, compression, column, writer, out var file))
                    yield return file;
            }

            // Nested shapes are the only source of repetition levels, and those
            // levels come off the page rather than the schema, so a corrupt file
            // controls them. Nothing generated a nested file, so none of that
            // decoding had ever run.
            foreach (var (nestedName, column, writer) in NestedColumns())
            {
                if (TryBuild(nestedName, compression, column, writer, out var file))
                    yield return file;
            }
        }

        // Every case above is a DataPageV2 page, because that is the writer's
        // default and nothing ever overrode it. A V1 page is a different shape:
        // its levels live inside the payload rather than in the header, and they
        // are length-prefixed and encoded differently. The reader branches on
        // header.Type in seven places to tell them apart, and no generated seed
        // had ever taken the V1 side of any of them. V1 is also what parquet-mr
        // wrote for years, so it is the shape most files in the wild have.
        //
        // Two codecs rather than seven: the page version and the codec are
        // independent, and one uncompressed plus one compressed case already
        // covers both sides of the "does the payload need inflating" branch.
        foreach (var compression in (CompressionKind[])[CompressionKind.None, CompressionKind.Snappy])
        {
            var tag = compression.ToString().ToLowerInvariant();
            foreach (var (name, column, writer) in AllColumnFamilies())
            {
                if (TryBuild($"{name}-{tag}-v1", compression, column, writer, out var file,
                        ParquetDataPageVersion.V1))
                    yield return file;
            }
        }

        // Page CRCs are off by default in the writer, so no seed carried one,
        // and the reader skips verification entirely when the header has no CRC
        // field — ParquetCrc32 measured 0/54 lines. Verification has three
        // distinct paths (uncompressed payload, compressed payload, and V2's
        // separate level bytes), so seeds have to span both page versions and
        // both compressed and uncompressed pages to reach all three.
        foreach (var pageVersion in (ParquetDataPageVersion[])[ParquetDataPageVersion.V2, ParquetDataPageVersion.V1])
        {
            foreach (var compression in (CompressionKind[])[CompressionKind.None, CompressionKind.Snappy])
            {
                var tag = compression.ToString().ToLowerInvariant();
                var suffix = pageVersion == ParquetDataPageVersion.V1 ? "v1" : "v2";
                foreach (var (name, column, writer) in CrcColumns())
                {
                    if (TryBuild($"crc-{name}-{tag}-{suffix}", compression, column, writer, out var file,
                            pageVersion, writePageCrc: true, selector: VerifyCrcSelector))
                        yield return file;
                }
            }
        }
    }

    // The V1 pass reuses every family rather than a hand-picked slice: the page
    // version cuts across all of them, and a family that only ever appears as a
    // V2 page leaves its V1 level-decoding untested.
    static IEnumerable<(string, ColumnDefinition, Action<ParquetWriter, RowGroupWriter, LeafColumn>)> AllColumnFamilies()
    {
        foreach (var c in TypedColumns()) yield return c;
        foreach (var c in EncodedColumns()) yield return c;
        foreach (var c in NullableColumns()) yield return c;
        foreach (var c in LogicalTypeColumns()) yield return c;
        foreach (var c in NestedColumns()) yield return c;
    }

    // A small slice for the CRC pass: verification hashes the page bytes without
    // caring what they encode, so what matters is the page *shape* — required vs
    // optional (V2 stores levels separately, and those are hashed on their own),
    // nested (repetition levels too) and a page big enough to span more than one
    // buffer chunk.
    static IEnumerable<(string, ColumnDefinition, Action<ParquetWriter, RowGroupWriter, LeafColumn>)> CrcColumns()
    {
        yield return ("i32", Leaf("c", ParquetPhysicalType.Int32, EncodingKind.Plain),
            (w, g, c) => Write<int>(w, g, c, [0, 1, -1, int.MaxValue, int.MinValue]));
        yield return ("bin", Leaf("c", ParquetPhysicalType.ByteArray, EncodingKind.Plain),
            (w, g, c) => Write<byte[]>(w, g, c, [[], [1], [1, 2, 3], [255, 0, 255], [7]]));
        yield return ("i32-opt", LeafOptional("c", ParquetPhysicalType.Int32, EncodingKind.Plain),
            (w, g, c) => Write<int?>(w, g, c, [1, null, 3, null, 5]));
        yield return ("i64-large", Leaf("c", ParquetPhysicalType.Int64, EncodingKind.Plain),
            (w, g, c) => Write<long>(w, g, c, Ramp(5000)));
        yield return ("list-i32",
            ColumnDefinition.List("c",
                ColumnDefinition.Leaf("element", ParquetPhysicalType.Int32,
                    new ColumnOptions(ParquetRepetition.Optional,
                        encodings: ImmutableArray.Create(EncodingKind.Plain)))),
            (w, g, c) => WriteRows<int?[]>(g, c, [[1, null], [], [3]]));
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
        yield return ("f64-alp", Leaf("c", ParquetPhysicalType.Double, EncodingKind.Alp),
            (w, g, c) => Write<double>(w, g, c,
                [1.25d, 2.5d, -3.75d, -0d, double.NaN, double.PositiveInfinity]));
        yield return ("f32-alp", Leaf("c", ParquetPhysicalType.Float, EncodingKind.Alp),
            (w, g, c) => Write<float>(w, g, c,
                [1.25f, 2.5f, -3.75f, -0f, float.NaN, float.NegativeInfinity]));

        yield return ("i32-bss", Leaf("c", ParquetPhysicalType.Int32, EncodingKind.ByteStreamSplit),
            (w, g, c) => Write<int>(w, g, c, [0, 1, -1, int.MaxValue, int.MinValue]));

        yield return ("i64-bss", Leaf("c", ParquetPhysicalType.Int64, EncodingKind.ByteStreamSplit),
            (w, g, c) => Write<long>(w, g, c, [0L, 1L, -1L, long.MaxValue, long.MinValue]));

        // The batched "slice" decoders only run once a page is big enough to be
        // split into batches; every seed so far held five values, so the slice
        // variants of ByteStreamSplit and delta never ran.
        yield return ("i64-bss-large", Leaf("c", ParquetPhysicalType.Int64, EncodingKind.ByteStreamSplit),
            (w, g, c) => Write<long>(w, g, c, Ramp(5000)));

        yield return ("i32-bss-large", Leaf("c", ParquetPhysicalType.Int32, EncodingKind.ByteStreamSplit),
            (w, g, c) => Write<int>(w, g, c, Array.ConvertAll(Ramp(5000), static v => (int)v)));

        yield return ("f64-bss-large", Leaf("c", ParquetPhysicalType.Double, EncodingKind.ByteStreamSplit),
            (w, g, c) => Write<double>(w, g, c, Array.ConvertAll(Ramp(5000), static v => v / 8d)));

        yield return ("i64-delta-large", Leaf("c", ParquetPhysicalType.Int64, EncodingKind.DeltaBinaryPacked),
            (w, g, c) => Write<long>(w, g, c, Ramp(5000)));

        yield return ("i32-delta-large", Leaf("c", ParquetPhysicalType.Int32, EncodingKind.DeltaBinaryPacked),
            (w, g, c) => Write<int>(w, g, c, Array.ConvertAll(Ramp(5000), static v => (int)v)));

        yield return ("i64-plain-large", Leaf("c", ParquetPhysicalType.Int64, EncodingKind.Plain),
            (w, g, c) => Write<long>(w, g, c, Ramp(5000)));

        // A dictionary wider than 2048 entries needs more than 11 bits per
        // index, which is a separate decode path from the narrow one. The
        // five-value seeds only ever produced single-digit dictionaries.
        yield return ("i64-dict-wide", Leaf("c", ParquetPhysicalType.Int64, EncodingKind.RleDictionary),
            (w, g, c) => Write<long>(w, g, c, WideDictionary(3000, 3)));

        yield return ("i32-dict-11bit", Leaf("c", ParquetPhysicalType.Int32, EncodingKind.RleDictionary),
            (w, g, c) => Write<int>(w, g, c, Array.ConvertAll(WideDictionary(1500, 3), static v => (int)v)));
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

    static IEnumerable<(string, ColumnDefinition, Action<ParquetWriter, RowGroupWriter, LeafColumn>)> LogicalTypeColumns()
    {
        yield return ("logical-date", Annotated("c", ParquetPhysicalType.Int32, new LogicalType.Date()),
            (w, g, c) => Write<DateOnly>(w, g, c,
                [new(1970, 1, 1), new(2000, 2, 29), new(2026, 7, 27), DateOnly.MinValue, DateOnly.MaxValue]));

        yield return ("logical-time-micros",
            Annotated("c", ParquetPhysicalType.Int64, new LogicalType.Time(TimeUnit.Micros, IsAdjustedToUtc: false)),
            (w, g, c) => Write<TimeOnly>(w, g, c,
                [TimeOnly.MinValue, new(1, 2, 3, 4), new(12, 34, 56), TimeOnly.MaxValue, new(0, 0, 1)]));

        yield return ("logical-timestamp-micros",
            Annotated("c", ParquetPhysicalType.Int64, new LogicalType.Timestamp(TimeUnit.Micros, IsAdjustedToUtc: true)),
            (w, g, c) => Write<DateTime>(w, g, c,
                [DateTime.UnixEpoch, new(2026, 7, 27, 1, 2, 3, DateTimeKind.Utc),
                 new(1999, 12, 31, 23, 59, 59, DateTimeKind.Utc), DateTime.UnixEpoch.AddTicks(1),
                 new(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc)]));

        yield return ("logical-timestamp-nanos",
            Annotated("c", ParquetPhysicalType.Int64, new LogicalType.Timestamp(TimeUnit.Nanos, IsAdjustedToUtc: true)),
            (w, g, c) => Write<DateTime>(w, g, c,
                [DateTime.UnixEpoch, DateTime.UnixEpoch.AddTicks(7), new(2020, 5, 5, 5, 5, 5, DateTimeKind.Utc),
                 DateTime.UnixEpoch.AddSeconds(1), DateTime.UnixEpoch.AddDays(1)]));

        // Decimal is backed by four different physical types, each with its own
        // conversion, and ParquetDecimalConverter measured 0%.
        yield return ("logical-decimal-i32",
            Annotated("c", ParquetPhysicalType.Int32, new LogicalType.Decimal(9, 2)),
            (w, g, c) => Write<decimal>(w, g, c, [0m, 1.23m, -1.23m, 9999999.99m, -9999999.99m]));

        yield return ("logical-decimal-i64",
            Annotated("c", ParquetPhysicalType.Int64, new LogicalType.Decimal(18, 4)),
            (w, g, c) => Write<decimal>(w, g, c, [0m, 1.2345m, -1.2345m, 99999999999999.9999m, 0.0001m]));

        yield return ("logical-decimal-flba",
            AnnotatedFixed("c", 16, new LogicalType.Decimal(28, 6)),
            (w, g, c) => Write<decimal>(w, g, c, [0m, 1.234567m, -1.234567m, 1000000m, -0.000001m]));

        yield return ("logical-uuid", AnnotatedFixed("c", 16, new LogicalType.Uuid()),
            (w, g, c) => Write<Guid>(w, g, c,
                [Guid.Empty, new("00000000-0000-0000-0000-000000000001"),
                 new("ffffffff-ffff-ffff-ffff-ffffffffffff"), new("12345678-1234-5678-1234-567812345678"),
                 new("87654321-4321-8765-4321-876543218765")]));

        yield return ("logical-string", Annotated("c", ParquetPhysicalType.ByteArray, new LogicalType.String()),
            (w, g, c) => Write<string>(w, g, c, ["", "a", "hello", "\u00e9\u00e8\u00ea", new string('x', 40)]));

        yield return ("logical-json", Annotated("c", ParquetPhysicalType.ByteArray, new LogicalType.Json()),
            (w, g, c) => Write<byte[]>(w, g, c, [[123, 125], [91, 93], [110, 117, 108, 108], [], [34, 34]]));

        // Plank does not support sbyte or short as CLR value types, so the
        // signed narrow widths have no direct representation here; the unsigned
        // ones do.
        yield return ("logical-uint8", Annotated("c", ParquetPhysicalType.Int32, new LogicalType.Int(8, false)),
            (w, g, c) => Write<byte>(w, g, c, [0, 1, 255, 128, 42]));

        yield return ("logical-uint32", Annotated("c", ParquetPhysicalType.Int32, new LogicalType.Int(32, false)),
            (w, g, c) => Write<uint>(w, g, c, [0u, 1u, uint.MaxValue, 2147483648u, 12345u]));

        yield return ("logical-uint64", Annotated("c", ParquetPhysicalType.Int64, new LogicalType.Int(64, false)),
            (w, g, c) => Write<ulong>(w, g, c, [0ul, 1ul, ulong.MaxValue, 9223372036854775808ul, 12345ul]));

        // Nullable annotated columns take a separate decode path from the
        // required ones — DecodeNullablePlainDateTimes measured 0% with only
        // required temporal columns in the corpus.
        yield return ("logical-timestamp-opt",
            AnnotatedOptional("c", ParquetPhysicalType.Int64,
                new LogicalType.Timestamp(TimeUnit.Micros, IsAdjustedToUtc: true)),
            (w, g, c) => Write<DateTime?>(w, g, c,
                [DateTime.UnixEpoch, null, new(2026, 7, 27, 1, 2, 3, DateTimeKind.Utc), null,
                 DateTime.UnixEpoch.AddTicks(5)]));

        yield return ("logical-date-opt",
            AnnotatedOptional("c", ParquetPhysicalType.Int32, new LogicalType.Date()),
            (w, g, c) => Write<DateOnly?>(w, g, c,
                [new(1970, 1, 1), null, new(2026, 7, 27), null, DateOnly.MaxValue]));

        yield return ("logical-time-opt",
            AnnotatedOptional("c", ParquetPhysicalType.Int64,
                new LogicalType.Time(TimeUnit.Micros, IsAdjustedToUtc: false)),
            (w, g, c) => Write<TimeOnly?>(w, g, c,
                [TimeOnly.MinValue, null, new(12, 34, 56), null, TimeOnly.MaxValue]));

        yield return ("logical-decimal-opt",
            AnnotatedOptional("c", ParquetPhysicalType.Int32, new LogicalType.Decimal(9, 2)),
            (w, g, c) => Write<decimal?>(w, g, c, [0m, null, 1.23m, null, -9999999.99m]));

        yield return ("logical-uint32-opt",
            AnnotatedOptional("c", ParquetPhysicalType.Int32, new LogicalType.Int(32, false)),
            (w, g, c) => Write<uint?>(w, g, c, [0u, null, uint.MaxValue, null, 12345u]));

        // ByteStreamSplit over integers and over an annotated column: the
        // float-only seeds never reached the integer or projected lanes, which
        // is where the unguarded lane indexing lived.
        yield return ("logical-timestamp-bss",
            AnnotatedEncoded("c", ParquetPhysicalType.Int64,
                new LogicalType.Timestamp(TimeUnit.Micros, IsAdjustedToUtc: true), EncodingKind.ByteStreamSplit),
            (w, g, c) => Write<DateTime>(w, g, c,
                [DateTime.UnixEpoch, DateTime.UnixEpoch.AddSeconds(1), DateTime.UnixEpoch.AddDays(1),
                 DateTime.UnixEpoch.AddTicks(10), new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc)]));

        yield return ("logical-uint32-bss",
            AnnotatedEncoded("c", ParquetPhysicalType.Int32, new LogicalType.Int(32, false),
                EncodingKind.ByteStreamSplit),
            (w, g, c) => Write<uint>(w, g, c, [0u, 1u, uint.MaxValue, 2147483648u, 12345u]));

        yield return ("logical-uint64-bss",
            AnnotatedEncoded("c", ParquetPhysicalType.Int64, new LogicalType.Int(64, false),
                EncodingKind.ByteStreamSplit),
            (w, g, c) => Write<ulong>(w, g, c, [0ul, 1ul, ulong.MaxValue, 9223372036854775808ul, 7ul]));

        yield return ("logical-decimal-bss",
            AnnotatedEncoded("c", ParquetPhysicalType.Int64, new LogicalType.Decimal(18, 4),
                EncodingKind.ByteStreamSplit),
            (w, g, c) => Write<decimal>(w, g, c, [0m, 1.2345m, -1.2345m, 99999999999999.9999m, 0.0001m]));

        yield return ("logical-uint16", Annotated("c", ParquetPhysicalType.Int32, new LogicalType.Int(16, false)),
            (w, g, c) => Write<ushort>(w, g, c, [0, 1, 65535, 32768, 12345]));
    }

    static IEnumerable<(string, ColumnDefinition, Action<ParquetWriter, RowGroupWriter, LeafColumn>)> NestedColumns()
    {
        // A top-level repeated leaf: one repetition level, no wrapping group.
        // No empty rows here — without a wrapping group there is no definition
        // level to express one, and Plank rejects it. Only LIST can be empty.
        yield return ("nested-repeated-i32",
            ColumnDefinition.Leaf("c", ParquetPhysicalType.Int32,
                new ColumnOptions(ParquetRepetition.Repeated, encodings: ImmutableArray.Create(EncodingKind.Plain))),
            (w, g, c) => WriteRows<int[]>(g, c, [[1, 2], [3], [4, 5, 6]]));

        yield return ("nested-repeated-bin",
            ColumnDefinition.Leaf("c", ParquetPhysicalType.ByteArray,
                new ColumnOptions(ParquetRepetition.Repeated, encodings: ImmutableArray.Create(EncodingKind.Plain))),
            (w, g, c) => WriteRows<byte[][]>(g, c, [[[1], [2, 2]], [[3, 3, 3]]]));

        // A LIST group: the element sits under a repeated intermediate, so the
        // leaf carries both a repetition and a definition level.
        yield return ("nested-list-i32",
            ColumnDefinition.List("c",
                ColumnDefinition.Leaf("element", ParquetPhysicalType.Int32,
                    new ColumnOptions(ParquetRepetition.Optional,
                        encodings: ImmutableArray.Create(EncodingKind.Plain)))),
            (w, g, c) => WriteRows<int?[]>(g, c, [[1, null], [], [3]]));

        yield return ("nested-list-required-i32",
            ColumnDefinition.List("c",
                ColumnDefinition.Leaf("element", ParquetPhysicalType.Int32,
                    new ColumnOptions(ParquetRepetition.Required,
                        encodings: ImmutableArray.Create(EncodingKind.Plain)))),
            (w, g, c) => WriteRows<int[]>(g, c, [[1, 2], [], [3]]));
    }

    // Nested columns are serialized per row group rather than per file.
    static void WriteRows<TRow>(RowGroupWriter group, LeafColumn column, TRow[] rows)
    {
        var serialized = group.CreateSerializedColumn<TRow>(column);
        serialized.Serialize(rows);
        group.Write(serialized);
    }

    static bool TryBuild(string name, CompressionKind compression, ColumnDefinition column,
        Action<ParquetWriter, RowGroupWriter, LeafColumn> write, out (string, byte, byte[]) file,
        ParquetDataPageVersion dataPageVersion = ParquetDataPageVersion.V2, bool writePageCrc = false,
        byte selector = FileSchemaSelector)
    {
        try
        {
            var schema = new ParquetSchema([column]);
            using var stream = new MemoryStream();
            var writer = schema.CreateWriter(stream, new ParquetWriterOptions
            {
                Compression = compression,
                DataPageVersion = dataPageVersion,
                WritePageCrc = writePageCrc
            });
            var group = writer.StartRowGroup();
            write(writer, group, schema.LeafColumns[0]);
            writer.CloseFile();
            file = (name, selector, stream.ToArray());
            return true;
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or ArgumentException)
        {
            // A codec the build does not support, or an encoding this type cannot
            // use. Skipping is right, but skipping *silently* is how the
            // Lz4Legacy gap hid: the codec is listed in Compressions(), the
            // writer cannot emit it, and every one of its cases was dropped
            // here — leaving a 326-line frame parser unseeded and nobody
            // looking, because the generator still reported a healthy count.
            Console.Error.WriteLine($"  skipped {name}: {ex.GetType().Name}: {ex.Message}");
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

    // Varying values, so a batched decoder cannot shortcut a constant run.
    static long[] Ramp(int count)
    {
        var values = new long[count];
        for (var i = 0; i < count; i++)
            values[i] = ((long)i * 2_654_435_761L) % 1_000_003L;
        return values;
    }

    // A wide dictionary needs many distinct values that also repeat. All-distinct
    // values make a dictionary pointless and the writer correctly falls back to
    // plain, which is what a first attempt at this produced.
    static long[] WideDictionary(int distinct, int repeats)
    {
        var values = new long[distinct * repeats];
        for (var i = 0; i < values.Length; i++)
            values[i] = i % distinct;
        return values;
    }

    static ColumnDefinition AnnotatedOptional(string name, ParquetPhysicalType type, LogicalType logicalType,
        EncodingKind encoding = EncodingKind.Plain)
        => ColumnDefinition.OptionalLeaf(name, type,
            new ColumnOptions(ParquetRepetition.Optional, encodings: ImmutableArray.Create(encoding)),
            logicalType);

    static ColumnDefinition AnnotatedEncoded(string name, ParquetPhysicalType type, LogicalType logicalType,
        EncodingKind encoding)
        => ColumnDefinition.RequiredLeaf(name, type,
            new ColumnOptions(encodings: ImmutableArray.Create(encoding)), logicalType);

    static ColumnDefinition Annotated(string name, ParquetPhysicalType type, LogicalType logicalType)
        => ColumnDefinition.RequiredLeaf(name, type,
            new ColumnOptions(encodings: ImmutableArray.Create(EncodingKind.Plain)), logicalType);

    static ColumnDefinition AnnotatedFixed(string name, uint length, LogicalType logicalType)
        => ColumnDefinition.RequiredLeaf(name, ParquetPhysicalType.FixedLenByteArray,
            new ColumnOptions(encodings: ImmutableArray.Create(EncodingKind.Plain), typeLength: length),
            logicalType);

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
