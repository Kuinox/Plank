using Plank.Reading;

namespace Plank.Writing;

/// <summary>Provides reusable random-access reads and writes to the same Parquet data source.</summary>
public interface IParquetReadWriteSource : IParquetReadSource, IParquetWriteSource;
