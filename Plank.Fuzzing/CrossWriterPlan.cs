using System.Text;
using Plank.Reading.Logical;
using Plank.Schema;
using Ps = ParquetSharp;

namespace Plank.Fuzzing;

/// <summary>
/// The file <see cref="PlankCrossWriterFuzzTarget"/> asks Apache Arrow to write:
/// its columns, its values, and the writer options that decide the shapes those
/// values end up in.
/// </summary>
/// <remarks>
/// The writer options carry most of the value here. A file's bytes are decided
/// far more by how the writer was configured than by what was put in it: whether
/// there is a page index (and so whether the reader ever has to parse a page
/// header speculatively), whether pages carry statistics, whether the values
/// went through a dictionary, which codec wrapped them, how big a page was
/// allowed to get. Each of those is a switch on the writing side and a different
/// path on the reading side.
/// </remarks>
public sealed class CrossWriterPlan
{
    internal CrossWriterPlan(IReadOnlyList<CrossWriterColumn> columns, IReadOnlyList<Array[]> rowGroups,
        CrossWriterSettings settings)
    {
        Columns = columns;
        RowGroups = rowGroups;
        Settings = settings;
    }

    public IReadOnlyList<CrossWriterColumn> Columns { get; }

    public IReadOnlyList<Array[]> RowGroups { get; }

    public CrossWriterSettings Settings { get; }

    public string Describe()
        => $"Columns=[{string.Join(", ", Columns.Select(static c => c.Describe()))}], "
           + $"RowGroups=[{string.Join(", ", RowGroups.Select(static g => g[0].Length))}], {Settings.Describe()}";

    internal Ps.WriterProperties BuildWriterProperties()
    {
        var builder = new Ps.WriterPropertiesBuilder()
            .Compression(Settings.Compression)
            .Version(Settings.Version)
            .DataPageVersion(Settings.DataPageVersion)
            .DataPagesize(Settings.DataPageSize)
            .WriteBatchSize(Settings.WriteBatchSize)
            .SetMaxStatisticsSize(Settings.MaxStatisticsSize)
            // Named so a file recovered from a crash can be traced back here
            // rather than mistaken for something a real writer produced.
            .CreatedBy("plank cross-writer fuzz target");

        builder = Settings.Statistics ? builder.EnableStatistics() : builder.DisableStatistics();
        builder = Settings.Dictionary ? builder.EnableDictionary() : builder.DisableDictionary();
        builder = Settings.PageIndex ? builder.EnableWritePageIndex() : builder.DisableWritePageIndex();
        builder = Settings.PageChecksum ? builder.EnablePageChecksum() : builder.DisablePageChecksum();
        if (Settings.Encoding is { } encoding)
            builder = builder.Encoding(encoding);

        using (builder)
            return builder.Build();
    }
}

/// <summary>The writer options a plan asks Arrow for.</summary>
public readonly record struct CrossWriterSettings(
    Ps.Compression Compression,
    Ps.ParquetVersion Version,
    Ps.ParquetDataPageVersion DataPageVersion,
    Ps.Encoding? Encoding,
    bool Statistics,
    bool Dictionary,
    bool PageIndex,
    bool PageChecksum,
    long DataPageSize,
    long WriteBatchSize,
    ulong MaxStatisticsSize)
{
    public string Describe()
        => $"{Version}/{DataPageVersion}/{Compression}/page={DataPageSize}B/batch={WriteBatchSize}"
           + $"/maxstats={MaxStatisticsSize}"
           + $"{(Encoding is { } e ? "/" + e : "")}{(Statistics ? "/stats" : "")}{(Dictionary ? "/dict" : "")}"
           + $"{(PageIndex ? "/pageindex" : "")}{(PageChecksum ? "/crc" : "")}";
}

/// <summary>One column of a plan: how to declare it, fill it, write it and check it.</summary>
public abstract class CrossWriterColumn(string name)
{
    public string Name { get; } = name;

    public abstract string Describe();

    internal abstract Ps.Column CreateColumn();

    internal abstract Array Generate(PlanCursor cursor, int rowCount);

    internal abstract void Write(Ps.ColumnWriter writer, Array values);

    internal abstract void VerifyRead(RowGroup rowGroup, LeafColumn leaf, Array expected, string where,
        CrossWriterPlan plan);

    private protected static void Fail(string where, CrossWriterPlan plan, string detail)
        => throw new PlankCrossWriterFuzzTarget.CrossWriterMismatchException(
            $"{where}: {detail}. {plan.Describe()}");
}

/// <summary>
/// A column of a value type, required or optional, written and read through the
/// same CLR type on both sides.
/// </summary>
/// <remarks>
/// Optional is not a separate class because it is not a separate column: Arrow
/// decides repetition from the nullability of the type argument, so
/// <c>Column&lt;int&gt;</c> is required and <c>Column&lt;int?&gt;</c> is
/// optional, and Plank reads them back through the matching pair.
/// </remarks>
internal sealed class ValueColumn<T>(
    string name,
    bool optional,
    Ps.LogicalType? logicalType,
    Func<PlanCursor, T> next,
    Func<T, T, bool>? equal = null) : CrossWriterColumn(name)
    where T : struct
{
    readonly Func<T, T, bool> _equal = equal ?? EqualityComparer<T>.Default.Equals;

    public override string Describe()
        => $"{Name}:{typeof(T).Name}{(optional ? "?" : "")}";

    internal override Ps.Column CreateColumn()
        => optional ? new Ps.Column<T?>(Name, logicalType) : new Ps.Column<T>(Name, logicalType);

    internal override Array Generate(PlanCursor cursor, int rowCount)
    {
        if (!optional)
        {
            var values = new T[rowCount];
            for (var i = 0; i < rowCount; i++)
                values[i] = next(cursor);
            return values;
        }

        var nullable = new T?[rowCount];
        for (var i = 0; i < rowCount; i++)
            nullable[i] = cursor.NextBool(oneIn: 4) ? null : next(cursor);
        return nullable;
    }

    internal override void Write(Ps.ColumnWriter writer, Array values)
    {
        if (optional)
            writer.LogicalWriter<T?>().WriteBatch((T?[])values);
        else
            writer.LogicalWriter<T>().WriteBatch((T[])values);
    }

    internal override void VerifyRead(RowGroup rowGroup, LeafColumn leaf, Array expected, string where,
        CrossWriterPlan plan)
    {
        if (optional)
        {
            var actual = ReadAll(rowGroup.Column<T?>(leaf));
            var want = (T?[])expected;
            if (actual.Count != want.Length)
                Fail(where, plan, $"'{Name}' read {actual.Count} values, wrote {want.Length}");
            for (var i = 0; i < want.Length; i++)
                if (want[i].HasValue != actual[i].HasValue ||
                    (want[i].HasValue && !_equal(want[i]!.Value, actual[i]!.Value)))
                    Fail(where, plan, $"'{Name}' row {i}: wrote '{Format(want[i])}', read '{Format(actual[i])}'");
            return;
        }

        var read = ReadAll(rowGroup.Column<T>(leaf));
        var written = (T[])expected;
        if (read.Count != written.Length)
            Fail(where, plan, $"'{Name}' read {read.Count} values, wrote {written.Length}");
        for (var i = 0; i < written.Length; i++)
            if (!_equal(written[i], read[i]))
                Fail(where, plan, $"'{Name}' row {i}: wrote '{written[i]}', read '{read[i]}'");
    }

    static string Format(T? value)
        => value.HasValue ? value.Value.ToString() ?? "?" : "null";

    static List<TValue> ReadAll<TValue>(RowGroupColumn<TValue> buffers)
    {
        var values = new List<TValue>();
        foreach (var buffer in buffers)
            foreach (var value in buffer.Values)
                values.Add(value);
        return values;
    }
}

/// <summary>
/// A BYTE_ARRAY column, either unannotated bytes or a UTF-8 string.
/// </summary>
/// <remarks>
/// Always optional: Arrow takes repetition from CLR nullability, and both
/// <c>string</c> and <c>byte[]</c> are reference types, so there is no way to
/// ask its column API for a required one. Plank reads either as a
/// <c>RowGroupColumn&lt;byte&gt;</c> — one span per row rather than one flat
/// value array — so a null and an empty value are distinct and have to stay so.
/// </remarks>
internal sealed class BinaryColumn(string name, bool asString) : CrossWriterColumn(name)
{
    public override string Describe()
        => $"{Name}:{(asString ? "string" : "byte[]")}?";

    internal override Ps.Column CreateColumn()
        => asString ? new Ps.Column<string?>(Name) : new Ps.Column<byte[]?>(Name);

    internal override Array Generate(PlanCursor cursor, int rowCount)
    {
        var values = new byte[]?[rowCount];
        for (var i = 0; i < rowCount; i++)
            values[i] = cursor.NextBool(oneIn: 5)
                ? null
                : asString
                    ? Encoding.UTF8.GetBytes(cursor.NextLabel())
                    : cursor.NextBytes(PlanCursor.MaxBinaryLength);
        return values;
    }

    internal override void Write(Ps.ColumnWriter writer, Array values)
    {
        var bytes = (byte[]?[])values;
        if (!asString)
        {
            writer.LogicalWriter<byte[]?>().WriteBatch(bytes);
            return;
        }

        var text = new string?[bytes.Length];
        for (var i = 0; i < bytes.Length; i++)
            text[i] = bytes[i] is null ? null : Encoding.UTF8.GetString(bytes[i]!);
        writer.LogicalWriter<string?>().WriteBatch(text);
    }

    internal override void VerifyRead(RowGroup rowGroup, LeafColumn leaf, Array expected, string where,
        CrossWriterPlan plan)
    {
        var want = (byte[]?[])expected;
        var actual = new List<byte[]?>();
        foreach (var buffer in rowGroup.Column<byte>(leaf))
            for (var i = 0; i < buffer.Count; i++)
                actual.Add(buffer.IsNull(i) ? null : buffer.GetValue(i).ToArray());

        if (actual.Count != want.Length)
            Fail(where, plan, $"'{Name}' read {actual.Count} values, wrote {want.Length}");

        for (var i = 0; i < want.Length; i++)
        {
            if (want[i] is null != actual[i] is null)
                Fail(where, plan, $"'{Name}' row {i}: wrote '{Describe(want[i])}', read '{Describe(actual[i])}'");
            if (want[i] is { } expectedBytes && !expectedBytes.AsSpan().SequenceEqual(actual[i]))
                Fail(where, plan, $"'{Name}' row {i}: wrote '{Describe(want[i])}', read '{Describe(actual[i])}'");
        }
    }

    static string Describe(byte[]? value)
        => value is null ? "null" : Convert.ToHexString(value);
}

/// <summary>A UUID column: written as a <see cref="Guid"/>, read back as bytes.</summary>
/// <remarks>
/// Not a <see cref="ValueColumn{T}"/> over <c>Guid</c>, because binding the
/// file's own schema does not give Plank a <c>Guid</c> reader. UUID is decoded
/// through a <c>ParquetValueConverter</c>, and converters come from the schema
/// the caller asks for, not from the file — so a reader that takes the file's
/// word for its schema sees a FIXED_LEN_BYTE_ARRAY and hands back the sixteen
/// bytes. That is the reader's contract rather than a defect, so the comparison
/// is made in those terms: big-endian, the byte order the UUID annotation
/// specifies.
/// </remarks>
internal sealed class UuidColumn(string name, bool optional) : CrossWriterColumn(name)
{
    public override string Describe()
        => $"{Name}:Guid{(optional ? "?" : "")}";

    internal override Ps.Column CreateColumn()
        => optional ? new Ps.Column<Guid?>(Name) : new Ps.Column<Guid>(Name);

    internal override Array Generate(PlanCursor cursor, int rowCount)
    {
        var values = new Guid?[rowCount];
        for (var i = 0; i < rowCount; i++)
            values[i] = optional && cursor.NextBool(oneIn: 4) ? null : cursor.NextGuid();
        return values;
    }

    internal override void Write(Ps.ColumnWriter writer, Array values)
    {
        var guids = (Guid?[])values;
        if (optional)
        {
            writer.LogicalWriter<Guid?>().WriteBatch(guids);
            return;
        }

        var required = new Guid[guids.Length];
        for (var i = 0; i < guids.Length; i++)
            required[i] = guids[i]!.Value;
        writer.LogicalWriter<Guid>().WriteBatch(required);
    }

    internal override void VerifyRead(RowGroup rowGroup, LeafColumn leaf, Array expected, string where,
        CrossWriterPlan plan)
    {
        var want = (Guid?[])expected;
        var actual = new List<byte[]?>();
        foreach (var buffer in rowGroup.Column<byte>(leaf))
            for (var i = 0; i < buffer.Count; i++)
                actual.Add(buffer.IsNull(i) ? null : buffer.GetValue(i).ToArray());

        if (actual.Count != want.Length)
            Fail(where, plan, $"'{Name}' read {actual.Count} values, wrote {want.Length}");

        for (var i = 0; i < want.Length; i++)
        {
            var expectedBytes = want[i]?.ToByteArray(bigEndian: true);
            if (expectedBytes is null != actual[i] is null ||
                (expectedBytes is not null && !expectedBytes.AsSpan().SequenceEqual(actual[i])))
                Fail(where, plan, $"'{Name}' row {i}: wrote '{want[i]?.ToString() ?? "null"}', "
                    + $"read '{(actual[i] is null ? "null" : Convert.ToHexString(actual[i]!))}'");
        }
    }
}
