using ConsoleAppFramework;
using System.Diagnostics;
using System.Reflection;
using Plank.Reading;
using Plank.Reading.Logical;
using Plank.Reading.Physical;
using Plank.Schema;
using Plank.Writing;
using Plank.Writing.PageStrategy;

namespace Plank.Tool;

internal static class EncodingProfileCommand
{
    /// <summary>Try lossless encodings, compression codecs and levels for selected columns.</summary>
    /// <param name="file">Input Parquet file.</param>
    /// <param name="columns">Comma-separated zero-based column IDs or complete paths; omitted means all columns.</param>
    /// <param name="output">Result path, default INPUT.encoding-profile.parquet.</param>
    /// <param name="encodings">Optional comma-separated encoding names; PlainDictionary is normalized to RleDictionary.</param>
    /// <param name="compressions">Optional comma-separated codec names.</param>
    /// <param name="compressionLevels">Optional comma-separated compression levels; valid levels are selected for each codec.</param>
    /// <param name="iterations">Measured write/read repetitions.</param>
    /// <param name="warmup">Untimed write/read repetitions, after mandatory correctness validation.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static int Run([Argument] string file, string? columns = null, string? output = null,
        string? encodings = null, string? compressions = null, string? compressionLevels = null,
        int iterations = 5, int warmup = 2, CancellationToken cancellationToken = default)
    {
        try
        {
            file = CommandFiles.Input(file);
            output = CommandFiles.Output(output, file, ".encoding-profile.parquet");
            if (iterations <= 0 || warmup < 0) throw new ArgumentException("Iterations must be positive and warmup non-negative.");
            using var reader = new ParquetReader(); reader.Reset(File.OpenRead(file));
            if (reader.RowGroups.Count == 0) throw new ArgumentException("The input has no row groups to profile.");
            var selected = columns is null ? reader.Schema.LeafColumns.ToArray() : columns.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Select(id => reader.Schema.LeafColumns.FirstOrDefault(c => c.Path == id) ??
                    (int.TryParse(id, out var ordinal) && ordinal >= 0 && ordinal < reader.Schema.LeafColumns.Length ? reader.Schema.LeafColumns[ordinal] : throw new ArgumentException($"Unknown column: {id}"))).Distinct().ToArray();
            if (selected.Length == 0) throw new ArgumentException("Select at least one column.");
            if (selected.Any(c => c.MaxRepetitionLevel != 0 || c.MaxDefinitionLevel > 1))
                throw new NotSupportedException("Encoding profiling currently requires flat, non-repeated columns.");
            var requestedEncodings = Names<EncodingKind>(encodings, Enum.GetValues<EncodingKind>().Where(e => e is not (EncodingKind.PlainDictionary or EncodingKind.BitPacked)))
                .Select(e => e == EncodingKind.PlainDictionary ? EncodingKind.RleDictionary : e).Distinct().ToArray();
            var codecs = Names<CompressionKind>(compressions, Enum.GetValues<CompressionKind>().Where(c => c != CompressionKind.Lz4Legacy));
            if (codecs.Contains(CompressionKind.Lz4Legacy)) throw new NotSupportedException("Writing legacy LZ4 is not supported.");
            var levels = compressionLevels?.Split(',', StringSplitOptions.TrimEntries).Select(int.Parse).Distinct().ToArray();
            var results = new List<Dictionary<string, object?>>();
            using var physical = new ParquetFileReader(); physical.Reset(File.OpenRead(file));
            foreach (var column in selected)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var type = column.PhysicalType switch
                {
                    ParquetPhysicalType.Boolean => typeof(bool), ParquetPhysicalType.Int32 => typeof(int),
                    ParquetPhysicalType.Int64 => typeof(long), ParquetPhysicalType.Float => typeof(float),
                    ParquetPhysicalType.Double => typeof(double), _ => typeof(byte[])
                };
                if (type.IsValueType && column.MaxDefinitionLevel > 0) type = typeof(Nullable<>).MakeGenericType(type);
                long originalSize = 0;
                for (var g = 0; g < physical.Metadata.RowGroupCount; g++) originalSize = checked(originalSize + (long)physical.Metadata.ColumnChunk(g, column.Ordinal).TotalCompressedSize);
                Console.Error.WriteLine($"Profiling {column.Path} ({column.PhysicalType})");
                typeof(EncodingProfileCommand).GetMethod(nameof(Profile), BindingFlags.NonPublic | BindingFlags.Static)!.MakeGenericMethod(type)
                    .Invoke(null, [reader, column, originalSize, requestedEncodings, codecs, levels, iterations, warmup, results, cancellationToken]);
            }
            if (results.Count == 0) throw new InvalidOperationException("No supported combinations were profiled.");
            ProfileOutput.Write(output, results, [
                new("column_path", ParquetPhysicalType.ByteArray), new("encoding", ParquetPhysicalType.ByteArray), new("compression", ParquetPhysicalType.ByteArray),
                new("original_size_bytes", ParquetPhysicalType.Int64), new("compressed_size_bytes", ParquetPhysicalType.Int64),
                new("write_time_ns", ParquetPhysicalType.Double), new("read_time_ns", ParquetPhysicalType.Double),
                new("compression_level", ParquetPhysicalType.Int32, true), new("physical_type", ParquetPhysicalType.ByteArray, true)],
                "encoding", "4", "Mean total write/read time across all source row groups. In-memory file. Source loading, mandatory correctness validation and warmups excluded. Sizes sum compressed column chunks including page/dictionary headers; footer, indexes and bloom filters excluded.", [
                    new("plank.profile.source.rows", reader.RowGroups.Sum(g => checked((long)g.RowCount)).ToString(System.Globalization.CultureInfo.InvariantCulture)),
                    new("plank.profile.source.row_groups", reader.RowGroups.Count.ToString()),
                    new("plank.profile.iterations", iterations.ToString()), new("plank.profile.warmup", warmup.ToString())]);
            Console.WriteLine(output); return 0;
        }
        catch (Exception error) { return CommandFiles.Error(error); }
    }

    static TEnum[] Names<TEnum>(string? text, IEnumerable<TEnum> defaults) where TEnum : struct, Enum
    {
        var values = text is null ? defaults.ToArray() : text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Select(x => Enum.Parse<TEnum>(x, true)).ToArray();
        if (values.Length == 0 || values.Any(x => !Enum.IsDefined(x))) throw new ArgumentException($"Invalid {typeof(TEnum).Name} selection.");
        return values.Distinct().ToArray();
    }
    static IEnumerable<int?> Levels(CompressionKind codec, int[]? selected)
    {
        if (codec is CompressionKind.None or CompressionKind.Snappy) return [null];
        var candidates = selected ?? (codec switch
        {
            CompressionKind.Zstd => [-5, -1, 1, 3, 6, 9, 12, 15, 19, 22],
            CompressionKind.Gzip => Enumerable.Range(0, 10).ToArray(),
            CompressionKind.Brotli => Enumerable.Range(0, 12).ToArray(),
            _ => [0, 3, 6, 9, 12]
        });
        return candidates.Where(level => codec switch
        {
            CompressionKind.Zstd => level >= ZstdSharp.Compressor.MinCompressionLevel && level <= ZstdSharp.Compressor.MaxCompressionLevel,
            CompressionKind.Gzip => level is >= 0 and <= 9, CompressionKind.Brotli => level is >= 0 and <= 11,
            CompressionKind.Lz4 => level is 0 or >= 3 and <= 12, _ => false
        }).Select(x => (int?)x);
    }

    static List<T[]> ReadGroups<T>(ParquetReader reader, int ordinal)
    {
        var groups = new List<T[]>();
        foreach (var group in reader.RowGroups)
        {
            var values = new List<T>(checked((int)group.RowCount));
            if (typeof(T) == typeof(byte[]))
                foreach (var buffer in group.Column<byte>(ordinal))
                    for (var i = 0; i < buffer.Count; i++) values.Add(buffer.IsNull(i) ? default! : (T)(object)buffer.GetValue(i).ToArray());
            else foreach (var buffer in group.Column<T>(ordinal)) values.AddRange(buffer.Values.ToArray());
            groups.Add(values.ToArray());
        }
        return groups;
    }
    static bool Same(object? a, object? b) => (a, b) switch
    {
        (byte[] x, byte[] y) => x.AsSpan().SequenceEqual(y),
        (double x, double y) => BitConverter.DoubleToInt64Bits(x) == BitConverter.DoubleToInt64Bits(y),
        (float x, float y) => BitConverter.SingleToInt32Bits(x) == BitConverter.SingleToInt32Bits(y),
        _ => Equals(a, b)
    };
    static long Consume<T>(MemoryStream memory)
    {
        memory.Position = 0;
        using var reader = new ParquetReader(); reader.Reset(new StreamReadSource(memory));
        long count = 0;
        foreach (var group in reader.RowGroups)
        {
            if (typeof(T) == typeof(byte[])) foreach (var buffer in group.Column<byte>(0)) count += buffer.Count;
            else foreach (var buffer in group.Column<T>(0)) count += buffer.Count;
        }
        return count;
    }
    static void Write<T>(MemoryStream memory, ParquetSchema schema, List<T[]> groups, CompressionKind codec, int? level)
    {
        memory.SetLength(0); memory.Position = 0;
        using var writer = schema.CreateWriter(new ToolWriteSource(memory, leaveOpen: true), new ParquetWriterOptions { Compression = codec, CompressionLevel = level,
            InitialColumnBufferBytes = 1024 * 1024, WritePageIndexes = false });
        foreach (var values in groups)
        {
            var group = writer.StartRowGroup();
            var serialized = group.CreateSerializedColumn<T>(schema.LeafColumns[0]);
            serialized.Serialize(values); group.Write(serialized);
        }
        writer.CloseFile();

    }
    static void Profile<T>(ParquetReader source, LeafColumn column, long originalSize, EncodingKind[] encodings,
        CompressionKind[] codecs, int[]? selectedLevels, int iterations, int warmup,
        List<Dictionary<string, object?>> results, CancellationToken token)
    {
        var groups = ReadGroups<T>(source, column.Ordinal);
        var totalRows = groups.Sum(x => (long)x.Length);
        foreach (var encoding in encodings)
        {
            ParquetSchema schema;
            try
            {
            var options = new ColumnOptions(encodings: [encoding], typeLength: column.Options.TypeLength);
            var definition = column.MaxDefinitionLevel > 0
                ? ColumnDefinition.OptionalLeaf(column.Path, column.PhysicalType, options, pageStrategy: encoding == EncodingKind.RleDictionary ? ForceDictionaryPageStrategy.Shared : null)
                : ColumnDefinition.RequiredLeaf(column.Path, column.PhysicalType, options, pageStrategy: encoding == EncodingKind.RleDictionary ? ForceDictionaryPageStrategy.Shared : null);
            schema = new ParquetSchema([definition]);
            }
            catch (NotSupportedException error) { Console.Error.WriteLine($"Skipping {encoding}: {error.Message}"); continue; }
            foreach (var codec in codecs)
            foreach (var level in Levels(codec, selectedLevels))
            {
                token.ThrowIfCancellationRequested();
                using var memory = new MemoryStream();
                try { Write(memory, schema, groups, codec, level); }
                catch (Exception error) when (error is NotSupportedException or ArgumentException)
                { Console.Error.WriteLine($"Skipping {encoding}/{codec}/{level}: {error.Message}"); continue; }
                memory.Position = 0;
                using var validation = new ParquetReader(); validation.Reset(new StreamReadSource(memory));
                var actual = ReadGroups<T>(validation, 0);
                if (actual.Count != groups.Count || actual.Where((x, i) => x.Length != groups[i].Length || x.Where((v, j) => !Same(v, groups[i][j])).Any()).Any())
                    throw new InvalidDataException($"Round-trip mismatch: {column.Path}/{encoding}/{codec}/{level}");
                using var physical = new ParquetFileReader(); physical.Reset(new StreamReadSource(memory));
                long size = 0; var used = new HashSet<EncodingKind>();
                for (var g = 0; g < physical.Metadata.RowGroupCount; g++)
                {
                    size = checked(size + (long)physical.Metadata.ColumnChunk(g, 0).TotalCompressedSize);
                    using var cursor = physical.OpenPages(g, 0);
                    while (cursor.MoveNext()) if (cursor.CurrentHeader.Type is PageHeaderType.DataPage or PageHeaderType.DataPageV2)
                        used.Add(cursor.CurrentHeader.Encoding == EncodingKind.PlainDictionary ? EncodingKind.RleDictionary : cursor.CurrentHeader.Encoding);
                }
                if (used.Any(x => x != encoding)) { Console.Error.WriteLine($"Skipping {encoding}: writer used {string.Join(',', used)}."); continue; }
                for (var i = 0; i < warmup; i++) { token.ThrowIfCancellationRequested(); Write(memory, schema, groups, codec, level); _ = Consume<T>(memory); }
                double write = 0, read = 0;
                for (var i = 0; i < iterations; i++)
                {
                    token.ThrowIfCancellationRequested();
                    var start = Stopwatch.GetTimestamp(); Write(memory, schema, groups, codec, level); write += Stopwatch.GetElapsedTime(start).TotalNanoseconds;
                    start = Stopwatch.GetTimestamp(); var count = Consume<T>(memory); read += Stopwatch.GetElapsedTime(start).TotalNanoseconds;
                    if (count != totalRows) throw new InvalidDataException("Measured read returned an inconsistent row count.");
                }
                results.Add(new() { ["column_path"] = column.Path, ["encoding"] = encoding.ToString(), ["compression"] = codec.ToString(),
                    ["original_size_bytes"] = originalSize, ["compressed_size_bytes"] = size,
                    ["write_time_ns"] = write / iterations, ["read_time_ns"] = read / iterations, ["compression_level"] = level,
                    ["physical_type"] = column.PhysicalType.ToString() });
            }
        }
    }
}
