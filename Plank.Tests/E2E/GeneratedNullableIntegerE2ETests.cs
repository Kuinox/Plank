using Plank.Schema;
using Plank.Writing;

namespace Plank.Tests.E2E;

internal sealed class GeneratedNullableIntegerE2ETests
{
    [Test]
    [Arguments(ParquetDataPageVersion.V1)]
    [Arguments(ParquetDataPageVersion.V2)]
    public void NullableIntegersRoundTripAcrossCollectionShapes(ParquetDataPageVersion pageVersion)
    {
        byte?[] byteValues = [0, null, byte.MaxValue];
        ushort?[] uint16Values = [0, null, ushort.MaxValue];
        uint?[] uint32Values = [0, int.MaxValue, null, (uint)int.MaxValue + 1, uint.MaxValue];
        ulong?[] uint64Values = [0, long.MaxValue, null, (ulong)long.MaxValue + 1, ulong.MaxValue];
        int?[] int32Values = [int.MinValue, null, 0, int.MaxValue];
        long?[] int64Values = [long.MinValue, null, 0, long.MaxValue];
        using var stream = new MemoryStream();
        using (var writer = GeneratedNullableIntegerSchema.CreateRowWriter(stream, maxParallelism: 1,
                   new ParquetWriterOptions { Compression = CompressionKind.None, DataPageVersion = pageVersion }))
        {
            for (var index = 0; index < 3; index++)
            {
                var row = writer.GetRow();
                var bytes = SelectValues(index, byteValues);
                row.ByteArray = bytes;
                row.ByteList = bytes?.ToList();
                row.ByteMap = ToMap(bytes);
                row.ByteMatrix = ToMatrix(bytes);
                row.RequiredBytes = RequiredValues(bytes);
                var uint16 = SelectValues(index, uint16Values);
                row.UInt16Array = uint16;
                row.UInt16List = uint16?.ToList();
                row.UInt16Map = ToMap(uint16);
                row.UInt16Matrix = ToMatrix(uint16);
                row.RequiredUInt16s = RequiredValues(uint16);
                var uint32 = SelectValues(index, uint32Values);
                row.UInt32Array = uint32;
                row.UInt32List = uint32?.ToList();
                row.UInt32Map = ToMap(uint32);
                row.UInt32Matrix = ToMatrix(uint32);
                row.RequiredUInt32s = RequiredValues(uint32);
                var uint64 = SelectValues(index, uint64Values);
                row.UInt64Array = uint64;
                row.UInt64List = uint64?.ToList();
                row.UInt64Map = ToMap(uint64);
                row.UInt64Matrix = ToMatrix(uint64);
                row.RequiredUInt64s = RequiredValues(uint64);
                row.Int32Array = SelectValues(index, int32Values);
                row.Int64Array = SelectValues(index, int64Values);
            }
            writer.Complete();
        }

        using var readStream = new MemoryStream(stream.ToArray());
        using var reader = GeneratedNullableIntegerSchema.CreateRowReader(readStream);
        var rowIndex = 0;
        foreach (var row in reader)
        {
            var bytes = SelectValues(rowIndex, byteValues);
            AssertSequence(row.ByteArray, bytes, nameof(row.ByteArray));
            AssertSequence(row.ByteList, bytes, nameof(row.ByteList));
            AssertSequence(row.ByteMap, ToMap(bytes), nameof(row.ByteMap));
            AssertMatrix(row.ByteMatrix, ToMatrix(bytes), nameof(row.ByteMatrix));
            AssertSequence(row.RequiredBytes, RequiredValues(bytes), nameof(row.RequiredBytes));
            var uint16 = SelectValues(rowIndex, uint16Values);
            AssertSequence(row.UInt16Array, uint16, nameof(row.UInt16Array));
            AssertSequence(row.UInt16List, uint16, nameof(row.UInt16List));
            AssertSequence(row.UInt16Map, ToMap(uint16), nameof(row.UInt16Map));
            AssertMatrix(row.UInt16Matrix, ToMatrix(uint16), nameof(row.UInt16Matrix));
            AssertSequence(row.RequiredUInt16s, RequiredValues(uint16), nameof(row.RequiredUInt16s));
            var uint32 = SelectValues(rowIndex, uint32Values);
            AssertSequence(row.UInt32Array, uint32, nameof(row.UInt32Array));
            AssertSequence(row.UInt32List, uint32, nameof(row.UInt32List));
            AssertSequence(row.UInt32Map, ToMap(uint32), nameof(row.UInt32Map));
            AssertMatrix(row.UInt32Matrix, ToMatrix(uint32), nameof(row.UInt32Matrix));
            AssertSequence(row.RequiredUInt32s, RequiredValues(uint32), nameof(row.RequiredUInt32s));
            var uint64 = SelectValues(rowIndex, uint64Values);
            AssertSequence(row.UInt64Array, uint64, nameof(row.UInt64Array));
            AssertSequence(row.UInt64List, uint64, nameof(row.UInt64List));
            AssertSequence(row.UInt64Map, ToMap(uint64), nameof(row.UInt64Map));
            AssertMatrix(row.UInt64Matrix, ToMatrix(uint64), nameof(row.UInt64Matrix));
            AssertSequence(row.RequiredUInt64s, RequiredValues(uint64), nameof(row.RequiredUInt64s));
            AssertSequence(row.Int32Array, SelectValues(rowIndex, int32Values), nameof(row.Int32Array));
            AssertSequence(row.Int64Array, SelectValues(rowIndex, int64Values), nameof(row.Int64Array));
            rowIndex++;
        }
        if (rowIndex != 3)
            throw new InvalidOperationException($"Expected 3 rows, read {rowIndex}.");
    }

    static T?[]? SelectValues<T>(int row, T?[] values) where T : struct
        => row switch { 0 => null, 1 => [], _ => values };

    static Dictionary<uint, T?>? ToMap<T>(T?[]? values) where T : struct
        => values?.Select((value, index) => new KeyValuePair<uint, T?>(uint.MaxValue - (uint)index, value))
            .ToDictionary();

    static T?[][]? ToMatrix<T>(T?[]? values) where T : struct
        => values is null ? null : [values, [], [null]];

    static List<T> RequiredValues<T>(T?[]? values) where T : struct
        => values?.Where(static value => value.HasValue).Select(static value => value!.Value).ToList() ?? [];

    static void AssertSequence<T>(IEnumerable<T>? actual, IEnumerable<T>? expected, string name)
    {
        if (actual is null || expected is null)
        {
            if (actual is not null || expected is not null)
                throw new InvalidOperationException($"Null collection differs for {name}.");
            return;
        }
        if (!actual.SequenceEqual(expected))
            throw new InvalidOperationException($"Values differ for {name}.");
    }

    static void AssertMatrix<T>(T?[][]? actual, T?[][]? expected, string name) where T : struct
    {
        if (actual is null || expected is null)
        {
            if (actual is not null || expected is not null)
                throw new InvalidOperationException($"Null matrix differs for {name}.");
            return;
        }
        if (actual.Length != expected.Length)
            throw new InvalidOperationException($"Matrix length differs for {name}.");
        for (var index = 0; index < expected.Length; index++)
            AssertSequence(actual[index], expected[index], name);
    }
}
