using System.Buffers.Binary;

namespace Plank.Fuzzing;

/// <summary>
/// Builds the cross-writer fuzzer's seed corpus by spelling out the plans it
/// should start from.
/// </summary>
/// <remarks>
/// The target's input is a plan for a file, and <see cref="PlanDecoder"/> reads
/// that plan as a sequence of choices, four bytes each. So a seed can be
/// written rather than found: emit the choice indexes in the order the decoder
/// asks for them and the plan comes out the other side by construction.
///
/// The alternative is what this replaces — random byte strings kept when they
/// happened to decode to something interesting. Those work, but nothing in the
/// repository says what they are or how to make another, and a new axis in the
/// decoder silently turns every one of them into a different file.
///
/// Each case carries what its plan is supposed to say, and
/// CrossWriterCorpusTests checks the decoded plan against it. That is what
/// catches the drift: adding a choice ahead of an existing one shifts every
/// index after it, and the assertion fails instead of the corpus quietly
/// becoming eight copies of nothing in particular.
/// </remarks>
public static class CrossWriterCorpusGenerator
{
    // PlankWriterFuzzTarget routes an input whose first byte is this to the
    // cross-writer target.
    const byte CrossWriterMarker = 0xFC;

    /// <summary>A named seed, and the substrings its plan's description must contain.</summary>
    public readonly record struct Case(string Name, byte[] Seed, string[] Expected);

    public static int Generate(string outputDirectory)
    {
        ArgumentException.ThrowIfNullOrEmpty(outputDirectory);
        Directory.CreateDirectory(outputDirectory);

        var written = 0;
        foreach (var seed in BuildCases())
        {
            File.WriteAllBytes(Path.Combine(outputDirectory, seed.Name), seed.Seed);
            written++;
        }

        return written;
    }

    public static Case[] BuildCases()
        =>
        [
            // No offset index, so the reader has to find each page header's end
            // by parsing it rather than being told where it is.
            //
            // Statistics are off here only because turning them on is what the
            // page-header probe currently trips over, and AFL will not take a
            // seed that already fails. The fuzzer reaches that combination on its
            // own within seconds — it is better than a third of the plan space.
            Case_("crosswriter-no-page-index", ["Uncompressed", "Int64"],
                plan => plan
                    .Settings(Codec.Uncompressed, Version.V1_0, DataPageVersion.V1, Encoding.WriterChoice,
                        statistics: false, dictionary: false, pageIndex: false, pageChecksum: false,
                        PageSize.Bytes256, BatchSize.Seven, StatisticsSize.Bytes4096)
                    .Column(Kind.Int64, optional: false)
                    .Column(Kind.Boolean, optional: true)
                    .RowGroups(12, 3)),

            // And the shape that routes around it, which is what Arrow writes by
            // default and so what most real files look like.
            Case_("crosswriter-page-index", ["Snappy", "/stats", "/dict", "/pageindex"],
                plan => plan
                    .Settings(Codec.Snappy, Version.V2_6, DataPageVersion.V1, Encoding.WriterChoice,
                        statistics: true, dictionary: true, pageIndex: true, pageChecksum: false,
                        PageSize.Bytes65536, BatchSize.Kilo, StatisticsSize.Bytes4096)
                    .Column(Kind.Int32, optional: false)
                    .Column(Kind.String, optional: true)
                    .RowGroups(20)),

            // Pages small enough that a column splits across several of them,
            // which is the only way to reach page continuation, per-page
            // statistics and an offset index with more than one entry.
            Case_("crosswriter-split-pages", ["/V2/Zstd", "page=64B", "/pageindex"],
                plan => plan
                    .Settings(Codec.Zstd, Version.V2_6, DataPageVersion.V2, Encoding.WriterChoice,
                        statistics: true, dictionary: false, pageIndex: true, pageChecksum: false,
                        PageSize.Bytes64, BatchSize.Seven, StatisticsSize.Bytes16)
                    .Column(Kind.Double, optional: false)
                    .RowGroups(40, 40)),

            // Byte-stream split is the one encoding Arrow will not choose on its
            // own here, and it decodes through a separate path per width.
            Case_("crosswriter-byte-split", ["/ByteStreamSplit", "Double", "Single"],
                plan => plan
                    .Settings(Codec.Uncompressed, Version.V2_6, DataPageVersion.V1, Encoding.ByteStreamSplit,
                        statistics: true, dictionary: false, pageIndex: true, pageChecksum: true,
                        PageSize.Bytes1024, BatchSize.Sixtyfour, StatisticsSize.Bytes4096)
                    .Column(Kind.Double, optional: false)
                    .Column(Kind.Float, optional: true)
                    .RowGroups(36)),

            // Variable-length values, where the page holds an offset table
            // rather than a stride.
            Case_("crosswriter-binary", ["Gzip", "string?", "byte[]?"],
                plan => plan
                    .Settings(Codec.Gzip, Version.V1_0, DataPageVersion.V1, Encoding.Plain,
                        statistics: true, dictionary: false, pageIndex: true, pageChecksum: true,
                        PageSize.Bytes256, BatchSize.Sixtyfour, StatisticsSize.Bytes16)
                    .Column(Kind.String, optional: true)
                    .Column(Kind.Binary, optional: true)
                    .RowGroups(30, 8)),

            // Statistics truncated to four bytes, which is what sets the
            // is_min_value_exact / is_max_value_exact flags a reader otherwise
            // never sees.
            Case_("crosswriter-truncated-stats", ["Brotli", "maxstats=4", "/stats"],
                plan => plan
                    .Settings(Codec.Brotli, Version.V2_4, DataPageVersion.V2, Encoding.Plain,
                        statistics: true, dictionary: true, pageIndex: true, pageChecksum: false,
                        PageSize.Bytes1024, BatchSize.One, StatisticsSize.Bytes4)
                    .Column(Kind.String, optional: true)
                    .RowGroups(24)),

            // The annotated types, which are read through a converter rather than
            // as their physical type.
            Case_("crosswriter-annotated", ["Decimal", "Guid", "DateTime", "DateOnly"],
                plan => plan
                    .Settings(Codec.Uncompressed, Version.V2_6, DataPageVersion.V1, Encoding.Plain,
                        statistics: true, dictionary: true, pageIndex: true, pageChecksum: false,
                        PageSize.Bytes65536, BatchSize.Kilo, StatisticsSize.Bytes4096)
                    .Column(Kind.Decimal, optional: false)
                    .Column(Kind.Uuid, optional: true)
                    .Column(Kind.TimestampMicros, optional: false)
                    .Column(Kind.Date, optional: true)
                    .RowGroups(16)),

            // LZ4_RAW and the deprecated Hadoop LZ4 are different codes in the
            // file and different decoders here, so one seed each.
            Case_("crosswriter-lz4-raw", ["/Lz4/", "/crc"],
                plan => plan
                    .Settings(Codec.Lz4, Version.V2_6, DataPageVersion.V1, Encoding.Plain,
                        statistics: true, dictionary: true, pageIndex: true, pageChecksum: true,
                        PageSize.Bytes256, BatchSize.Seven, StatisticsSize.Bytes16)
                    .Column(Kind.UInt64, optional: true)
                    .RowGroups(33)),

            Case_("crosswriter-lz4-hadoop", ["Lz4Hadoop"],
                plan => plan
                    .Settings(Codec.Lz4Hadoop, Version.V1_0, DataPageVersion.V1, Encoding.Plain,
                        statistics: false, dictionary: false, pageIndex: false, pageChecksum: false,
                        PageSize.Bytes1024, BatchSize.Sixtyfour, StatisticsSize.Bytes4096)
                    .Column(Kind.Int32, optional: false)
                    .Column(Kind.TimestampMillis, optional: true)
                    .RowGroups(18, 4)),
        ];

    static Case Case_(string name, string[] expected, Func<PlanWriter, PlanWriter> build)
        => new(name, build(new PlanWriter()).ToSeed(), expected);

    // The indexes PlanDecoder maps each choice through. Named rather than
    // spelled as integers because an index into a table in another file is
    // exactly the sort of thing that goes stale without anyone noticing.
    enum Codec { Uncompressed, Snappy, Gzip, Brotli, Zstd, Lz4, Lz4Hadoop }

    enum Version { V1_0, V2_4, V2_6 }

    enum DataPageVersion { V1, V2 }

    // The decoder picks from five encodings plus two slots meaning "leave it to
    // the writer", which is what a real file usually looks like.
    enum Encoding { Plain, DeltaBinaryPacked, DeltaLengthByteArray, DeltaByteArray, ByteStreamSplit, WriterChoice }

    enum PageSize { Bytes64, Bytes256, Bytes1024, Bytes65536 }

    enum BatchSize { One, Seven, Sixtyfour, Kilo }

    enum StatisticsSize { Bytes1, Bytes4, Bytes16, Bytes4096 }

    enum Kind
    {
        Boolean, Int32, Int64, Float, Double, String, Binary, Date,
        TimestampMicros, TimestampMillis, Uuid, Decimal, Byte, UInt16, UInt32, UInt64
    }

    /// <summary>Writes the choices <see cref="PlanDecoder"/> reads, in its order.</summary>
    sealed class PlanWriter
    {
        readonly List<uint> _choices = [];
        readonly List<(Kind Kind, bool Optional)> _columns = [];
        int[] _rowCounts = [];

        internal PlanWriter Settings(Codec codec, Version version, DataPageVersion dataPageVersion,
            Encoding encoding, bool statistics, bool dictionary, bool pageIndex, bool pageChecksum,
            PageSize pageSize, BatchSize batchSize, StatisticsSize statisticsSize)
        {
            Choose(codec);
            Choose(version);
            // DataPageVersion is a one-in-two flag: V1 when it comes up true.
            Flag(dataPageVersion == DataPageVersion.V1, oneIn: 2);
            Choose(encoding);
            // Statistics, dictionary and the page index read as "off" flags, so
            // the plan asks for the negation of what it wants.
            Flag(!statistics, oneIn: 4);
            Flag(!dictionary, oneIn: 3);
            Flag(!pageIndex, oneIn: 2);
            Flag(pageChecksum, oneIn: 3);
            Choose(pageSize);
            Choose(batchSize);
            Choose(statisticsSize);
            return this;
        }

        internal PlanWriter Column(Kind kind, bool optional)
        {
            _columns.Add((kind, optional));
            return this;
        }

        internal PlanWriter RowGroups(params int[] rowCounts)
        {
            _rowCounts = rowCounts;
            return this;
        }

        internal byte[] ToSeed()
        {
            Range(_columns.Count, minInclusive: 1);
            foreach (var (kind, optional) in _columns)
            {
                Flag(optional, oneIn: 2);
                Choose(kind);
            }

            Range(_rowCounts.Length, minInclusive: 1);
            foreach (var rowCount in _rowCounts)
                Range(rowCount, minInclusive: 1);

            var seed = new byte[1 + (_choices.Count * sizeof(uint)) + FillerLength];
            seed[0] = CrossWriterMarker;
            for (var i = 0; i < _choices.Count; i++)
                BinaryPrimitives.WriteUInt32LittleEndian(seed.AsSpan(1 + (i * sizeof(uint))), _choices[i]);

            // The decoder keeps reading past the choices to fill in values, and
            // wraps when it runs out. Without a tail it would wrap onto the
            // choice bytes, which are mostly zero, and every column would hold
            // the same value. A fixed tail keeps the seeds reproducible.
            var filler = seed.AsSpan(seed.Length - FillerLength);
            for (var i = 0; i < filler.Length; i++)
                filler[i] = unchecked((byte)((i * 37) + 11));
            return seed;
        }

        const int FillerLength = 64;

        // Every choice is one four-byte read taken modulo the option count, so
        // an index below that count encodes itself.
        void Choose<T>(T value) where T : struct, Enum
            => _choices.Add(Convert.ToUInt32(value, System.Globalization.CultureInfo.InvariantCulture));

        void Range(int value, int minInclusive)
            => _choices.Add(checked((uint)(value - minInclusive)));

        // NextBool is true only on zero, so anything else is false; one is the
        // smallest value every option count leaves alone.
        void Flag(bool value, int oneIn)
            => _choices.Add(value ? 0u : 1u % (uint)oneIn);
    }
}
