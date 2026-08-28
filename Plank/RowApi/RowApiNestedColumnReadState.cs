using System.Runtime.CompilerServices;
using Plank.Reading.Logical;
using Plank.Schema;

namespace Plank.RowApi;

sealed class RowApiNestedColumnReadState<TShape, TElement> : RowApiColumnReadState
{
    readonly RowApiNestedColumnDescriptor<TShape, TElement> _descriptor;
    readonly List<int> _repetitionLevels = [];
    readonly List<int> _definitionLevels = [];
    readonly List<TElement> _values = [];
    NestedRowGroupColumn<TElement>.Enumerator _buffers;
    NestedRowGroupColumn<byte>.Enumerator _binaryBuffers;
    NestedColumnBuffer<TElement> _buffer;
    NestedColumnBuffer<byte> _binaryBuffer;
    internal TShape Current = default!;
    int _levelIndex;
    int _valueIndex;
    bool _binary;
    bool _hasBuffer;
    bool _buffersOpen;
    bool _usingMissing;

    internal RowApiNestedColumnReadState(RowApiNestedColumnDescriptor<TShape, TElement> descriptor)
        : base(descriptor)
        => _descriptor = descriptor;

    internal override void ResetBufferState()
    {
        DisposeBuffers();
        Current = default!;
        _levelIndex = 0;
        _valueIndex = 0;
        _hasBuffer = false;
        _usingMissing = false;
        CurrentIndex = -1;
        BufferedValueCount = 0;
    }

    internal override void SetMissingValue()
    {
        DisposeBuffers();
        Current = default!;
        _usingMissing = true;
        CurrentIndex = 0;
        BufferedValueCount = 1;
    }

    internal override void Open(RowGroup rowGroup)
    {
        DisposeBuffers();
        _binary = Column.PhysicalType is ParquetPhysicalType.ByteArray
            or ParquetPhysicalType.FixedLenByteArray
            or ParquetPhysicalType.Int96;
        if (_binary)
        {
            if (typeof(TElement) != typeof(byte[]))
                throw new NotSupportedException(
                    $"Nested binary row column '{PropertyName}' must use byte[] dense elements.");
            _binaryBuffers = rowGroup.NestedColumn<byte>(Ordinal).GetEnumerator();
        }
        else
        {
            if (RuntimeHelpers.IsReferenceOrContainsReferences<TElement>())
                throw new NotSupportedException(
                    $"Nested row column '{PropertyName}' cannot decode dense values of type {typeof(TElement)}.");
            _buffers = rowGroup.NestedColumn<TElement>(Ordinal).GetEnumerator();
        }

        _buffer = default;
        _binaryBuffer = default;
        Current = default!;
        _levelIndex = 0;
        _valueIndex = 0;
        _hasBuffer = false;
        _buffersOpen = true;
        _usingMissing = false;
        CurrentIndex = -1;
        BufferedValueCount = 0;
    }

    internal override void AdvanceBuffer()
    {
        if (!Projected || _usingMissing)
            return;

        _repetitionLevels.Clear();
        _definitionLevels.Clear();
        _values.Clear();

        if (!EnsureEntry())
            throw new CorruptParquetException($"Column '{PropertyName}' ended before the row group was complete.");
        if (CurrentRepetitionLevel != 0)
            throw new CorruptParquetException(
                $"Column '{PropertyName}' begins a generated row at repetition level {CurrentRepetitionLevel}.");

        do
        {
            CopyCurrentEntry();
            AdvanceEntry();
        }
        while (EnsureEntry() && CurrentRepetitionLevel != 0);

        var denseValueIndex = 0;
        var value = Materialize(typeof(TShape), depth: 0, start: 0, _definitionLevels.Count,
            ref denseValueIndex);
        if (denseValueIndex != _values.Count)
            throw new CorruptParquetException(
                $"Column '{PropertyName}' materialized {denseValueIndex} dense values but decoded {_values.Count}.");

        Current = value is null ? default! : (TShape)value;
    }

    internal override void DisposeBuffers()
    {
        if (!_buffersOpen)
            return;

        if (_binary)
            _binaryBuffers.Dispose();
        else
            _buffers.Dispose();
        _buffersOpen = false;
        _hasBuffer = false;
    }

    object? Materialize(Type shapeType, int depth, int start, int end, ref int denseValueIndex)
    {
        if (depth == _descriptor.CollectionLevels.Length)
        {
            if (end - start != 1)
                throw new CorruptParquetException(
                    $"Column '{PropertyName}' has {end - start} entries for one materialized leaf value.");
            if (_definitionLevels[start] == Descriptor.Column.MaxDefinitionLevel)
            {
                if (denseValueIndex >= _values.Count)
                    throw new CorruptParquetException($"Column '{PropertyName}' has fewer dense values than levels.");
                return _values[denseValueIndex++];
            }

            if (shapeType.IsValueType && Nullable.GetUnderlyingType(shapeType) is null)
                throw new CorruptParquetException(
                    $"Column '{PropertyName}' contains a null leaf that cannot be materialized as {shapeType}.");
            return null;
        }

        if (!shapeType.IsArray)
            throw new CorruptParquetException(
                $"Column '{PropertyName}' expected an array at nested collection depth {depth + 1}.");

        var level = _descriptor.CollectionLevels[depth];
        var firstDefinition = _definitionLevels[start];
        if (firstDefinition < level.DefinedDefinitionLevel)
            return null;

        var elementType = shapeType.GetElementType()!;
        if (firstDefinition < level.ElementDefinitionLevel)
            return Array.CreateInstance(elementType, 0);

        var elementCount = 1;
        for (var i = start + 1; i < end; i++)
            if (_repetitionLevels[i] <= level.RepetitionLevel)
                elementCount++;

        var result = Array.CreateInstance(elementType, elementCount);
        var elementIndex = 0;
        var elementStart = start;
        for (var i = start + 1; i <= end; i++)
        {
            if (i != end && _repetitionLevels[i] > level.RepetitionLevel)
                continue;

            result.SetValue(Materialize(elementType, depth + 1, elementStart, i, ref denseValueIndex),
                elementIndex++);
            elementStart = i;
        }
        return result;
    }

    bool EnsureEntry()
    {
        while (!_hasBuffer || _levelIndex >= CurrentBufferCount)
        {
            if (!MoveNextBuffer())
                return false;
            if (CurrentBufferCount == 0)
                continue;
        }
        return true;
    }

    bool MoveNextBuffer()
    {
        var moved = _binary ? _binaryBuffers.MoveNext() : _buffers.MoveNext();
        if (!moved)
        {
            _hasBuffer = false;
            return false;
        }

        if (_binary)
            _binaryBuffer = _binaryBuffers.Current;
        else
            _buffer = _buffers.Current;
        _levelIndex = 0;
        _valueIndex = 0;
        _hasBuffer = true;
        return true;
    }

    void CopyCurrentEntry()
    {
        var definitionLevel = CurrentDefinitionLevel;
        _repetitionLevels.Add(CurrentRepetitionLevel);
        _definitionLevels.Add(definitionLevel);
        if (definitionLevel != Descriptor.Column.MaxDefinitionLevel)
            return;

        if (_binary)
        {
            if ((uint)_valueIndex >= (uint)_binaryBuffer.Values.Count)
                throw new CorruptParquetException($"Column '{PropertyName}' has fewer binary values than levels.");
            var bytes = _binaryBuffer.Values.GetValue(_valueIndex++).ToArray();
            _values.Add((TElement)(object)bytes);
            return;
        }

        if ((uint)_valueIndex >= (uint)_buffer.Values.Count)
            throw new CorruptParquetException($"Column '{PropertyName}' has fewer dense values than levels.");
        _values.Add(_buffer.Values.Values[_valueIndex++]);
    }

    void AdvanceEntry()
        => _levelIndex++;

    int CurrentBufferCount
        => _binary ? _binaryBuffer.Count : _buffer.Count;

    int CurrentRepetitionLevel
        => _binary ? _binaryBuffer.RepetitionLevels[_levelIndex] : _buffer.RepetitionLevels[_levelIndex];

    int CurrentDefinitionLevel
        => _binary ? _binaryBuffer.DefinitionLevels[_levelIndex] : _buffer.DefinitionLevels[_levelIndex];
}
