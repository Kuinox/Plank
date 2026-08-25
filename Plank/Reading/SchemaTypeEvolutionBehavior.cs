namespace Plank.Reading;

/// <summary>
/// Controls how a generated multi-file row reader handles physical, logical, or materialized type changes.
/// </summary>
public enum SchemaTypeEvolutionBehavior
{
    /// <summary>
    /// Throw when the type shape differs from the generated schema.
    /// </summary>
    Reject = 0,

    /// <summary>
    /// Allow only type shapes that Plank can prove are compatible with the generated row property type.
    /// </summary>
    AllowCompatible = 1
}
