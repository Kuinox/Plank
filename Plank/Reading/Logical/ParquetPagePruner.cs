namespace Plank.Reading.Logical;

/// <summary>Decides whether a logical scan should read a data page.</summary>
/// <remarks>
/// The callback runs before page payload I/O. Return <see langword="true"/> to read the page or
/// <see langword="false"/> to skip it. Dictionary and index pages are not passed to the callback.
/// Cache a static delegate to keep warmed scans allocation-free.
/// </remarks>
public delegate bool ParquetPagePruner(in ParquetDataPageMetadata page);
