using Plank.Reading;
using Plank.Reading.Logical;
using Plank.Reading.Physical;
using Plank.Schema;

namespace Plank.Tests.Reading.ParquetTesting;

/// <summary>
/// Reads a corpus file through the public reader in three independent passes and reports
/// how far each one got.
/// </summary>
/// <remarks>
/// The passes are deliberately separate rather than one drain like
/// <c>PlankReaderFuzzTarget</c> does. The fuzz target only has to answer "did this crash",
/// so it can stop at the first exception; a compatibility matrix has to distinguish
/// "the footer is unreadable" from "the footer parses but a decoder fails" from "values
/// decode but the page-index scan does not", because those are three different defects
/// and, as it turns out, the corpus contains all three.
/// </remarks>
static class ParquetTestingProbe
{
    /// <summary>
    /// Payload above which a file is treated as a stress case rather than something to
    /// decode on every test run.
    /// </summary>
    /// <remarks>
    /// One corpus file needs this: data/large_string_map.brotli.parquet is 4,325 bytes of
    /// brotli that inflate to 2,147,483,827 -- a 496,528x expansion, deliberately just over
    /// the int32 boundary. Decoding it costs about 3.5 seconds and 2 GiB of RSS, which is
    /// not a thing to do four times in a suite that otherwise runs in seconds. Nothing else
    /// in the corpus declares more than 720 KB, so the cap is not close to any real file.
    /// </remarks>
    public const long MaxDecodedBytes = 64L * 1024 * 1024;

    /// <summary>
    /// Total uncompressed payload the footer claims, or -1 when the footer will not parse.
    /// Read from the metadata rather than measured, which is the point: it is what a reader
    /// has to size its buffers from before it has decoded anything.
    /// </summary>
    public static long DeclaredUncompressedSize(byte[] file)
    {
        try
        {
            using var source = new MemoryReadSource(file);
            using var reader = new ParquetFileReader();
            reader.Reset(source);
            var total = 0L;
            foreach (var chunk in reader.Metadata.ColumnChunks)
                total += (long)chunk.TotalUncompressedSize;
            return total;
        }
        catch (Exception ex) when (ex is CorruptParquetException or NotSupportedException or InvalidOperationException)
        {
            return -1;
        }
    }

    public static bool IsStressCase(byte[] file)
        => DeclaredUncompressedSize(file) > MaxDecodedBytes;

    /// <summary>Parses the footer and binds the file's own schema.</summary>
    public static string? Open(byte[] file)
        => Run(file, static reader => _ = reader.Schema.LeafColumns.Length);

    /// <summary>Decodes every value of every leaf column of every row group.</summary>
    public static string? DecodeValues(byte[] file)
        => Run(file, static reader =>
        {
            foreach (var group in reader.RowGroups)
                foreach (var column in reader.Schema.LeafColumns)
                    DrainColumn(group, column);
        });

    /// <summary>Walks the per-page metadata a caller reaches through <see cref="ParquetColumnChunkMetadata.OpenPages"/>.</summary>
    public static string? ScanPageIndex(byte[] file)
        => Run(file, static reader =>
        {
            foreach (var group in reader.RowGroups)
                foreach (var column in reader.Schema.LeafColumns)
                {
                    using var pages = group.GetColumnMetadata(column).OpenPages();
                    for (var i = 0; i < pages.Count; i++)
                        _ = pages[i].RowCount;
                }
        });

    // Returns null on success, or the exception's "Type: message" on failure. Only the
    // exceptions the reader documents for a malformed file are caught -- anything else
    // escapes, because an IndexOutOfRangeException or a NullReferenceException from a
    // corpus file is a reader defect, not an outcome to record in a table.
    static string? Run(byte[] file, Action<ParquetReader> body)
    {
        try
        {
            using var source = new MemoryReadSource(file);
            using var reader = new ParquetReader();
            reader.Reset(source);
            body(reader);
            return null;
        }
        catch (Exception ex) when (ex is CorruptParquetException or NotSupportedException or InvalidOperationException)
        {
            return $"{ex.GetType().Name}: {ex.Message.ReplaceLineEndings(" ")}";
        }
    }

    static void DrainColumn(RowGroup group, LeafColumn column)
    {
        if (column.MaxRepetitionLevel > 0)
        {
            DrainNestedColumn(group, column);
            return;
        }

        // Reading an annotated column as its physical type skips the conversion entirely,
        // so the logical type has to pick the CLR type or the decimal, temporal and UUID
        // converters never run over any of these files.
        if (column.LogicalType is not null && TryDrainLogicalColumn(group, column))
            return;

        // A non-zero max definition level means the value can be absent, whether because
        // the leaf is optional or because an ancestor group is; the reader rejects the
        // non-nullable overload for those before decoding anything.
        var optional = column.MaxDefinitionLevel > 0;
        switch (column.PhysicalType)
        {
            case ParquetPhysicalType.Boolean:
                if (optional) Drain(group.Column<bool?>(column)); else Drain(group.Column<bool>(column));
                break;
            case ParquetPhysicalType.Int32:
                if (optional) Drain(group.Column<int?>(column)); else Drain(group.Column<int>(column));
                break;
            case ParquetPhysicalType.Int64:
                if (optional) Drain(group.Column<long?>(column)); else Drain(group.Column<long>(column));
                break;
            case ParquetPhysicalType.Float:
                if (optional) Drain(group.Column<float?>(column)); else Drain(group.Column<float>(column));
                break;
            case ParquetPhysicalType.Double:
                if (optional) Drain(group.Column<double?>(column)); else Drain(group.Column<double>(column));
                break;
            // ByteArray, FixedLenByteArray and Int96 are all read as spans of bytes.
            default:
                DrainBinary(group.Column<byte>(column));
                break;
        }
    }

    // Returns false when the annotation has no distinct CLR representation, so the caller
    // falls back to the physical type.
    static bool TryDrainLogicalColumn(RowGroup group, LeafColumn column)
    {
        var optional = column.MaxDefinitionLevel > 0;
        switch (column.LogicalType)
        {
            case LogicalType.Date:
                if (optional) Drain(group.Column<DateOnly?>(column)); else Drain(group.Column<DateOnly>(column));
                return true;
            case LogicalType.Time:
                if (optional) Drain(group.Column<TimeOnly?>(column)); else Drain(group.Column<TimeOnly>(column));
                return true;
            case LogicalType.Timestamp:
                if (optional) Drain(group.Column<DateTime?>(column)); else Drain(group.Column<DateTime>(column));
                return true;
            case LogicalType.Decimal:
                if (optional) Drain(group.Column<decimal?>(column)); else Drain(group.Column<decimal>(column));
                return true;
            case LogicalType.Uuid:
                if (optional) Drain(group.Column<Guid?>(column)); else Drain(group.Column<Guid>(column));
                return true;
            default:
                return false;
        }
    }

    static void DrainNestedColumn(RowGroup group, LeafColumn column)
    {
        switch (column.PhysicalType)
        {
            case ParquetPhysicalType.Boolean: DrainNested<bool>(group, column); break;
            case ParquetPhysicalType.Int32: DrainNested<int>(group, column); break;
            case ParquetPhysicalType.Int64: DrainNested<long>(group, column); break;
            case ParquetPhysicalType.Float: DrainNested<float>(group, column); break;
            case ParquetPhysicalType.Double: DrainNested<double>(group, column); break;
            default: DrainNested<byte>(group, column); break;
        }
    }

    // Walking the levels is what exercises the repetition-level bookkeeping; the values
    // ride along in the same buffer.
    static void DrainNested<T>(RowGroup group, LeafColumn column) where T : unmanaged
    {
        foreach (var buffer in group.NestedColumn<T>(column))
        {
            var levels = buffer.DefinitionLevels;
            for (var i = 0; i < levels.Length; i++)
                _ = levels[i];
        }
    }

    static void Drain<T>(RowGroupColumn<T> buffers)
    {
        foreach (var buffer in buffers)
        {
            var values = buffer.Values;
            for (var i = 0; i < values.Length; i++)
                _ = values[i];
        }
    }

    static void DrainBinary(RowGroupColumn<byte> buffers)
    {
        foreach (var buffer in buffers)
            for (var i = 0; i < buffer.Count; i++)
            {
                var value = buffer.GetValue(i);
                for (var j = 0; j < value.Length; j++)
                    _ = value[j];
            }
    }
}
