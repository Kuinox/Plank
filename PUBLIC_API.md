# Plank Public API

## `Plank.Schema`

### Enums
- `public enum ParquetPhysicalType`
- `public enum ParquetRepetition`
- `public enum EncodingKind`
- `public enum NodeKind`

### Attributes
- `public sealed class GenerateRowApiAttribute : Attribute`
- `public sealed class RowApiTypeHintAttribute : Attribute`
  - `RowApiTypeHintAttribute(string columnName, Type clrType)`
  - `string ColumnName { get; }`
  - `Type ClrType { get; }`

### Types
- `public sealed record Column`
  - `Column(string name, ParquetPhysicalType physicalType, ColumnOptions? options = null)`
  - `string Name { get; }`
  - `ParquetPhysicalType PhysicalType { get; }`
  - `ColumnOptions Options { get; }`
  - `void Validate()`

- `public sealed record ColumnOptions`
  - `ColumnOptions(ParquetRepetition repetition = ParquetRepetition.Unspecified, ImmutableArray<EncodingKind> encodings = default, uint typeLength = 0)`
  - `static readonly ColumnOptions Default`
  - `ParquetRepetition Repetition { get; }`
  - `ImmutableArray<EncodingKind> Encodings { get; }`
  - `uint TypeLength { get; }`
  - `bool Equals(ColumnOptions? other)`
  - `override int GetHashCode()`
  - `void Validate()`

- `public sealed record ColumnDefinition`
  - `required string Name { get; init; }`
  - `required NodeKind Kind { get; init; }`
  - `required ParquetRepetition Repetition { get; init; }`
  - `ParquetPhysicalType? PhysicalType { get; init; }`
  - `ColumnOptions? Options { get; init; }`
  - `ImmutableArray<ColumnDefinition> Children { get; init; }`
  - `void Validate()`

- `public static class ColumnDef`
  - `ColumnDefinition RequiredGroup(string name, params ColumnDefinition[] children)`
  - `ColumnDefinition OptionalGroup(string name, params ColumnDefinition[] children)`
  - `ColumnDefinition RequiredLeaf(string name, ParquetPhysicalType physicalType, ColumnOptions? options = null)`
  - `ColumnDefinition OptionalLeaf(string name, ParquetPhysicalType physicalType, ColumnOptions? options = null)`
  - `ColumnDefinition List(string name, ColumnDefinition element, ParquetRepetition repetition = ParquetRepetition.Required)`
  - `ColumnDefinition Map(string name, ColumnDefinition key, ColumnDefinition value, ParquetRepetition repetition = ParquetRepetition.Required)`

- `public sealed record ParquetSchema`
  - `ParquetSchema(ImmutableArray<Column> columns)`
  - `ParquetSchema(ImmutableArray<ColumnDefinition> definitions)`
  - `ImmutableArray<Column> Columns { get; }`
  - `ImmutableArray<ColumnDefinition> Definitions { get; }`
  - `void Validate()`

## `Plank.Writing`

### Enums
- `public enum CompressionKind`
- `public enum DictionaryMode`
- `public enum PageKind`

### Buffer Pool
- `public interface IParquetBufferPool`
  - `byte[] Rent(uint minimumLength)`
  - `void Return(byte[] buffer)`

- `public sealed class DefaultParquetBufferPool : IParquetBufferPool`
  - `static readonly DefaultParquetBufferPool Shared`
  - `byte[] Rent(uint minimumLength)`
  - `void Return(byte[] buffer)`

### Writer Options
- `public sealed class ParquetWriterOptions`
  - `static readonly ParquetWriterOptions Default`
  - `IParquetBufferPool BufferPool { get; init; }`
  - `uint BufferChunkSizeBytes { get; init; }`
  - `uint InitialPageBufferBytes { get; init; }`
  - `uint InitialColumnBufferBytes { get; init; }`
  - `uint InitialPageCapacity { get; init; }`
  - `CompressionKind Compression { get; init; }`
  - `uint RowApiMaxParallelism { get; init; }`

### Low-Level Buffer
- `public struct BufferWriter : IBufferWriter<byte>`
  - `void Advance(int count)`
  - `Memory<byte> GetMemory(int sizeHint = 0)`
  - `Span<byte> GetSpan(int sizeHint = 0)`

### Core Writing API
- `public sealed class ParquetWriter`
  - `static ParquetWriter Create(Stream stream, ParquetSchema schema, ParquetWriterOptions? options = null)`
  - `uint RowApiMaxParallelism { get; }`
  - `SerializedColumn CreateSerializedColumn()`
  - `void Reset(Stream stream)`
  - `RowGroupWriter StartRowGroup()`
  - `void CloseFile()`

- `public sealed class RowGroupWriter`
  - `SerializedColumn CreateSerializedColumn()`
  - `void Write(SerializedColumn serialized)`

- `public sealed class SerializedColumn`
  - `void Serialize<T>(Column column, ReadOnlySpan<T> values) where T : notnull`
  - `void Serialize<T>(Column column, ReadOnlySpan<T> values, IPageStrategy strategy) where T : notnull`

## `Plank.Writing.PageStrategy`

- `public interface IPageStrategy`
  - `DictionaryMode GetDictionaryMode(Column column)`
  - `bool ShouldDropDictionary<T>(Column column, IReadOnlyDictionary<T, int> dictionary, int totalRowCount, int rowsSeen) where T : notnull`
  - `bool ShouldStartNewDataPage(Column column, int totalRowCount, int rowsWritten, int currentPageRowCount)`

## Row API Runtime Base

- `public interface IRowWriterSlot`
  - `bool IsFull { get; }`
  - `bool IsEmpty { get; }`
  - `void SerializeColumns()`
  - `void WriteSerialized(RowGroupWriter rowGroupWriter)`
  - `void ResetForReuse()`

- `public abstract class RowWriterBase<TSlot> where TSlot : class, IRowWriterSlot`
  - `protected RowWriterBase(Stream stream, ParquetSchema schema, uint maxParallelism, ParquetWriterOptions options)`
  - `protected abstract TSlot CreateSlot(ParquetWriter writer)`
  - `protected void InitializeSlots()`
  - `protected TSlot TakeInitialSlot()`
  - `protected TSlot EnqueueAndTakeFree(TSlot slot)`
  - `protected void Complete(TSlot activeSlot, bool hasRows)`
  - `protected void ThrowIfFaulted()`

## Source-Generated API

For each schema property annotated with `[GenerateRowApi]`, a generated type is emitted:
- `<ContainingType>_<PropertyName>PlankRow`

It exposes schema-specific row writer/pipeline writer APIs.
