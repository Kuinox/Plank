namespace Plank.Writing.Encoding;

/// <summary>
/// Reads presence and value out of an optional row. C# cannot express "nullable" over both value and
/// reference types, which is why every optional helper used to exist twice - once constrained to
/// <c>struct</c> over <c>T?</c> and once to <c>class</c> over <c>T</c>, with identical bodies.
/// Implementations are structs and every member is static, so each instantiation devirtualizes and
/// inlines exactly as the hand-written pair did.
/// </summary>
interface IOptionalRow<TRow, out TValue>
{
    /// <summary>
    /// True when the row always carries a value, so presence never has to be tested. This is a JIT
    /// constant per instantiation, letting shared loops keep the bulk paths the required-only
    /// copies used to hand-write.
    /// </summary>
    static abstract bool ValueRequired { get; }

    static abstract bool IsPresent(in TRow row);

    static abstract TValue GetValue(in TRow row);
}

readonly struct NullableValueRow<T> : IOptionalRow<T?, T>
    where T : struct
{
    public static bool ValueRequired => false;

    public static bool IsPresent(in T? row)
        => row.HasValue;

    public static T GetValue(in T? row)
        => row!.Value;
}

readonly struct ReferenceRow<T> : IOptionalRow<T, T>
    where T : class
{
    public static bool ValueRequired => false;

    public static bool IsPresent(in T row)
        => row is not null;

    public static T GetValue(in T row)
        => row;
}

/// <summary>A row of a column whose values are never absent.</summary>
readonly struct RequiredRow<T> : IOptionalRow<T, T>
    where T : notnull
{
    public static bool ValueRequired => true;

    public static bool IsPresent(in T row)
        => true;

    public static T GetValue(in T row)
        => row;
}
