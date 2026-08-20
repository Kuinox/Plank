using System.Text;
using Ps = ParquetSharp;

namespace Plank.Fuzzing;

/// <summary>
/// Turns fuzzer bytes into a <see cref="CrossWriterPlan"/>.
/// </summary>
/// <remarks>
/// The mapping is positional and append-only on purpose. A saved crash input is
/// only useful if it keeps describing the same file, so new axes go on the end
/// and existing ones keep their order; reordering them would silently
/// invalidate every case in the corpus.
/// </remarks>
sealed class PlanDecoder
{
    const int MaxColumns = 4;
    const int MaxRowGroups = 3;
    const int MaxRowsPerGroup = 40;

    readonly PlanCursor _cursor;

    internal PlanDecoder(ReadOnlySpan<byte> data)
        => _cursor = new PlanCursor(data);

    internal CrossWriterPlan Decode()
    {
        var settings = DecodeSettings();
        var columnCount = _cursor.NextInt(1, MaxColumns + 1);
        var columns = new CrossWriterColumn[columnCount];
        for (var i = 0; i < columnCount; i++)
            columns[i] = DecodeColumn($"c{i}");

        var rowGroupCount = _cursor.NextInt(1, MaxRowGroups + 1);
        var rowGroups = new Array[rowGroupCount][];
        for (var g = 0; g < rowGroupCount; g++)
        {
            var rowCount = _cursor.NextInt(1, MaxRowsPerGroup + 1);
            rowGroups[g] = new Array[columnCount];
            for (var i = 0; i < columnCount; i++)
                rowGroups[g][i] = columns[i].Generate(_cursor, rowCount);
        }

        return new CrossWriterPlan(columns, rowGroups, settings);
    }

    CrossWriterSettings DecodeSettings()
        => new(
            Compression: Codecs[_cursor.NextInt(0, Codecs.Length)],
            Version: Versions[_cursor.NextInt(0, Versions.Length)],
            DataPageVersion: _cursor.NextBool(oneIn: 2)
                ? Ps.ParquetDataPageVersion.V1
                : Ps.ParquetDataPageVersion.V2,
            Encoding: DecodeEncoding(),

            // Statistics off is the interesting minority rather than the
            // default: with them on, Arrow puts a min and a max in every page
            // header, which is the shape Plank's own writer never produces.
            Statistics: !_cursor.NextBool(oneIn: 4),
            Dictionary: !_cursor.NextBool(oneIn: 3),

            // The page index is the single most consequential switch here. With
            // one in the file the reader takes every page's bounds from the
            // offset index; without one it has to grow a buffer until the page
            // header parses. Those are different decoders, and only the second
            // one meets the statistics above.
            PageIndex: !_cursor.NextBool(oneIn: 2),
            PageChecksum: _cursor.NextBool(oneIn: 3),

            // Arrow's default page size is 1 MiB and a plan writes at most 40
            // rows, so left alone every column would be a single page and the
            // multi-page paths — continuation, per-page statistics, the offset
            // index having more than one entry — would never run.
            DataPageSize: PageSizes[_cursor.NextInt(0, PageSizes.Length)],
            WriteBatchSize: BatchSizes[_cursor.NextInt(0, BatchSizes.Length)],

            // A small cap makes Arrow truncate its min/max, which sets the
            // is_min_value_exact / is_max_value_exact flags that are otherwise
            // never present.
            MaxStatisticsSize: StatisticsSizes[_cursor.NextInt(0, StatisticsSizes.Length)]);

    Ps.Encoding? DecodeEncoding()
    {
        var pick = _cursor.NextInt(0, Encodings.Length + 2);
        // Two thirds of the weight on "whatever Arrow would choose", because
        // that is what a real file looks like; the rest pins an encoding so the
        // decoders that Arrow only picks under specific conditions still come up.
        return pick >= Encodings.Length ? null : Encodings[pick];
    }

    // Lzo and Bz2 are in Arrow's enum but not in its Parquet writer, and Lz4Frame
    // is rejected outright ("Codec type lz4 not supported in Parquet format").
    // Lz4 and Lz4Hadoop are both here because they are different codes in the
    // file — LZ4_RAW and the deprecated LZ4 — and Plank decodes them differently.
    static readonly Ps.Compression[] Codecs =
    [
        Ps.Compression.Uncompressed,
        Ps.Compression.Snappy,
        Ps.Compression.Gzip,
        Ps.Compression.Brotli,
        Ps.Compression.Zstd,
        Ps.Compression.Lz4,
        Ps.Compression.Lz4Hadoop
    ];

    static readonly Ps.ParquetVersion[] Versions =
    [
        Ps.ParquetVersion.PARQUET_1_0,
        Ps.ParquetVersion.PARQUET_2_4,
        Ps.ParquetVersion.PARQUET_2_6
    ];

    static readonly Ps.Encoding[] Encodings =
    [
        Ps.Encoding.Plain,
        Ps.Encoding.DeltaBinaryPacked,
        Ps.Encoding.DeltaLengthByteArray,
        Ps.Encoding.DeltaByteArray,
        Ps.Encoding.ByteStreamSplit
    ];

    static readonly long[] PageSizes = [64, 256, 1024, 65536];

    static readonly long[] BatchSizes = [1, 7, 64, 1024];

    static readonly ulong[] StatisticsSizes = [1, 4, 16, 4096];

    CrossWriterColumn DecodeColumn(string name)
    {
        var optional = _cursor.NextBool(oneIn: 2);
        return _cursor.NextInt(0, 16) switch
        {
            0 => new ValueColumn<bool>(name, optional, null, static c => c.NextBool(oneIn: 2)),
            1 => new ValueColumn<int>(name, optional, null, static c => c.NextInt32()),
            2 => new ValueColumn<long>(name, optional, null, static c => c.NextInt64()),

            // Compared as bits, not as numbers: read back as numbers, a lost
            // sign bit on a zero and a mangled NaN payload both compare equal.
            3 => new ValueColumn<float>(name, optional, null, static c => c.NextFloat(), FloatBitsEqual),
            4 => new ValueColumn<double>(name, optional, null, static c => c.NextDouble(), DoubleBitsEqual),

            5 => new BinaryColumn(name, asString: true),
            6 => new BinaryColumn(name, asString: false),
            7 => new ValueColumn<DateOnly>(name, optional, null, static c => c.NextDate()),
            8 => new ValueColumn<DateTime>(name, optional,
                Ps.LogicalType.Timestamp(isAdjustedToUtc: true, Ps.TimeUnit.Micros),
                static c => c.NextTimestamp(Ps.TimeUnit.Micros)),
            9 => new ValueColumn<DateTime>(name, optional,
                Ps.LogicalType.Timestamp(isAdjustedToUtc: false, Ps.TimeUnit.Millis),
                static c => c.NextTimestamp(Ps.TimeUnit.Millis)),
            10 => new UuidColumn(name, optional),

            // Arrow's decimal support is a 16-byte fixed-length array with a
            // precision it fixes at 29, so only the scale is ours to choose.
            11 => new ValueColumn<decimal>(name, optional,
                Ps.LogicalType.Decimal(precision: 29, scale: 3), static c => c.NextDecimal()),

            12 => new ValueColumn<byte>(name, optional, null, static c => (byte)c.NextInt(0, 256)),
            13 => new ValueColumn<ushort>(name, optional, null, static c => (ushort)c.NextInt(0, 65536)),
            14 => new ValueColumn<uint>(name, optional, null, static c => (uint)c.NextInt32()),
            _ => new ValueColumn<ulong>(name, optional, null, static c => (ulong)c.NextInt64())
        };
    }

    static bool FloatBitsEqual(float left, float right)
        => BitConverter.SingleToInt32Bits(left) == BitConverter.SingleToInt32Bits(right);

    static bool DoubleBitsEqual(double left, double right)
        => BitConverter.DoubleToInt64Bits(left) == BitConverter.DoubleToInt64Bits(right);
}

/// <summary>Reads fuzzer bytes as the choices a plan is made of.</summary>
/// <remarks>
/// A class rather than the ref struct the other targets use, because the column
/// value generators are delegates and a ref struct cannot be captured. Input
/// wraps rather than running out, so a five-byte case still describes a whole
/// file — AFL grows inputs from tiny ones and a plan truncated to zeros would
/// waste most of them.
/// </remarks>
sealed class PlanCursor
{
    internal const int MaxBinaryLength = 24;

    readonly byte[] _data;
    int _offset;

    internal PlanCursor(ReadOnlySpan<byte> data)
        => _data = data.IsEmpty ? [0] : data.ToArray();

    internal int NextInt(int minInclusive, int maxExclusive)
    {
        var range = (uint)(maxExclusive - minInclusive);
        return range == 0 ? minInclusive : minInclusive + (int)(NextUInt32() % range);
    }

    internal bool NextBool(int oneIn)
        => NextInt(0, oneIn) == 0;

    internal int NextInt32()
        => (int)NextUInt32();

    internal long NextInt64()
        => (long)(((ulong)NextUInt32() << 32) | NextUInt32());

    // Bit patterns rather than a numeric range, so the infinities, the
    // denormals and a negative zero all come up.
    internal float NextFloat()
        => BitConverter.Int32BitsToSingle(NextInt32());

    internal double NextDouble()
        => BitConverter.Int64BitsToDouble(NextInt64());

    internal DateOnly NextDate()
        => DateOnly.FromDayNumber(NextInt(0, 100_000));

    // Kept inside the unit's representable range on purpose. A timestamp Arrow
    // would refuse to write teaches nothing, and one that overflows on the way
    // in is a bug in this target rather than in Plank.
    internal DateTime NextTimestamp(Ps.TimeUnit unit)
    {
        var ticksPerUnit = unit == Ps.TimeUnit.Millis ? TimeSpan.TicksPerMillisecond : 10L;
        var units = NextInt64() % (TimeSpan.TicksPerDay / ticksPerUnit * 70_000);
        return DateTime.UnixEpoch.AddTicks(units * ticksPerUnit);
    }

    internal Guid NextGuid()
    {
        Span<byte> bytes = stackalloc byte[16];
        for (var i = 0; i < bytes.Length; i++)
            bytes[i] = NextByte();
        return new Guid(bytes);
    }

    // Three decimal places, matching the scale the plan declares, so the value
    // survives the round trip exactly rather than being rounded by the writer.
    internal decimal NextDecimal()
        => NextInt64() % 1_000_000_000_000L / 1000m;

    internal byte[] NextBytes(int maxLength)
    {
        var bytes = new byte[NextInt(0, maxLength + 1)];
        for (var i = 0; i < bytes.Length; i++)
            bytes[i] = NextByte();
        return bytes;
    }

    // Built from scalar values rather than raw bytes: an unpaired surrogate is
    // not round-trippable through UTF-8 by any encoder, so comparing one back
    // would report a defect that is not one.
    internal string NextLabel()
    {
        var length = NextInt(0, 9);
        var builder = new StringBuilder(length);
        for (var i = 0; i < length; i++)
        {
            var pick = NextByte();
            builder.Append(pick switch
            {
                < 0x40 => (char)('a' + (pick % 26)),      // ASCII
                < 0x80 => (char)(0x00A1 + (pick % 0x40)), // two-byte UTF-8
                < 0xC0 => (char)(0x0800 + (pick % 0x40)), // three-byte UTF-8
                _ => '�'
            });
        }

        return builder.ToString();
    }

    byte NextByte()
        => _data[_offset++ % _data.Length];

    uint NextUInt32()
    {
        uint value = NextByte();
        value |= (uint)NextByte() << 8;
        value |= (uint)NextByte() << 16;
        value |= (uint)NextByte() << 24;
        return value;
    }
}
