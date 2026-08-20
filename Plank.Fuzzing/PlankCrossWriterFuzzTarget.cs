using System.Text;
using Plank.Reading.Logical;
using Plank.Schema;
using Ps = ParquetSharp;

namespace Plank.Fuzzing;

/// <summary>
/// Writes a file with Apache Arrow's Parquet writer and requires Plank to read
/// it back, value for value.
/// </summary>
/// <remarks>
/// Every other target in this project writes with Plank. That makes the reader
/// fuzzer blind in a specific way: the only well-formed files it ever sees are
/// the ones Plank itself produces, so any part of the format Plank's writer does
/// not emit is reachable only by a mutation inventing it — which, for anything
/// structural, does not happen. Page-header statistics are the worked example:
/// <c>WriteDataPageHeader</c> takes no statistics, so no generated seed carries
/// them, and the reader's header probe mis-detected truncation on the ones that
/// do. Twenty-six files in apache/parquet-testing failed on it. No amount of
/// fuzzing Plank-written bytes would have found it.
///
/// The other half of the blindness is the oracle. The reader target treats
/// <see cref="Plank.Reading.CorruptParquetException"/> as a pass, because it is
/// fed deliberately mangled bytes and rejecting them is the correct answer. So
/// even a corpus that did contain such a file would report nothing: the reader
/// would throw, the target would swallow it, and the fleet would stay green.
///
/// Here the input is not a file. It is a plan for one, and the file is built
/// from that plan by a conformant writer, so it is valid by construction and
/// there is no such thing as an acceptable failure. Plank has to open it, walk
/// its page metadata, and return the values that went in. Anything else — any
/// exception, any mismatch — is a defect in Plank.
///
/// What the fuzzer explores is therefore Arrow's writer option space rather than
/// a byte space: page index on or off, statistics on or off, dictionary, codec,
/// format version, data page version, page size, checksums, statistics
/// truncation. Those combinations decide what shapes end up in the file, and
/// Plank has to handle all of them.
/// </remarks>
public static class PlankCrossWriterFuzzTarget
{
    const int MaxColumns = 4;
    const int MaxRowGroups = 3;
    const int MaxRowsPerGroup = 40;
    const int MaxBinaryLength = 24;

    public static void Execute(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
            return;

        var plan = Decode(data);
        var file = TryWrite(plan);

        // Arrow refusing to write the plan is not a Plank defect. It is also the
        // only failure this target tolerates, and it is confined to the write
        // call so a rejection cannot mask anything on the read side.
        if (file is null)
            return;

        Verify(plan, file);
    }

    public static CrossWriterPlan Decode(ReadOnlySpan<byte> data)
        => new PlanDecoder(data).Decode();

    static byte[]? TryWrite(CrossWriterPlan plan)
    {
        try
        {
            return Write(plan);
        }
        catch (Ps.ParquetException)
        {
            return null;
        }
    }

    static byte[] Write(CrossWriterPlan plan)
    {
        using var stream = new MemoryStream();
        using var properties = plan.BuildWriterProperties();
        var columns = new Ps.Column[plan.Columns.Count];
        for (var i = 0; i < columns.Length; i++)
            columns[i] = plan.Columns[i].CreateColumn();

        using (var writer = new Ps.ParquetFileWriter(stream, columns, null, properties, null, leaveOpen: true))
        {
            foreach (var rowGroup in plan.RowGroups)
            {
                using var groupWriter = writer.AppendRowGroup();
                for (var i = 0; i < plan.Columns.Count; i++)
                {
                    using var columnWriter = groupWriter.NextColumn();
                    plan.Columns[i].Write(columnWriter, rowGroup[i]);
                }
            }

            writer.Close();
        }

        return stream.ToArray();
    }

    /// <summary>Runs every check a conformant file has to survive.</summary>
    /// <remarks>
    /// Nothing here catches. A file Arrow wrote is well formed, so the reader
    /// throwing at all is the finding — which is the whole difference between
    /// this target and the byte-level reader fuzzer.
    /// </remarks>
    public static void Verify(CrossWriterPlan plan, byte[] file)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(file);

        VerifyValues(plan, file, pagePruner: null);
        VerifyPageMetadata(plan, file);

        // A pruner is the second way into the page-header probe, and it is the
        // one that reaches it on an ordinary read rather than through the
        // metadata API: ColumnBufferEnumerable takes it and then has to know
        // each page's bounds before it can decide to skip one. An accept-all
        // pruner changes no results, so the same value comparison applies.
        VerifyValues(plan, file, pagePruner: static (in ParquetDataPageMetadata _) => true);
    }

    static void VerifyValues(CrossWriterPlan plan, byte[] file, ParquetPagePruner? pagePruner)
    {
        var where = pagePruner is null ? "read" : "pruned read";
        using var reader = new ParquetReader();
        reader.Reset(new MemoryStream(file, writable: false), pagePruner);

        var leaves = reader.Schema.LeafColumns;
        if (leaves.Length != plan.Columns.Count)
            throw new CrossWriterMismatchException(
                $"{where}: schema has {leaves.Length} leaf columns, wrote {plan.Columns.Count}. {plan.Describe()}");

        var rowGroupIndex = 0;
        foreach (var rowGroup in reader.RowGroups)
        {
            if (rowGroupIndex >= plan.RowGroups.Count)
                throw new CrossWriterMismatchException(
                    $"{where}: more row groups than were written. {plan.Describe()}");

            for (var i = 0; i < plan.Columns.Count; i++)
                plan.Columns[i].VerifyRead(rowGroup, leaves[i], plan.RowGroups[rowGroupIndex][i],
                    $"{where} row group {rowGroupIndex} column {i}", plan);
            rowGroupIndex++;
        }

        if (rowGroupIndex != plan.RowGroups.Count)
            throw new CrossWriterMismatchException(
                $"{where}: read {rowGroupIndex} row groups, wrote {plan.RowGroups.Count}. {plan.Describe()}");
    }

    /// <summary>
    /// Walks the page metadata of every chunk, which is where the header probe
    /// lives.
    /// </summary>
    /// <remarks>
    /// Reading values does not always get here: with a page index in the file the
    /// reader takes each page's bounds from the offset index and never parses a
    /// page header speculatively. Without one it has to grow a buffer until the
    /// header parses, and that is the code a foreign writer's statistics reach.
    /// So this runs unconditionally and touches the statistics it finds, rather
    /// than only when the values pass.
    /// </remarks>
    static void VerifyPageMetadata(CrossWriterPlan plan, byte[] file)
    {
        using var reader = new ParquetReader();
        reader.Reset(new MemoryStream(file, writable: false));

        foreach (var rowGroup in reader.RowGroups)
            foreach (var leaf in reader.Schema.LeafColumns)
            {
                var metadata = rowGroup.GetColumnMetadata(leaf);
                using var pages = metadata.OpenPages();
                ulong rows = 0;
                var counted = pages.Count > 0;
                for (var i = 0; i < pages.Count; i++)
                {
                    var page = pages[i];
                    // A page's row count is only known when the file says so —
                    // through the offset index, or through a v2 header. Summing
                    // partial counts would invent a mismatch, so the check is
                    // skipped rather than guessed at.
                    if (page.RowCount is { } rowCount)
                        rows += rowCount;
                    else
                        counted = false;
                    Consume(page.Statistics.Minimum);
                    Consume(page.Statistics.Maximum);
                }

                if (counted && rows != rowGroup.RowCount)
                    throw new CrossWriterMismatchException(
                        $"page metadata for '{leaf.Path}' accounts for {rows} rows, "
                        + $"the row group holds {rowGroup.RowCount}. {plan.Describe()}");
            }
    }

    static void Consume(ReadOnlySpan<byte> bytes)
    {
        for (var i = 0; i < bytes.Length; i++)
            _ = bytes[i];
    }

    /// <summary>Signals that Plank did not read back what Arrow wrote.</summary>
    public sealed class CrossWriterMismatchException(string message) : Exception(message);
}
