using System.Buffers.Binary;
using System.Collections.Immutable;
using Parquet;
using ParquetSharp;
using Plank.Schema;
using Plank.Writing;
using PlankColumn = Plank.Schema.Column;
using PlankParquetSchema = Plank.Schema.ParquetSchema;
using PlankWriter = Plank.Writing.ParquetWriter;

namespace Plank.Tests.E2E;

internal sealed class EncodingTypeMatrixE2ETests
{
    const int RowCount = 48;

    [Test]
    public async Task SupportedEncodingTypePairsAreReadableByBothImplementations()
    {
        var failures = new List<string>();
        foreach (var testCase in EnumerateCases())
        {
            if (!testCase.IsSupported)
                continue;

            try
            {
                await WriteAndReadCaseAsync(testCase).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                failures.Add($"{testCase.Encoding}/{testCase.PhysicalType}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        if (failures.Count > 0)
            throw new InvalidOperationException(
                $"Supported encoding/type cases failed: {string.Join(" | ", failures)}");
    }

    [Test]
    public async Task UnsupportedEncodingTypePairsAreRejected()
    {
        var failures = new List<string>();
        foreach (var testCase in EnumerateCases())
        {
            if (testCase.IsSupported)
                continue;

            try
            {
                await AssertUnsupportedCaseAsync(testCase).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                failures.Add($"{testCase.Encoding}/{testCase.PhysicalType}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        if (failures.Count > 0)
            throw new InvalidOperationException(
                $"Unsupported encoding/type cases unexpectedly succeeded or failed incorrectly: {string.Join(" | ", failures)}");
    }

    static IEnumerable<EncodingTypeCase> EnumerateCases()
    {
        foreach (var encoding in Enum.GetValues<EncodingKind>())
            foreach (var physicalType in Enum.GetValues<ParquetPhysicalType>())
                yield return new EncodingTypeCase(encoding, physicalType, IsSupported(encoding, physicalType));
    }

    static bool IsSupported(EncodingKind encoding, ParquetPhysicalType physicalType)
        => encoding switch
        {
            EncodingKind.Plain => physicalType is not ParquetPhysicalType.FixedLenByteArray,
            EncodingKind.PlainDictionary or EncodingKind.RleDictionary
                => physicalType is not ParquetPhysicalType.Boolean and not ParquetPhysicalType.FixedLenByteArray,
            EncodingKind.Rle => false,
            EncodingKind.BitPacked => false,
            EncodingKind.DeltaBinaryPacked =>
                physicalType is ParquetPhysicalType.Int32 or ParquetPhysicalType.Int64,
            EncodingKind.DeltaLengthByteArray => physicalType == ParquetPhysicalType.ByteArray,
            EncodingKind.DeltaByteArray => physicalType == ParquetPhysicalType.ByteArray,
            EncodingKind.ByteStreamSplit =>
                physicalType is ParquetPhysicalType.Float or ParquetPhysicalType.Double,
            _ => false
        };

    static async Task WriteAndReadCaseAsync(EncodingTypeCase testCase)
    {
        var path = NewPath($"{testCase.Encoding}-{testCase.PhysicalType}");
        try
        {
            using (var stream = File.Create(path))
            {
                var schema = CreateSchema(testCase.Encoding, testCase.PhysicalType);
                var writer = schema.CreateWriter(stream, new ParquetWriterOptions
                {
                    Compression = CompressionKind.None
                });
                var rowGroup = writer.StartRowGroup();
                WriteValues(writer, rowGroup, schema.Columns[0], testCase.PhysicalType);
                writer.CloseFile();
            }

            await AssertParquetNetCanReadAsync(path, RowCount).ConfigureAwait(false);
            AssertParquetSharpCanRead(path, testCase.PhysicalType, RowCount);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    static async Task AssertUnsupportedCaseAsync(EncodingTypeCase testCase)
    {
        var path = NewPath($"unsupported-{testCase.Encoding}-{testCase.PhysicalType}");
        try
        {
            using var stream = File.Create(path);
            var schema = CreateSchema(testCase.Encoding, testCase.PhysicalType);
            var writer = schema.CreateWriter(stream, new ParquetWriterOptions
            {
                Compression = CompressionKind.None
            });
            var rowGroup = writer.StartRowGroup();

            Exception? failure = null;
            try
            {
                WriteValues(writer, rowGroup, schema.Columns[0], testCase.PhysicalType);
                writer.CloseFile();
            }
            catch (NotSupportedException ex)
            {
                failure = ex;
            }
            catch (InvalidOperationException ex)
            {
                failure = ex;
            }

            if (failure is not null)
                return;

            Exception? readFailure = null;
            try
            {
                await AssertParquetNetCanReadAsync(path, RowCount).ConfigureAwait(false);
                AssertParquetSharpCanRead(path, testCase.PhysicalType, RowCount);
            }
            catch (Exception ex)
            {
                readFailure = ex;
            }

            if (readFailure is null)
                throw new InvalidOperationException(
                    $"Unsupported case {testCase.Encoding}/{testCase.PhysicalType} unexpectedly succeeded and was readable by both readers.");
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    static PlankParquetSchema CreateSchema(EncodingKind encoding, ParquetPhysicalType physicalType)
    {
        var options = physicalType == ParquetPhysicalType.FixedLenByteArray
            ? new ColumnOptions(encodings: ImmutableArray.Create(encoding), typeLength: 6)
            : new ColumnOptions(encodings: ImmutableArray.Create(encoding));

        return new PlankParquetSchema([
            new PlankColumn("V", physicalType, options)
        ]);
    }

    static async Task AssertParquetNetCanReadAsync(string path, int expectedRowCount)
    {
        using var stream = File.OpenRead(path);
        using var reader = await ParquetReader.CreateAsync(stream).ConfigureAwait(false);
        if (reader.RowGroupCount != 1)
            throw new InvalidOperationException(
                $"Parquet.Net row-group count mismatch. Expected 1, got {reader.RowGroupCount}.");

        var fields = reader.Schema.GetDataFields();
        if (fields.Length != 1)
            throw new InvalidOperationException(
                $"Parquet.Net field count mismatch. Expected 1, got {fields.Length}.");

        using var rowGroup = reader.OpenRowGroupReader(0);
        var dataColumn = await rowGroup.ReadColumnAsync(fields[0]).ConfigureAwait(false);
        if (dataColumn.Data.Length != expectedRowCount)
            throw new InvalidOperationException(
                $"Parquet.Net row count mismatch. Expected {expectedRowCount}, got {dataColumn.Data.Length}.");
    }

    static void AssertParquetSharpCanRead(string path, ParquetPhysicalType physicalType, int expectedRowCount)
    {
        using var reader = new ParquetFileReader(path);
        var rowGroupCount = checked((int)reader.FileMetaData.NumRowGroups);
        if (rowGroupCount != 1)
            throw new InvalidOperationException(
                $"ParquetSharp row-group count mismatch. Expected 1, got {rowGroupCount}.");

        using var rowGroup = reader.RowGroup(0);
        var rowCount = checked((int)rowGroup.MetaData.NumRows);
        if (rowCount != expectedRowCount)
            throw new InvalidOperationException(
                $"ParquetSharp row count mismatch. Expected {expectedRowCount}, got {rowCount}.");

        switch (physicalType)
        {
            case ParquetPhysicalType.Boolean:
            {
                using var valueReader = rowGroup.Column(0).LogicalReader<bool>();
                if (valueReader.ReadAll(rowCount).Length != expectedRowCount)
                    throw new InvalidOperationException("ParquetSharp boolean read length mismatch.");
                return;
            }
            case ParquetPhysicalType.Int32:
            {
                using var valueReader = rowGroup.Column(0).LogicalReader<int>();
                if (valueReader.ReadAll(rowCount).Length != expectedRowCount)
                    throw new InvalidOperationException("ParquetSharp int32 read length mismatch.");
                return;
            }
            case ParquetPhysicalType.Int64:
            {
                using var valueReader = rowGroup.Column(0).LogicalReader<long>();
                if (valueReader.ReadAll(rowCount).Length != expectedRowCount)
                    throw new InvalidOperationException("ParquetSharp int64 read length mismatch.");
                return;
            }
            case ParquetPhysicalType.Int96:
            {
                using var valueReader = rowGroup.Column(0).LogicalReader<Int96>();
                if (valueReader.ReadAll(rowCount).Length != expectedRowCount)
                    throw new InvalidOperationException("ParquetSharp int96 read length mismatch.");
                return;
            }
            case ParquetPhysicalType.Float:
            {
                using var valueReader = rowGroup.Column(0).LogicalReader<float>();
                if (valueReader.ReadAll(rowCount).Length != expectedRowCount)
                    throw new InvalidOperationException("ParquetSharp float read length mismatch.");
                return;
            }
            case ParquetPhysicalType.Double:
            {
                using var valueReader = rowGroup.Column(0).LogicalReader<double>();
                if (valueReader.ReadAll(rowCount).Length != expectedRowCount)
                    throw new InvalidOperationException("ParquetSharp double read length mismatch.");
                return;
            }
            case ParquetPhysicalType.ByteArray:
            {
                using var valueReader = rowGroup.Column(0).LogicalReader<byte[]>();
                if (valueReader.ReadAll(rowCount).Length != expectedRowCount)
                    throw new InvalidOperationException("ParquetSharp byte[] read length mismatch.");
                return;
            }
            case ParquetPhysicalType.FixedLenByteArray:
            {
                using var valueReader = rowGroup.Column(0).LogicalReader<FixedLenByteArray>();
                if (valueReader.ReadAll(rowCount).Length != expectedRowCount)
                    throw new InvalidOperationException("ParquetSharp fixed-len byte-array read length mismatch.");
                return;
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(physicalType), physicalType, "Unexpected physical type.");
        }
    }

    static void WriteValues(PlankWriter writer, Plank.Writing.RowGroupWriter rowGroup, PlankColumn column,
        ParquetPhysicalType physicalType)
    {
        switch (physicalType)
        {
            case ParquetPhysicalType.Boolean:
            {
                var serialized = writer.CreateSerializedColumn<bool>(column);
                serialized.Serialize(CreateBooleanValues(RowCount));
                rowGroup.Write(serialized);
                return;
            }
            case ParquetPhysicalType.Int32:
            {
                var serialized = writer.CreateSerializedColumn<int>(column);
                serialized.Serialize(CreateInt32Values(RowCount));
                rowGroup.Write(serialized);
                return;
            }
            case ParquetPhysicalType.Int64:
            {
                var serialized = writer.CreateSerializedColumn<long>(column);
                serialized.Serialize(CreateInt64Values(RowCount));
                rowGroup.Write(serialized);
                return;
            }
            case ParquetPhysicalType.Int96:
            {
                var serialized = writer.CreateSerializedColumn<byte[]>(column);
                serialized.Serialize(CreateInt96Values(RowCount));
                rowGroup.Write(serialized);
                return;
            }
            case ParquetPhysicalType.Float:
            {
                var serialized = writer.CreateSerializedColumn<float>(column);
                serialized.Serialize(CreateFloatValues(RowCount));
                rowGroup.Write(serialized);
                return;
            }
            case ParquetPhysicalType.Double:
            {
                var serialized = writer.CreateSerializedColumn<double>(column);
                serialized.Serialize(CreateDoubleValues(RowCount));
                rowGroup.Write(serialized);
                return;
            }
            case ParquetPhysicalType.ByteArray:
            {
                var serialized = writer.CreateSerializedColumn<byte[]>(column);
                serialized.Serialize(CreateByteArrayValues(RowCount));
                rowGroup.Write(serialized);
                return;
            }
            case ParquetPhysicalType.FixedLenByteArray:
            {
                var serialized = writer.CreateSerializedColumn<byte[]>(column);
                serialized.Serialize(CreateFixedLengthByteArrayValues(RowCount, 6));
                rowGroup.Write(serialized);
                return;
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(physicalType), physicalType, "Unexpected physical type.");
        }
    }

    static bool[] CreateBooleanValues(int count)
    {
        var values = new bool[count];
        for (var i = 0; i < count; i++)
            values[i] = (i & 1) == 0;
        return values;
    }

    static int[] CreateInt32Values(int count)
    {
        var values = new int[count];
        for (var i = 0; i < count; i++)
            values[i] = i % 11;
        return values;
    }

    static long[] CreateInt64Values(int count)
    {
        var values = new long[count];
        for (var i = 0; i < count; i++)
            values[i] = (i % 13) * 1000L;
        return values;
    }

    static float[] CreateFloatValues(int count)
    {
        var values = new float[count];
        for (var i = 0; i < count; i++)
            values[i] = (i % 7) + 0.25f;
        return values;
    }

    static double[] CreateDoubleValues(int count)
    {
        var values = new double[count];
        for (var i = 0; i < count; i++)
            values[i] = (i % 17) + 0.125;
        return values;
    }

    static byte[][] CreateByteArrayValues(int count)
    {
        var values = new byte[count][];
        for (var i = 0; i < count; i++)
            values[i] = [(byte)i, (byte)(i >> 1), (byte)(i >> 2), (byte)(i % 3)];
        return values;
    }

    static byte[][] CreateFixedLengthByteArrayValues(int count, int length)
    {
        var values = new byte[count][];
        for (var i = 0; i < count; i++)
        {
            var value = new byte[length];
            for (var j = 0; j < length; j++)
                value[j] = (byte)(i + j);
            values[i] = value;
        }

        return values;
    }

    static byte[][] CreateInt96Values(int count)
    {
        var values = new byte[count][];
        var start = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < count; i++)
        {
            var value = new byte[12];
            var timestamp = start.AddMinutes(i * 15L);
            var julianDay = ToJulianDay(timestamp);
            var nanosOfDay = timestamp.TimeOfDay.Ticks * 100L;
            BinaryPrimitives.WriteInt64LittleEndian(value.AsSpan(0, 8), nanosOfDay);
            BinaryPrimitives.WriteInt32LittleEndian(value.AsSpan(8, 4), julianDay);
            values[i] = value;
        }

        return values;
    }

    static int ToJulianDay(DateTime value)
    {
        var utcDate = value.ToUniversalTime().Date;
        var daysSinceUnixEpoch = (int)(utcDate - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalDays;
        return 2_440_588 + daysSinceUnixEpoch;
    }

    static string NewPath(string suffix)
        => Path.Combine(Path.GetTempPath(), $"plank-encoding-matrix-{suffix}-{Guid.NewGuid():N}.parquet");

    readonly record struct EncodingTypeCase(EncodingKind Encoding, ParquetPhysicalType PhysicalType, bool IsSupported);
}
