using Apache.Arrow;
using Apache.Arrow.Arrays;
using Apache.Arrow.Types;
using ParquetSharp;
using Plank.Writing;
using ArrowColumn = Apache.Arrow.Column;
using ArrowSchema = Apache.Arrow.Schema;
using ArrowTimeUnit = Apache.Arrow.Types.TimeUnit;

namespace Plank.Arrow.Tests;

internal sealed class ArrowAdapterTests
{
    [Test]
    public void SchemaConversionCoversSupportedScalarSurface()
    {
        var arrowSchema = new ArrowSchema([
            new Field("boolean", BooleanType.Default, nullable: false),
            new Field("i8", Int8Type.Default, nullable: true),
            new Field("i16", Int16Type.Default, nullable: false),
            new Field("i32", Int32Type.Default, nullable: true),
            new Field("i64", Int64Type.Default, nullable: false),
            new Field("u8", UInt8Type.Default, nullable: true),
            new Field("u16", UInt16Type.Default, nullable: false),
            new Field("u32", UInt32Type.Default, nullable: true),
            new Field("u64", UInt64Type.Default, nullable: false),
            new Field("float", FloatType.Default, nullable: true),
            new Field("double", DoubleType.Default, nullable: false),
            new Field("string", StringType.Default, nullable: true),
            new Field("binary", BinaryType.Default, nullable: false),
            new Field("fixed", new FixedSizeBinaryType(12), nullable: true),
            new Field("uuid", GuidType.Default, nullable: false),
            new Field("date", Date32Type.Default, nullable: true),
            new Field("time_ms", new Time32Type(ArrowTimeUnit.Millisecond), nullable: false),
            new Field("time_us", new Time64Type(ArrowTimeUnit.Microsecond), nullable: true),
            new Field("time_ns", new Time64Type(ArrowTimeUnit.Nanosecond), nullable: false),
            new Field("timestamp", new TimestampType(ArrowTimeUnit.Microsecond, "UTC"), nullable: true)
        ], metadata: null);

        var parquetSchema = ArrowSchemaConverter.ToParquetSchema(arrowSchema);
        var roundTripped = ArrowSchemaConverter.ToArrowSchema(parquetSchema);

        Equal(arrowSchema.FieldsList.Count, roundTripped.FieldsList.Count, "field count");
        for (var i = 0; i < arrowSchema.FieldsList.Count; i++)
        {
            Equal(arrowSchema.GetFieldByIndex(i).Name, roundTripped.GetFieldByIndex(i).Name, $"field {i} name");
            Equal(arrowSchema.GetFieldByIndex(i).IsNullable, roundTripped.GetFieldByIndex(i).IsNullable,
                $"field {i} nullability");
            Equal(arrowSchema.GetFieldByIndex(i).DataType.GetType(), roundTripped.GetFieldByIndex(i).DataType.GetType(),
                $"field {i} type");
        }

        var fixedType = (FixedSizeBinaryType)roundTripped.GetFieldByName("fixed").DataType;
        Equal(12, fixedType.ByteWidth, "fixed byte width");
        var timestamp = (TimestampType)roundTripped.GetFieldByName("timestamp").DataType;
        Equal(ArrowTimeUnit.Microsecond, timestamp.Unit, "timestamp unit");
        Equal("UTC", timestamp.Timezone, "timestamp timezone");
    }

    [Test]
    public void RecordBatchRoundTripsSupportedValuesAndNulls()
    {
        var firstGuid = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");
        var secondGuid = Guid.Parse("ffeeddcc-bbaa-9988-7766-554433221100");
        var schema = new ArrowSchema([
            new Field("boolean", BooleanType.Default, nullable: false),
            new Field("i8", Int8Type.Default, nullable: true),
            new Field("i16", Int16Type.Default, nullable: false),
            new Field("i32", Int32Type.Default, nullable: true),
            new Field("i64", Int64Type.Default, nullable: false),
            new Field("u8", UInt8Type.Default, nullable: true),
            new Field("u16", UInt16Type.Default, nullable: false),
            new Field("u32", UInt32Type.Default, nullable: true),
            new Field("u64", UInt64Type.Default, nullable: false),
            new Field("float", FloatType.Default, nullable: true),
            new Field("double", DoubleType.Default, nullable: false),
            new Field("string", StringType.Default, nullable: true),
            new Field("binary", BinaryType.Default, nullable: false),
            new Field("uuid", GuidType.Default, nullable: true),
            new Field("date", Date32Type.Default, nullable: true),
            new Field("time", new Time64Type(ArrowTimeUnit.Microsecond), nullable: false),
            new Field("timestamp", new TimestampType(ArrowTimeUnit.Microsecond, "UTC"), nullable: true)
        ], metadata: null);
        var arrays = new IArrowArray[]
        {
            new BooleanArray.Builder().Append(true).Append(false).Append(true).Build(),
            new Int8Array.Builder().Append(-1).AppendNull().Append(127).Build(),
            new Int16Array.Builder().Append(short.MinValue).Append(0).Append(short.MaxValue).Build(),
            new Int32Array.Builder().Append(10).AppendNull().Append(-30).Build(),
            new Int64Array.Builder().Append(long.MinValue).Append(0).Append(long.MaxValue).Build(),
            new UInt8Array.Builder().Append(1).AppendNull().Append(byte.MaxValue).Build(),
            new UInt16Array.Builder().Append(1).Append(2).Append(ushort.MaxValue).Build(),
            new UInt32Array.Builder().Append(1).AppendNull().Append(uint.MaxValue).Build(),
            new UInt64Array.Builder().Append(1).Append(2).Append(ulong.MaxValue).Build(),
            new FloatArray.Builder().Append(1.5f).AppendNull().Append(-3.25f).Build(),
            new DoubleArray.Builder().Append(double.MinValue).Append(0.25).Append(double.MaxValue).Build(),
            new StringArray.Builder().Append("alpha").AppendNull().Append(string.Empty).Build(),
            new BinaryArray.Builder().Append(new byte[] { 1, 2 }).Append(ReadOnlySpan<byte>.Empty)
                .Append(new byte[] { 3, 4, 5 }).Build(),
            new GuidArray.Builder().Append(firstGuid).AppendNull().Append(secondGuid).Build(),
            new Date32Array.Builder().Append(new DateOnly(2024, 1, 2)).AppendNull()
                .Append(new DateOnly(1969, 12, 31)).Build(),
            new Time64Array.Builder(ArrowTimeUnit.Microsecond).Append(new TimeOnly(1, 2, 3))
                .Append(new TimeOnly(12, 34, 56)).Append(new TimeOnly(23, 59, 59)).Build(),
            new TimestampArray.Builder(ArrowTimeUnit.Microsecond, "UTC")
                .Append(DateTimeOffset.UnixEpoch.AddTicks(10)).AppendNull()
                .Append(DateTimeOffset.UnixEpoch.AddDays(20)).Build()
        };
        using var batch = new RecordBatch(schema, arrays, 3);
        using var stream = new MemoryStream();

        using (var writer = new ArrowParquetWriter(stream, schema, SmallWriterOptions(), leaveOpen: true))
            writer.WriteRecordBatch(batch);

        stream.Position = 0;
        using var reader = new ArrowParquetReader(stream);
        using var actual = reader.ReadRecordBatch(0);

        Equal(3, actual.Length, "row count");
        SequenceEqual([true, false, true], Values((BooleanArray)actual.Column(0)), "boolean");
        SequenceEqual<sbyte?>([-1, null, 127], Values((Int8Array)actual.Column(1)), "i8");
        SequenceEqual<short?>([short.MinValue, 0, short.MaxValue], Values((Int16Array)actual.Column(2)), "i16");
        SequenceEqual<int?>([10, null, -30], Values((Int32Array)actual.Column(3)), "i32");
        SequenceEqual<long?>([long.MinValue, 0, long.MaxValue], Values((Int64Array)actual.Column(4)), "i64");
        SequenceEqual<byte?>([1, null, byte.MaxValue], Values((UInt8Array)actual.Column(5)), "u8");
        SequenceEqual<ushort?>([1, 2, ushort.MaxValue], Values((UInt16Array)actual.Column(6)), "u16");
        SequenceEqual<uint?>([1, null, uint.MaxValue], Values((UInt32Array)actual.Column(7)), "u32");
        SequenceEqual<ulong?>([1, 2, ulong.MaxValue], Values((UInt64Array)actual.Column(8)), "u64");
        SequenceEqual<float?>([1.5f, null, -3.25f], Values((FloatArray)actual.Column(9)), "float");
        SequenceEqual<double?>([double.MinValue, 0.25, double.MaxValue], Values((DoubleArray)actual.Column(10)),
            "double");
        SequenceEqual<string?>(["alpha", null, string.Empty], Values((StringArray)actual.Column(11)), "string");
        BinaryEqual([[1, 2], [], [3, 4, 5]], (BinaryArray)actual.Column(12), "binary");
        SequenceEqual<Guid?>([firstGuid, null, secondGuid], Values((GuidArray)actual.Column(13)), "uuid");
        SequenceEqual<int?>([19724, null, -1], Values((Date32Array)actual.Column(14)), "date raw values");
        SequenceEqual<long?>(arrays[15] is Time64Array expectedTime ? Values(expectedTime) : [],
            Values((Time64Array)actual.Column(15)), "time raw values");
        SequenceEqual<long?>(arrays[16] is TimestampArray expectedTimestamp ? Values(expectedTimestamp) : [],
            Values((TimestampArray)actual.Column(16)), "timestamp raw values");
    }

    [Test]
    public void TablesJoinInputChunksAndRetainOutputRowGroupChunks()
    {
        var schema = new ArrowSchema([
            new Field("id", Int32Type.Default, nullable: false),
            new Field("name", StringType.Default, nullable: true)
        ], metadata: null);
        using var first = CreateSimpleBatch(schema, [1, 2], ["one", null]);
        using var second = CreateSimpleBatch(schema, [3, 4, 5], ["three", "four", "five"]);
        var inputTable = Table.TableFromRecordBatches(schema, [first, second]);
        using var tableStream = new MemoryStream();

        using (var writer = new ArrowParquetWriter(tableStream, schema, SmallWriterOptions(), leaveOpen: true))
            writer.WriteTable(inputTable);

        tableStream.Position = 0;
        using (var reader = new ArrowParquetReader(tableStream))
        {
            Equal(1, reader.RecordBatchCount, "joined table row groups");
            using var joined = reader.ReadRecordBatch(0);
            SequenceEqual<int?>([1, 2, 3, 4, 5], Values((Int32Array)joined.Column(0)), "joined ids");
            SequenceEqual<string?>(["one", null, "three", "four", "five"],
                Values((StringArray)joined.Column(1)), "joined names");
        }

        using var batchesStream = new MemoryStream();
        using (var writer = new ArrowParquetWriter(batchesStream, schema, SmallWriterOptions(), leaveOpen: true))
        {
            writer.WriteRecordBatch(first);
            writer.WriteRecordBatch(second);
        }

        batchesStream.Position = 0;
        using var batchesReader = new ArrowParquetReader(batchesStream);
        var outputTable = batchesReader.ReadTable();
        Equal(5L, outputTable.RowCount, "output table rows");
        Equal(2, outputTable.Column(0).Data.ArrayCount, "output table chunks");
    }

    [Test]
    public void FixedSizeBinaryRoundTripsWidthAndNulls()
    {
        var type = new FixedSizeBinaryType(3);
        var schema = new ArrowSchema([new Field("fixed", type, nullable: true)], metadata: null);
#pragma warning disable CA2000 // RecordBatch takes ownership of its arrays.
        using var batch = new RecordBatch(schema,
            [CreateFixedSizeBinary(type, [new byte[] { 1, 2, 3 }, null, new byte[] { 4, 5, 6 }])], 3);
#pragma warning restore CA2000
        using var stream = new MemoryStream();

        using (var writer = new ArrowParquetWriter(stream, schema, SmallWriterOptions(), leaveOpen: true))
            writer.WriteRecordBatch(batch);

        stream.Position = 0;
        using var reader = new ArrowParquetReader(stream);
        using var actualBatch = reader.ReadRecordBatch(0);
        var actual = (FixedSizeBinaryArray)actualBatch.Column(0);
        Equal(3, ((FixedSizeBinaryType)actual.Data.DataType).ByteWidth, "fixed width");
        if (!actual.GetBytes(0).SequenceEqual(new byte[] { 1, 2, 3 }))
            throw new InvalidOperationException("First fixed-size value differs.");
        Equal(true, actual.IsNull(1), "fixed null");
        if (!actual.GetBytes(2).SequenceEqual(new byte[] { 4, 5, 6 }))
            throw new InvalidOperationException("Last fixed-size value differs.");
    }

    [Test]
    public void AdapterOutputIsReadableByParquetSharp()
    {
        var path = NewPath("arrow-write");
        var schema = new ArrowSchema([
            new Field("id", Int32Type.Default, nullable: false),
            new Field("name", StringType.Default, nullable: true)
        ], metadata: null);
        using var batch = CreateSimpleBatch(schema, [10, 20, 30], ["ten", null, "thirty"]);
        try
        {
            using (var stream = File.Create(path))
            using (var writer = new ArrowParquetWriter(stream, schema, SmallWriterOptions()))
                writer.WriteRecordBatch(batch);

            using var reader = new ParquetFileReader(path);
            using var rowGroup = reader.RowGroup(0);
            SequenceEqual([10, 20, 30], rowGroup.Column(0).LogicalReader<int>().ReadAll(3), "interop ids");
            SequenceEqual<string?>(["ten", null, "thirty"],
                rowGroup.Column(1).LogicalReader<string>().ReadAll(3), "interop names");
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Test]
    public void ParquetSharpOutputIsReadableAsArrow()
    {
        var path = NewPath("arrow-read");
        try
        {
            using (var stream = File.Create(path))
            using (var writer = new ParquetFileWriter(stream,
                       [new ParquetSharp.Column<int>("id"), new ParquetSharp.Column<string>("name")],
                       leaveOpen: true))
            {
                using var rowGroup = writer.AppendRowGroup();
                using (var ids = rowGroup.NextColumn().LogicalWriter<int>())
                    ids.WriteBatch([7, 8, 9]);
                using (var names = rowGroup.NextColumn().LogicalWriter<string>())
                    names.WriteBatch(["seven", "eight", "nine"]);
                writer.Close();
            }

            using var source = File.OpenRead(path);
            using var reader = new ArrowParquetReader(source);
            using var batch = reader.ReadRecordBatch(0);
            SequenceEqual<int?>([7, 8, 9], Values((Int32Array)batch.Column(0)), "ParquetSharp ids");
            SequenceEqual<string?>(["seven", "eight", "nine"], Values((StringArray)batch.Column(1)),
                "ParquetSharp names");
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Test]
    public void UnsupportedTimestampSecondsAreRejected()
    {
        var schema = new ArrowSchema([
            new Field("timestamp", new TimestampType(ArrowTimeUnit.Second, "UTC"), nullable: false)
        ], metadata: null);

        try
        {
            ArrowSchemaConverter.ToParquetSchema(schema);
        }
        catch (NotSupportedException exception) when (exception.Message.Contains("Second", StringComparison.Ordinal))
        {
            return;
        }

        throw new InvalidOperationException("Expected second-resolution timestamps to be rejected.");
    }

    [Test]
    public void RequiredArrowFieldsRejectNullArrays()
    {
        var schema = new ArrowSchema([new Field("id", Int32Type.Default, nullable: false)], metadata: null);
        using var batch = new RecordBatch(schema, [new Int32Array.Builder().AppendNull().Build()], 1);
        using var stream = new MemoryStream();
        using var writer = new ArrowParquetWriter(stream, schema, SmallWriterOptions(), leaveOpen: true);

        try
        {
            writer.WriteRecordBatch(batch);
        }
        catch (ArgumentException exception) when (exception.Message.Contains("Non-nullable", StringComparison.Ordinal))
        {
            return;
        }

        throw new InvalidOperationException("Expected a null in a required Arrow field to be rejected.");
    }

    static RecordBatch CreateSimpleBatch(ArrowSchema schema, int[] ids, string?[] names)
    {
        var idBuilder = new Int32Array.Builder().Append(ids);
        var nameBuilder = new StringArray.Builder();
        for (var i = 0; i < names.Length; i++)
            nameBuilder.Append(names[i]);
        return new RecordBatch(schema, [idBuilder.Build(), nameBuilder.Build()], ids.Length);
    }

    static FixedSizeBinaryArray CreateFixedSizeBinary(FixedSizeBinaryType type, byte[]?[] rows)
    {
        var values = new ArrowBuffer.Builder<byte>(checked(rows.Length * type.ByteWidth));
        var validity = new ArrowBuffer.BitmapBuilder(rows.Length);
        for (var i = 0; i < rows.Length; i++)
        {
            if (rows[i] is { } value)
            {
                if (value.Length != type.ByteWidth)
                    throw new ArgumentException("Fixed-size test value has the wrong width.", nameof(rows));
                values.Append(value);
                validity.Append(true);
            }
            else
            {
                values.Append(new byte[type.ByteWidth]);
                validity.Append(false);
            }
        }

#pragma warning disable CA2000 // FixedSizeBinaryArray takes ownership of ArrayData and its buffers.
        return new FixedSizeBinaryArray(new ArrayData(type, rows.Length, validity.UnsetBitCount, 0,
            [validity.Build(), values.Build()]));
#pragma warning restore CA2000
    }

    static ParquetWriterOptions SmallWriterOptions()
        => new()
        {
            BufferChunkSizeBytes = 4 * 1024,
            InitialPageBufferBytes = 4 * 1024,
            InitialColumnBufferBytes = 4 * 1024,
            TargetDataPageSizeBytes = 4 * 1024
        };

    static T?[] Values<T>(PrimitiveArray<T> array)
        where T : struct, IEquatable<T>
    {
        var values = new T?[array.Length];
        for (var i = 0; i < values.Length; i++)
            values[i] = array.GetValue(i);
        return values;
    }

    static bool?[] Values(BooleanArray array)
    {
        var values = new bool?[array.Length];
        for (var i = 0; i < values.Length; i++)
            values[i] = array.GetValue(i);
        return values;
    }

    static string?[] Values(StringArray array)
    {
        var values = new string?[array.Length];
        for (var i = 0; i < values.Length; i++)
            values[i] = array.GetString(i);
        return values;
    }

    static Guid?[] Values(GuidArray array)
    {
        var values = new Guid?[array.Length];
        for (var i = 0; i < values.Length; i++)
            values[i] = array.GetGuid(i);
        return values;
    }

    static void BinaryEqual(byte[][] expected, BinaryArray actual, string label)
    {
        Equal(expected.Length, actual.Length, $"{label} length");
        for (var i = 0; i < expected.Length; i++)
            if (!actual.GetBytes(i).SequenceEqual(expected[i]))
                throw new InvalidOperationException($"{label} differs at index {i}.");
    }

    static void SequenceEqual<T>(T[] expected, T[] actual, string label)
    {
        Equal(expected.Length, actual.Length, $"{label} length");
        for (var i = 0; i < expected.Length; i++)
            if (!EqualityComparer<T>.Default.Equals(expected[i], actual[i]))
                throw new InvalidOperationException(
                    $"{label} differs at index {i}: expected '{Display(expected[i])}', actual '{Display(actual[i])}'.");
    }

    static void Equal<T>(T expected, T actual, string label)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{label}: expected '{expected}', actual '{actual}'.");
    }

    static string NewPath(string label)
        => Path.Combine(Path.GetTempPath(), $"plank-{label}-{Guid.NewGuid():N}.parquet");

    static string Display<T>(T value)
        => value is null ? "<null>" : value.ToString() ?? "<null>";
}
