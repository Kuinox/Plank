namespace Plank.Schema;

/// <summary>A physical leaf value in a repeated element with optional descendants.</summary>
/// <remarks>For a missing value, count the optional nodes that exist below the innermost
/// repeated node before the first missing node. For example, in a list of optional records
/// with an optional price, Null(0) means no record and Null(1) means a record with no price.</remarks>
public readonly struct NestedValue<T>
{
    readonly int _level;
    readonly T _value;

    NestedValue(T value, int level) => (_value, _level) = (value, level);

    /// <summary>True when the physical leaf has a value.</summary>
    public bool HasValue => _level < 0;

    /// <summary>The physical value of a present leaf.</summary>
    public T Value => HasValue ? _value : throw new InvalidOperationException("The leaf is null.");

    /// <summary>For a null leaf, the number of optional nodes defined beneath the repeated node.</summary>
    public int DefinedOptionalNodes => HasValue
        ? throw new InvalidOperationException("The leaf is present.")
        : _level;

    /// <summary>Creates a present physical value.</summary>
    public static NestedValue<T> Present(T value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new(value, -1);
    }

    /// <summary>Marks the first missing optional node. The default value is equivalent to Null(0).</summary>
    public static NestedValue<T> Null(int definedOptionalNodes = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(definedOptionalNodes);
        return new(default!, definedOptionalNodes);
    }
}
