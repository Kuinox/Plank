namespace Plank.Dataset;

/// <summary>Creates reusable files for a dataset writer's fixed active-file pool.</summary>
public interface IParquetFileFactory
{
    /// <summary>Creates one unopened reusable file.</summary>
    /// <returns>The reusable file.</returns>
    IParquetFile Create();
}
