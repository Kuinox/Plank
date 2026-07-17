using System.Collections.Immutable;
using Plank.Reading.Logical;
using Plank.Schema;
using Plank.Writing;
using Plank.Writing.PageStrategy;

namespace Plank.Tests.Reading;

[NotInParallel]
internal sealed class ReaderBufferRetentionTests
{
    [Test]
    public void RetainedBufferSurvivesAdvancementAndReaderDisposal()
    {
        var path = CreateFile();
        ParquetBuffer retained = default;
        int[] expected;

        try
        {
            using (var stream = File.OpenRead(path))
            using (var reader = new ParquetReader())
            {
                reader.Reset(stream);
                if (reader.RowGroups.Count == 0)
                    throw new InvalidOperationException("Expected a row group.");

                var rowGroup = reader.RowGroups[0];
                var buffers = rowGroup.Column<int>(0).GetEnumerator();
                try
                {
                    if (!buffers.MoveNext())
                        throw new InvalidOperationException("Expected a value buffer.");

                    var first = buffers.Current;
                    if (!first.CanRetain)
                        throw new InvalidOperationException("Expected an unmanaged buffer to be retainable.");
                    expected = first.Values.ToArray();
                    retained = first.Retain();
                    var firstAddress = retained.DangerousGetAddress();

                    if (!buffers.MoveNext())
                        throw new InvalidOperationException("Expected more than one value buffer.");
                    using var second = buffers.Current.Retain();
                    if (second.DangerousGetAddress() == firstAddress)
                        throw new InvalidOperationException("The pool reused storage that still had a retained owner.");
                }
                finally
                {
                    buffers.Dispose();
                }
            }

            if (!retained.AsSpan<int>().SequenceEqual(expected))
                throw new InvalidOperationException("Retained page contents changed after disposing the reader.");
        }
        finally
        {
            retained.Dispose();
            File.Delete(path);
        }
    }

    [Test]
    public void ReferenceBufferReportsThatRetentionIsUnavailable()
    {
        var schema = new ParquetSchema([
            new Column("Value", ParquetPhysicalType.ByteArray,
                new ColumnOptions(encodings: ImmutableArray.Create(EncodingKind.Plain)))
        ]);
        var path = Path.Combine(Path.GetTempPath(), $"plank-reference-retention-{Guid.NewGuid():N}.parquet");
        try
        {
            using (var stream = File.Create(path))
            {
                var writer = schema.CreateWriter(stream);
                var column = writer.CreateSerializedColumn<byte[]>(schema.Columns[0]);
                column.Serialize([[1], [2]]);
                writer.StartRowGroup().Write(column);
                writer.CloseFile();
            }

            using var input = File.OpenRead(path);
            using var reader = schema.CreateReader(input);
            if (reader.RowGroups.Count == 0)
                throw new InvalidOperationException("Expected a row group.");
            var rowGroup = reader.RowGroups[0];
            var buffers = rowGroup.Column<byte[]>(0).GetEnumerator();
            if (!buffers.MoveNext())
                throw new InvalidOperationException("Expected a value buffer.");
            if (buffers.Current.CanRetain)
                throw new InvalidOperationException("Reference-containing buffers must remain managed for now.");
            buffers.Dispose();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public void NullableBufferIsRetainable()
    {
        var schema = new ParquetSchema([
            new Column("Value", ParquetPhysicalType.Int32,
                new ColumnOptions(ParquetRepetition.Optional,
                    ImmutableArray.Create(EncodingKind.Plain)))
        ]);
        var expected = new int?[] { 1, null, 3, null, 5 };
        AssertRetainedValues(schema, expected, expected);
    }

    [Test]
    public void DictionaryValuesBufferIsRetainable()
    {
        var schema = new ParquetSchema([
            new Column("Value", ParquetPhysicalType.Int32,
                new ColumnOptions(encodings: ImmutableArray.Create(EncodingKind.RleDictionary)))
        ])
        {
            PageStrategiesByColumnName = ImmutableDictionary<string, IPageStrategy>.Empty
                .WithComparers(StringComparer.Ordinal)
                .Add("Value", ForceDictionaryPageStrategy.Shared)
        };
        var expected = new[] { 7, 7, 9, 7, 9, 11 };
        AssertRetainedValues(schema, expected, expected);
    }

    [Test]
    public void UnmanagedNullableAndDictionaryPagesDoNotRentManagedArrays()
    {
        var optionalSchema = new ParquetSchema([
            new Column("Value", ParquetPhysicalType.Int32,
                new ColumnOptions(ParquetRepetition.Optional, ImmutableArray.Create(EncodingKind.Plain)))
        ]);
        AssertNoManagedReaderBuffers(optionalSchema, new int?[] { 1, null, 3, null, 5 });

        var dictionarySchema = new ParquetSchema([
            new Column("Value", ParquetPhysicalType.Int32,
                new ColumnOptions(encodings: ImmutableArray.Create(EncodingKind.RleDictionary)))
        ])
        {
            PageStrategiesByColumnName = ImmutableDictionary<string, IPageStrategy>.Empty
                .WithComparers(StringComparer.Ordinal)
                .Add("Value", ForceDictionaryPageStrategy.Shared)
        };
        AssertNoManagedReaderBuffers(dictionarySchema, new[] { 7, 7, 9, 7, 9, 11 });
    }

    static string CreateFile()
    {
        var schema = new ParquetSchema([
            new Column("Value", ParquetPhysicalType.Int32,
                new ColumnOptions(encodings: ImmutableArray.Create(EncodingKind.Plain)))
        ]);
        var path = Path.Combine(Path.GetTempPath(), $"plank-native-retention-{Guid.NewGuid():N}.parquet");
        using var stream = File.Create(path);
        var writer = schema.CreateWriter(stream, new ParquetWriterOptions
        {
            Compression = CompressionKind.None,
            TargetDataPageSizeBytes = 64
        });
        var column = writer.CreateSerializedColumn<int>(schema.Columns[0]);
        column.Serialize(Enumerable.Range(0, 4096).ToArray());
        writer.StartRowGroup().Write(column);
        writer.CloseFile();
        return path;
    }

    static void AssertRetainedValues<T>(ParquetSchema schema, T[] values, T[] expected)
    {
        var path = Path.Combine(Path.GetTempPath(), $"plank-retained-values-{Guid.NewGuid():N}.parquet");
        try
        {
            using (var stream = File.Create(path))
            {
                var writer = schema.CreateWriter(stream);
                var column = writer.CreateSerializedColumn<T>(schema.Columns[0]);
                column.Serialize(values);
                writer.StartRowGroup().Write(column);
                writer.CloseFile();
            }

            using var input = File.OpenRead(path);
            using var reader = schema.CreateReader(input);
            if (reader.RowGroups.Count == 0)
                throw new InvalidOperationException("Expected a row group.");
            var rowGroup = reader.RowGroups[0];
            var buffers = rowGroup.Column<T>(0).GetEnumerator();
            if (!buffers.MoveNext())
                throw new InvalidOperationException("Expected a value buffer.");
            using var retained = buffers.Current.Retain();
            if (!retained.AsSpan<T>().SequenceEqual(expected))
                throw new InvalidOperationException("Retained values did not match decoded values.");
            buffers.Dispose();
        }
        finally
        {
            File.Delete(path);
        }
    }

    static void AssertNoManagedReaderBuffers<T>(ParquetSchema schema, T[] values)
    {
        var path = Path.Combine(Path.GetTempPath(), $"plank-native-reader-{Guid.NewGuid():N}.parquet");
        try
        {
            using (var stream = File.Create(path))
            {
                var writer = schema.CreateWriter(stream);
                var column = writer.CreateSerializedColumn<T>(schema.Columns[0]);
                column.Serialize(values);
                writer.StartRowGroup().Write(column);
                writer.CloseFile();
            }

            var pool = new TrackingBufferPool();
            using var input = File.OpenRead(path);
            using var reader = schema.CreateReader(input, new ParquetReaderOptions { BufferPool = pool });
            foreach (var buffer in reader.RowGroups[0].Column<T>(0))
                _ = buffer.Values.Length;
        }
        finally
        {
            File.Delete(path);
        }
    }

    sealed class TrackingBufferPool : IParquetBufferPool
    {
        public ParquetBuffer Rent(uint minimumByteLength)
            => DefaultParquetBufferPool.Shared.Rent(minimumByteLength);
    }
}
