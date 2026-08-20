namespace Plank.Tests.Reading.ParquetTesting;

/// <summary>
/// Records exactly how much of the apache/parquet-testing corpus Plank can read today, and
/// fails when that changes in either direction.
/// </summary>
/// <remarks>
/// A file absent from <see cref="KnownGaps"/> is expected to open, decode every value and
/// scan its page index cleanly, so a newly added upstream file that Plank cannot read shows
/// up as a failure rather than passing silently. A file listed here is expected to keep
/// failing exactly the passes it is recorded as failing -- so fixing one of these bugs also
/// fails the suite, with a message saying to delete the entry. That is deliberate: the point
/// of the table is to be deleted a line at a time.
///
/// The failure messages are not asserted, only the pass/fail shape. Most of them quote byte
/// counts out of the specific file, which would turn every upstream corpus bump into a
/// diff of noise.
/// </remarks>
internal sealed class ParquetTestingCompatibilityTests
{
    [Test]
    public void SubmoduleIsCheckedOut()
    {
        if (!ParquetTestingCorpus.IsAvailable)
            throw new InvalidOperationException(ParquetTestingCorpus.MissingMessage);
    }

    [Test]
    [MethodDataSource(nameof(DataFiles))]
    public void DataFile_MatchesRecordedOutcome(string relativePath)
    {
        var file = ParquetTestingCorpus.ReadAllBytes(relativePath);
        var expected = KnownGaps.GetValueOrDefault(relativePath, ReadsCleanly);

        Check(relativePath, "open", expected.Opens, ParquetTestingProbe.Open(file));

        // A file whose footer will not parse cannot be asked about its columns, so the
        // later passes only mean anything once the first one succeeds.
        if (!expected.Opens)
            return;

        // Decoding a file that declares gigabytes is a stress case, not a compatibility
        // check; LargeStringMap_ReportsPayloadAboveInt32Range covers the one file that is.
        if (!ParquetTestingProbe.IsStressCase(file))
            Check(relativePath, "decode values", expected.DecodesValues, ParquetTestingProbe.DecodeValues(file));

        Check(relativePath, "scan page index", expected.ScansPageIndex, ParquetTestingProbe.ScanPageIndex(file));
    }

    /// <summary>
    /// The corpus's one decompression bomb, checked for the property it exists to test:
    /// 4,325 bytes of brotli declaring 2,147,483,827 bytes of payload, which is deliberately
    /// just past int.MaxValue. A reader that accumulates chunk sizes into an int reports a
    /// negative total here; Plank's metadata is unsigned throughout, so this asserts the
    /// size survives the footer intact. The 2 GiB is never decoded.
    /// </summary>
    [Test]
    public void LargeStringMap_ReportsPayloadAboveInt32Range()
    {
        if (!ParquetTestingCorpus.IsAvailable)
            throw new InvalidOperationException(ParquetTestingCorpus.MissingMessage);

        const string path = "data/large_string_map.brotli.parquet";
        var declared = ParquetTestingProbe.DeclaredUncompressedSize(ParquetTestingCorpus.ReadAllBytes(path));

        if (declared <= int.MaxValue)
            throw new InvalidOperationException(
                $"{path}: expected a declared payload above int.MaxValue but got {declared}.");
    }

    /// <summary>
    /// Every file in <see cref="KnownGaps"/> must still exist upstream, so an entry does not
    /// quietly stop covering anything when the corpus is bumped.
    /// </summary>
    [Test]
    public void KnownGapsAllNameAnExistingFile()
    {
        if (!ParquetTestingCorpus.IsAvailable)
            throw new InvalidOperationException(ParquetTestingCorpus.MissingMessage);

        var present = ParquetTestingCorpus.DataFiles().ToHashSet(StringComparer.Ordinal);
        var stale = KnownGaps.Keys.Where(path => !present.Contains(path)).ToArray();
        if (stale.Length > 0)
            throw new InvalidOperationException(
                $"KnownGaps names {stale.Length} file(s) that no longer exist in the corpus: {string.Join(", ", stale)}. Remove the entries.");
    }

    static void Check(string relativePath, string pass, bool expectedToSucceed, string? failure)
    {
        var succeeded = failure is null;
        if (succeeded == expectedToSucceed)
            return;

        throw new InvalidOperationException(succeeded
            ? $"{relativePath}: the '{pass}' pass now succeeds but is recorded as a known gap. Update KnownGaps in {nameof(ParquetTestingCompatibilityTests)}."
            : $"{relativePath}: the '{pass}' pass was expected to succeed but failed with {failure}");
    }

    public static string[] DataFiles()
        => ParquetTestingCorpus.IsAvailable ? ParquetTestingCorpus.DataFiles() : [];

    sealed record Outcome(bool Opens, bool DecodesValues, bool ScansPageIndex, string Cause);

    static readonly Outcome ReadsCleanly = new(Opens: true, DecodesValues: true, ScansPageIndex: true, Cause: "");

    // Shorthands for the four shapes the gaps actually take.
    static Outcome PageIndexOnly(string cause) => new(true, true, false, cause);
    static Outcome ValuesOnly(string cause) => new(true, false, true, cause);
    static Outcome ValuesAndPageIndex(string cause) => new(true, false, false, cause);
    static Outcome FooterRejected(string cause) => new(false, false, false, cause);

    // Grouped by root cause rather than alphabetically, because these are a handful of
    // defects rather than 46 unrelated ones, and the grouping is what says so.
    static readonly Dictionary<string, Outcome> KnownGaps = new(StringComparer.Ordinal)
    {
        // Same probe, reached from the other side: enough of a truncated header parses as
        // a well-formed struct that the loop returns garbage instead of retrying, and the
        // caller rejects the nonsense it was handed.
        ["data/datapage_v1-corrupt-checksum.parquet"] = PageIndexOnly("page-header probe: partial header parses"),
        ["data/datapage_v1-snappy-compressed-checksum.parquet"] = PageIndexOnly("page-header probe: partial header parses"),
        ["data/datapage_v1-uncompressed-checksum.parquet"] = PageIndexOnly("page-header probe: partial header parses"),

        // ---------------------------------------------------------------------------
        // 2. CompactProtocolReader.Skip does not know the DOUBLE wire type, so it cannot
        //    step over an unknown field carrying one. The GEOMETRY/GEOGRAPHY statistics
        //    the geospatial files put in their footers are exactly that, and the footer
        //    parse dies on a field Plank is otherwise free to ignore.
        // ---------------------------------------------------------------------------
        ["data/geospatial/crs-arbitrary-value.parquet"] = FooterRejected("compact protocol: cannot skip DOUBLE"),
        ["data/geospatial/crs-default.parquet"] = FooterRejected("compact protocol: cannot skip DOUBLE"),
        ["data/geospatial/crs-projjson.parquet"] = FooterRejected("compact protocol: cannot skip DOUBLE"),
        ["data/geospatial/crs-srid.parquet"] = FooterRejected("compact protocol: cannot skip DOUBLE"),
        ["data/geospatial/geography-lines.parquet"] = FooterRejected("compact protocol: cannot skip DOUBLE"),
        ["data/geospatial/geography-points.parquet"] = FooterRejected("compact protocol: cannot skip DOUBLE"),
        ["data/geospatial/geography-polygons.parquet"] = FooterRejected("compact protocol: cannot skip DOUBLE"),
        ["data/geospatial/geospatial-with-nan.parquet"] = FooterRejected("compact protocol: cannot skip DOUBLE"),
        ["data/geospatial/geospatial.parquet"] = FooterRejected("compact protocol: cannot skip DOUBLE"),

        // ---------------------------------------------------------------------------
        // 3. Decoder defects. Each of these is a distinct bug reachable from the public
        //    reader on a file a mainstream writer produced.
        // ---------------------------------------------------------------------------

        // The canonical DELTA_BINARY_PACKED conformance file: 65 columns, one per bit
        // width from 0 to 64, with delta_binary_packed_expect.csv as ground truth.
        // ParquetTestingDeltaEncodingTests covers what it should decode to.
        ["data/delta_binary_packed.parquet"] = ValuesOnly("DELTA_BINARY_PACKED: 'Unexpected end of delta-binary-packed mini-block'"),

        // The DELTA_BYTE_ARRAY companion. The block size it reads (26888794261) is not a
        // plausible header field, so the decoder is starting from the wrong offset.
        ["data/delta_byte_array.parquet"] = ValuesOnly("DELTA_BYTE_ARRAY: reads an implausible block size"),

        // Multi-member gzip streams. The file exists upstream precisely because writers
        // concatenate members and readers are expected to inflate all of them; Plank
        // inflates the first and rejects the rest as trailing bytes.
        ["data/concatenated_gzip_members.parquet"] = ValuesOnly("gzip: only the first member is inflated"),

        // Optional PLAIN INT32 columns whose page holds fewer values than the header's
        // value count, because the nulls do not occupy payload. Plank sizes the payload
        // from the value count instead of the non-null count.
        ["data/nullable.impala.parquet"] = ValuesOnly("PLAIN: payload sized from value count, not non-null count"),
        ["data/nulls.snappy.parquet"] = new(true, false, true, "PLAIN: payload sized from value count, not non-null count"),

        // A TIMESTAMP(NANOS) that fits a DateTime comfortably (2020-12-24) is rejected by
        // the scaling bound check.
        ["data/nested_structs.rust.parquet"] = ValuesOnly("TIMESTAMP(NANOS): bound check rejects an in-range value"),

        // ---------------------------------------------------------------------------
        // 4. Footer strictness. Plank refuses files other implementations accept.
        // ---------------------------------------------------------------------------

        // A sorting_columns field Plank insists must be a list of structs.
        ["data/dict-page-offset-zero.parquet"] = FooterRejected("footer: sorting_columns shape rejected"),

        // Plank requires a column annotated with an unknown logical type to be optional.
        // Both files are valid and both are read by arrow.
        ["data/null_list.parquet"] = FooterRejected("schema: UNKNOWN logical type required to be optional"),

        // A page header longer than the 64 KiB probe window -- the same probe as group 1,
        // failing at the other end of its loop.
        ["data/column_chunk_key_value_metadata.parquet"] = ValuesAndPageIndex("page-header probe: exceeds the 64 KiB window"),

        // ---------------------------------------------------------------------------
        // 5. Files that are malformed on purpose. Rejecting them is correct; they are
        //    listed so the suite notices if the rejection ever turns into a crash.
        // ---------------------------------------------------------------------------
        ["data/incorrect_map_schema.parquet"] = FooterRejected("intentionally malformed: MAP key is optional"),
        ["data/nation.dict-malformed.parquet"] = ValuesAndPageIndex("intentionally malformed: page size exceeds the chunk"),
    };
}
