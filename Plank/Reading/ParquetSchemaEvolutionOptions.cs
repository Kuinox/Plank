namespace Plank.Reading;

/// <summary>
/// Controls schema evolution tolerance for source-generated row readers reused across multiple files.
/// </summary>
public sealed class ParquetSchemaEvolutionOptions
{
    /// <summary>
    /// Gets a strict schema evolution policy.
    /// </summary>
    public static ParquetSchemaEvolutionOptions Default { get; } = new();

    /// <summary>
    /// Gets or initializes how projected generated-schema columns that are absent from the current file are handled.
    /// </summary>
    public MissingColumnEvolutionBehavior MissingColumns { get; init; } = MissingColumnEvolutionBehavior.Reject;

    /// <summary>
    /// Gets or initializes how required/optional repetition changes are handled.
    /// </summary>
    public RepetitionEvolutionBehavior Repetition { get; init; } = RepetitionEvolutionBehavior.Reject;

    /// <summary>
    /// Gets or initializes how physical Parquet type changes are handled.
    /// </summary>
    public SchemaTypeEvolutionBehavior PhysicalTypes { get; init; } = SchemaTypeEvolutionBehavior.Reject;

    /// <summary>
    /// Gets or initializes how logical Parquet type changes are handled.
    /// </summary>
    public SchemaTypeEvolutionBehavior LogicalTypes { get; init; } = SchemaTypeEvolutionBehavior.Reject;

    /// <summary>
    /// Gets or initializes how generated CLR row property type materialization changes are handled.
    /// </summary>
    public SchemaTypeEvolutionBehavior MaterializedTypes { get; init; } = SchemaTypeEvolutionBehavior.Reject;
}
