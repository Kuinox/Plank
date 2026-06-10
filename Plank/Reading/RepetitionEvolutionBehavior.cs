namespace Plank.Reading;

/// <summary>
/// Controls how a generated multi-file row reader handles required/optional repetition changes in the current file.
/// </summary>
public enum RepetitionEvolutionBehavior
{
    /// <summary>
    /// Throw when the file repetition does not match the generated schema repetition.
    /// </summary>
    Reject = 0,

    /// <summary>
    /// Allow files with required columns to satisfy optional generated-schema columns.
    /// </summary>
    AllowRequiredToOptional = 1,

    /// <summary>
    /// Allow required-to-optional and optional-to-required repetition changes.
    /// </summary>
    AllowRequiredToOptionalAndOptionalToRequired = 2
}
