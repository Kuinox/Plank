using System.Buffers;
using System.Runtime.CompilerServices;
using Apache.Arrow;
using Apache.Arrow.Arrays;
using Apache.Arrow.Types;
using Plank.Schema;
using Plank.Writing;

namespace Plank.Arrow;

sealed class ArrowColumnWriter
{
    readonly Field _field;
    readonly object _serializedColumn;

    public ArrowColumnWriter(ParquetWriter writer, LeafColumn column, Field field)
    {
        _field = field;
        _serializedColumn = CreateSerializedColumn(writer, column, field);
    }

    public void Write(RowGroupWriter rowGroup, IArrowArray array, int expectedLength)
        => Write(rowGroup, new ArraySequence(array), expectedLength);

    public void Write(RowGroupWriter rowGroup, ChunkedArray arrays, int expectedLength)
        => Write(rowGroup, new ArraySequence(arrays), expectedLength);

    void Write(RowGroupWriter rowGroup, ArraySequence arrays, int expectedLength)
    {
        if (arrays.Length != expectedLength)
            throw new ArgumentException(
                $"Arrow field '{_field.Name}' has {arrays.Length} values; expected {expectedLength}.");
        if (!_field.IsNullable && arrays.NullCount != 0)
            throw new ArgumentException(
                $"Non-nullable Arrow field '{_field.Name}' contains {arrays.NullCount} null values.");

        switch (_field.DataType)
        {
            case BooleanType:
                WriteBoolean(rowGroup, arrays);
                break;
            case Int8Type:
                WriteInt8(rowGroup, arrays);
                break;
            case Int16Type:
                WriteInt16(rowGroup, arrays);
                break;
            case Int32Type:
                WritePrimitive<int, Int32Array>(rowGroup, arrays);
                break;
            case Int64Type:
                WritePrimitive<long, Int64Array>(rowGroup, arrays);
                break;
            case UInt8Type:
                WritePrimitive<byte, UInt8Array>(rowGroup, arrays);
                break;
            case UInt16Type:
                WritePrimitive<ushort, UInt16Array>(rowGroup, arrays);
                break;
            case UInt32Type:
                WritePrimitive<uint, UInt32Array>(rowGroup, arrays);
                break;
            case UInt64Type:
                WritePrimitive<ulong, UInt64Array>(rowGroup, arrays);
                break;
            case FloatType:
                WritePrimitive<float, FloatArray>(rowGroup, arrays);
                break;
            case DoubleType:
                WritePrimitive<double, DoubleArray>(rowGroup, arrays);
                break;
            case StringType:
                WriteVariable(rowGroup, arrays, requireString: true);
                break;
            case BinaryType:
                WriteVariable(rowGroup, arrays, requireString: false);
                break;
            case FixedSizeBinaryType fixedBinary:
                WriteFixedBinary(rowGroup, arrays, fixedBinary.ByteWidth);
                break;
            case GuidType:
                WriteGuid(rowGroup, arrays);
                break;
            case Date32Type:
                WritePrimitive<int, Date32Array>(rowGroup, arrays);
                break;
            case Time32Type:
                WritePrimitive<int, Time32Array>(rowGroup, arrays);
                break;
            case Time64Type:
                WritePrimitive<long, Time64Array>(rowGroup, arrays);
                break;
            case TimestampType:
                WritePrimitive<long, TimestampArray>(rowGroup, arrays);
                break;
            default:
                throw new NotSupportedException(
                    $"Arrow adapter does not support writing type '{_field.DataType.Name}' for field '{_field.Name}'.");
        }
    }

    void WriteBoolean(RowGroupWriter rowGroup, ArraySequence arrays)
    {
        if (!_field.IsNullable)
        {
            var rented = ArrayPool<bool>.Shared.Rent(arrays.Length);
            try
            {
                var destination = rented.AsSpan(0, arrays.Length);
                var offset = 0;
                for (var chunkIndex = 0; chunkIndex < arrays.Count; chunkIndex++)
                {
                    var array = Require<BooleanArray>(arrays[chunkIndex]);
                    for (var i = 0; i < array.Length; i++)
                        destination[offset++] = array.GetValue(i)!.Value;
                }
                SerializeAndWrite(rowGroup, destination);
            }
            finally
            {
                ArrayPool<bool>.Shared.Return(rented);
            }
            return;
        }

        var nullableRented = ArrayPool<bool?>.Shared.Rent(arrays.Length);
        try
        {
            var destination = nullableRented.AsSpan(0, arrays.Length);
            var offset = 0;
            for (var chunkIndex = 0; chunkIndex < arrays.Count; chunkIndex++)
            {
                var array = Require<BooleanArray>(arrays[chunkIndex]);
                for (var i = 0; i < array.Length; i++)
                    destination[offset++] = array.GetValue(i);
            }
            SerializeAndWrite(rowGroup, destination);
        }
        finally
        {
            ArrayPool<bool?>.Shared.Return(nullableRented);
        }
    }

    void WriteInt8(RowGroupWriter rowGroup, ArraySequence arrays)
    {
        if (!_field.IsNullable)
        {
            var rented = ArrayPool<int>.Shared.Rent(arrays.Length);
            try
            {
                var destination = rented.AsSpan(0, arrays.Length);
                var offset = 0;
                for (var chunkIndex = 0; chunkIndex < arrays.Count; chunkIndex++)
                {
                    var array = Require<Int8Array>(arrays[chunkIndex]);
                    foreach (var value in array.Values)
                        destination[offset++] = value;
                }
                SerializeAndWrite(rowGroup, destination);
            }
            finally
            {
                ArrayPool<int>.Shared.Return(rented);
            }
            return;
        }

        var nullableRented = ArrayPool<int?>.Shared.Rent(arrays.Length);
        try
        {
            var destination = nullableRented.AsSpan(0, arrays.Length);
            var offset = 0;
            for (var chunkIndex = 0; chunkIndex < arrays.Count; chunkIndex++)
            {
                var array = Require<Int8Array>(arrays[chunkIndex]);
                for (var i = 0; i < array.Length; i++)
                    destination[offset++] = array.GetValue(i);
            }
            SerializeAndWrite(rowGroup, destination);
        }
        finally
        {
            ArrayPool<int?>.Shared.Return(nullableRented);
        }
    }

    void WriteInt16(RowGroupWriter rowGroup, ArraySequence arrays)
    {
        if (!_field.IsNullable)
        {
            var rented = ArrayPool<int>.Shared.Rent(arrays.Length);
            try
            {
                var destination = rented.AsSpan(0, arrays.Length);
                var offset = 0;
                for (var chunkIndex = 0; chunkIndex < arrays.Count; chunkIndex++)
                {
                    var array = Require<Int16Array>(arrays[chunkIndex]);
                    foreach (var value in array.Values)
                        destination[offset++] = value;
                }
                SerializeAndWrite(rowGroup, destination);
            }
            finally
            {
                ArrayPool<int>.Shared.Return(rented);
            }
            return;
        }

        var nullableRented = ArrayPool<int?>.Shared.Rent(arrays.Length);
        try
        {
            var destination = nullableRented.AsSpan(0, arrays.Length);
            var offset = 0;
            for (var chunkIndex = 0; chunkIndex < arrays.Count; chunkIndex++)
            {
                var array = Require<Int16Array>(arrays[chunkIndex]);
                for (var i = 0; i < array.Length; i++)
                    destination[offset++] = array.GetValue(i);
            }
            SerializeAndWrite(rowGroup, destination);
        }
        finally
        {
            ArrayPool<int?>.Shared.Return(nullableRented);
        }
    }

    void WritePrimitive<T, TArray>(RowGroupWriter rowGroup, ArraySequence arrays)
        where T : struct, IEquatable<T>
        where TArray : PrimitiveArray<T>
    {
        if (!_field.IsNullable && arrays.Count == 1)
        {
            var array = Require<TArray>(arrays[0]);
            SerializeAndWrite(rowGroup, array.Values);
            return;
        }

        if (!_field.IsNullable)
        {
            var rented = ArrayPool<T>.Shared.Rent(arrays.Length);
            try
            {
                var destination = rented.AsSpan(0, arrays.Length);
                var offset = 0;
                for (var chunkIndex = 0; chunkIndex < arrays.Count; chunkIndex++)
                {
                    var values = Require<TArray>(arrays[chunkIndex]).Values;
                    values.CopyTo(destination[offset..]);
                    offset += values.Length;
                }
                SerializeAndWrite(rowGroup, destination);
            }
            finally
            {
                ArrayPool<T>.Shared.Return(rented, RuntimeHelpers.IsReferenceOrContainsReferences<T>());
            }
            return;
        }

        var nullableRented = ArrayPool<T?>.Shared.Rent(arrays.Length);
        try
        {
            var destination = nullableRented.AsSpan(0, arrays.Length);
            var offset = 0;
            for (var chunkIndex = 0; chunkIndex < arrays.Count; chunkIndex++)
            {
                var array = Require<TArray>(arrays[chunkIndex]);
                for (var i = 0; i < array.Length; i++)
                    destination[offset++] = array.GetValue(i);
            }
            SerializeAndWrite(rowGroup, destination);
        }
        finally
        {
            ArrayPool<T?>.Shared.Return(nullableRented, RuntimeHelpers.IsReferenceOrContainsReferences<T?>());
        }
    }

    void WriteVariable(RowGroupWriter rowGroup, ArraySequence arrays, bool requireString)
    {
        var rented = ArrayPool<byte[]>.Shared.Rent(arrays.Length);
        try
        {
            var destination = rented.AsSpan(0, arrays.Length);
            var offset = 0;
            for (var chunkIndex = 0; chunkIndex < arrays.Count; chunkIndex++)
            {
                var array = RequireBinary(arrays[chunkIndex], requireString);
                for (var i = 0; i < array.Length; i++)
                    destination[offset++] = array.IsNull(i) ? null! : array.GetBytes(i).ToArray();
            }
            SerializeAndWrite(rowGroup, destination);
        }
        finally
        {
            ArrayPool<byte[]>.Shared.Return(rented, clearArray: true);
        }
    }

    void WriteFixedBinary(RowGroupWriter rowGroup, ArraySequence arrays, int byteWidth)
    {
        var rented = ArrayPool<byte[]>.Shared.Rent(arrays.Length);
        try
        {
            var destination = rented.AsSpan(0, arrays.Length);
            var offset = 0;
            for (var chunkIndex = 0; chunkIndex < arrays.Count; chunkIndex++)
            {
                var array = Require<FixedSizeBinaryArray>(arrays[chunkIndex]);
                for (var i = 0; i < array.Length; i++)
                    destination[offset++] = array.IsNull(i)
                        ? null!
                        : array.ValueBuffer.Memory.Slice((array.Offset + i) * byteWidth, byteWidth).ToArray();
            }
            SerializeAndWrite(rowGroup, destination);
        }
        finally
        {
            ArrayPool<byte[]>.Shared.Return(rented, clearArray: true);
        }
    }

    void WriteGuid(RowGroupWriter rowGroup, ArraySequence arrays)
    {
        if (!_field.IsNullable)
        {
            var rented = ArrayPool<Guid>.Shared.Rent(arrays.Length);
            try
            {
                var destination = rented.AsSpan(0, arrays.Length);
                var offset = 0;
                for (var chunkIndex = 0; chunkIndex < arrays.Count; chunkIndex++)
                {
                    var array = Require<GuidArray>(arrays[chunkIndex]);
                    for (var i = 0; i < array.Length; i++)
                        destination[offset++] = array.GetGuid(i)!.Value;
                }
                SerializeAndWrite(rowGroup, destination);
            }
            finally
            {
                ArrayPool<Guid>.Shared.Return(rented);
            }
            return;
        }

        var nullableRented = ArrayPool<Guid?>.Shared.Rent(arrays.Length);
        try
        {
            var destination = nullableRented.AsSpan(0, arrays.Length);
            var offset = 0;
            for (var chunkIndex = 0; chunkIndex < arrays.Count; chunkIndex++)
            {
                var array = Require<GuidArray>(arrays[chunkIndex]);
                for (var i = 0; i < array.Length; i++)
                    destination[offset++] = array.GetGuid(i);
            }
            SerializeAndWrite(rowGroup, destination);
        }
        finally
        {
            ArrayPool<Guid?>.Shared.Return(nullableRented);
        }
    }

    BinaryArray RequireBinary(IArrowArray array, bool requireString)
    {
        if (requireString && array is StringArray stringArray)
            return stringArray;
        if (!requireString && array is BinaryArray binaryArray && array is not StringArray)
            return binaryArray;
        throw new ArgumentException(
            $"Arrow field '{_field.Name}' expected {(requireString ? "StringArray" : "BinaryArray")}, but received '{array.GetType().Name}'.");
    }

    static TArray Require<TArray>(IArrowArray array)
        where TArray : IArrowArray
        => array is TArray typed
            ? typed
            : throw new ArgumentException(
                $"Expected Arrow array '{typeof(TArray).Name}', but received '{array.GetType().Name}'.");

    void SerializeAndWrite<T>(RowGroupWriter rowGroup, ReadOnlySpan<T> values)
    {
        var serialized = (SerializedColumn<T>)_serializedColumn;
        serialized.Serialize(values);
        rowGroup.Write(serialized);
    }

    static object CreateSerializedColumn(ParquetWriter writer, LeafColumn column, Field field)
        => field.DataType switch
        {
            BooleanType => Create<bool>(writer, column, field.IsNullable),
            Int8Type or Int16Type or Int32Type or Date32Type or Time32Type =>
                Create<int>(writer, column, field.IsNullable),
            Int64Type or Time64Type or TimestampType => Create<long>(writer, column, field.IsNullable),
            UInt8Type => Create<byte>(writer, column, field.IsNullable),
            UInt16Type => Create<ushort>(writer, column, field.IsNullable),
            UInt32Type => Create<uint>(writer, column, field.IsNullable),
            UInt64Type => Create<ulong>(writer, column, field.IsNullable),
            FloatType => Create<float>(writer, column, field.IsNullable),
            DoubleType => Create<double>(writer, column, field.IsNullable),
            StringType or BinaryType or FixedSizeBinaryType => writer.CreateSerializedColumn<byte[]>(column),
            GuidType => Create<Guid>(writer, column, field.IsNullable),
            _ => throw new NotSupportedException(
                $"Arrow adapter does not support writing type '{field.DataType.Name}' for field '{field.Name}'.")
        };

    static object Create<T>(ParquetWriter writer, LeafColumn column, bool nullable)
        where T : struct
        => nullable ? writer.CreateSerializedColumn<T?>(column) : writer.CreateSerializedColumn<T>(column);

    readonly struct ArraySequence
    {
        readonly IArrowArray? _single;
        readonly ChunkedArray? _chunks;

        public ArraySequence(IArrowArray single)
        {
            _single = single;
            _chunks = null;
            Length = single.Length;
            NullCount = single.NullCount;
        }

        public ArraySequence(ChunkedArray chunks)
        {
            _single = null;
            _chunks = chunks;
            Length = checked((int)chunks.Length);
            NullCount = checked((int)chunks.NullCount);
        }

        public int Count
            => _single is null ? _chunks!.ArrayCount : 1;

        public int Length { get; }

        public int NullCount { get; }

        public IArrowArray this[int index]
            => _single is null ? _chunks!.ArrowArray(index) : index == 0
                ? _single
                : throw new ArgumentOutOfRangeException(nameof(index));
    }
}
