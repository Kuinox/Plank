namespace Plank;

/// <summary>Controls how the default buffer pool retains idle native buffers.</summary>
public enum ParquetBufferRetentionPolicy
{
    /// <summary>Retains the rolling p99 peak demand observed independently for each buffer size.</summary>
    Adaptive = 0,

    /// <summary>Retains the highest demand observed for each buffer size to avoid allocations after warmup.</summary>
    ZeroAllocation = 1
}
