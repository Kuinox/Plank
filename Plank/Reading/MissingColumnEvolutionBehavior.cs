namespace Plank.Reading;

/// <summary>
/// Controls how a generated multi-file row reader handles a projected column that is absent from the current file.
/// </summary>
public enum MissingColumnEvolutionBehavior
{
    /// <summary>
    /// Throw when a projected generated-schema column is not present in the current file schema.
    /// </summary>
    Reject = 0,

    /// <summary>
    /// Materialize absent projected columns as the CLR default value for the generated row property type.
    /// </summary>
    MaterializeDefault = 1
}
