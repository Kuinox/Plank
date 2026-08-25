using System.Runtime.CompilerServices;
using Plank.Schema;

namespace Plank.Writing.Encoding;

/// <summary>
/// Reads the payload out of one row of a BYTE_ARRAY column. A byte-array page reaches the encoders
/// in four shapes - required or optional, carried as <c>byte[]</c> or
/// <see cref="ReadOnlyMemory{T}"/> - and every encoder used to carry a hand-written copy of its
/// loops for each one. Implementations are structs with only static members, so each instantiation
/// devirtualizes to the same code the hand-written copies compiled to.
/// </summary>
interface IByteArrayRow<TRow>
{
    /// <summary>
    /// True when the column cannot hold nulls, so a missing row is an error rather than a value to
    /// skip. This is a JIT constant per instantiation, so the required shapes fold away the
    /// presence bookkeeping entirely.
    /// </summary>
    static abstract bool ValueRequired { get; }

    static abstract bool IsPresent(in TRow row);

    static abstract ReadOnlySpan<byte> GetSpan(in TRow row);
}

readonly struct RequiredByteArrayRow : IByteArrayRow<byte[]>
{
    public static bool ValueRequired => true;

    public static bool IsPresent(in byte[] row) => row is not null;

    public static ReadOnlySpan<byte> GetSpan(in byte[] row) => row;
}

readonly struct OptionalByteArrayRow : IByteArrayRow<byte[]>
{
    public static bool ValueRequired => false;

    public static bool IsPresent(in byte[] row) => row is not null;

    public static ReadOnlySpan<byte> GetSpan(in byte[] row) => row;
}

readonly struct RequiredMemoryRow : IByteArrayRow<ReadOnlyMemory<byte>>
{
    public static bool ValueRequired => true;

    public static bool IsPresent(in ReadOnlyMemory<byte> row) => true;

    public static ReadOnlySpan<byte> GetSpan(in ReadOnlyMemory<byte> row) => row.Span;
}

readonly struct OptionalMemoryRow : IByteArrayRow<ReadOnlyMemory<byte>?>
{
    public static bool ValueRequired => false;

    public static bool IsPresent(in ReadOnlyMemory<byte>? row) => row.HasValue;

    public static ReadOnlySpan<byte> GetSpan(in ReadOnlyMemory<byte>? row) => row.GetValueOrDefault().Span;
}

static class ByteArrayRows
{
    /// <summary>
    /// Fetches the payload of one row, or reports it absent. Rows of a required column are never
    /// absent, so a null there throws instead.
    /// </summary>
    /// <remarks>
    /// The <c>typeof(TRow) == typeof(byte[])</c> tests are JIT constants per instantiation, so the
    /// <c>byte[]</c> instantiations compile to a plain null check and array access rather than a
    /// static-abstract dispatch, which does not inline under <c>__Canon</c> shared generics.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool TryGetPayload<TRow, TRowAccess>(Column column, in TRow row,
        out ReadOnlySpan<byte> payload)
        where TRowAccess : IByteArrayRow<TRow>
    {
        if (typeof(TRow) == typeof(byte[]))
        {
            var value = Unsafe.As<TRow, byte[]>(ref Unsafe.AsRef(in row));
            if (value is null)
            {
                if (TRowAccess.ValueRequired)
                    ThrowNullValue(column);
                payload = default;
                return false;
            }

            payload = value;
            return true;
        }

        if (!TRowAccess.IsPresent(in row))
        {
            payload = default;
            return false;
        }

        payload = TRowAccess.GetSpan(in row);
        return true;
    }

    /// <summary>Rows carrying a value. Folds to the row count for the required shapes.</summary>
    internal static int CountPresent<TRow, TRowAccess>(ReadOnlySpan<TRow> rows)
        where TRowAccess : IByteArrayRow<TRow>
    {
        if (TRowAccess.ValueRequired)
            return rows.Length;

        var count = 0;
        for (var i = 0; i < rows.Length; i++)
            if (typeof(TRow) == typeof(byte[])
                    ? Unsafe.As<TRow, byte[]>(ref Unsafe.AsRef(in rows[i])) is not null
                    : TRowAccess.IsPresent(in rows[i]))
                count++;
        return count;
    }

    internal static void ThrowNullValue(Column column)
        => throw new InvalidOperationException($"Column '{column.Name}' does not support null values.");
}
