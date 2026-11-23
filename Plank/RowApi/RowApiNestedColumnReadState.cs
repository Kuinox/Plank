using System.Runtime.CompilerServices;
using Plank.Reading.Logical;
using Plank.Schema;

namespace Plank.RowApi;

// Collects the levels and dense values of the current row; generated code rebuilds the row's objects from them.
sealed class RowApiNestedColumnReadState<TValue> : RowApiColumnReadState
{
    NestedRowGroupColumn<TValue>.Enumerator _buffers;
    NestedRowGroupColumn<byte>.Enumerator _binaryBuffers;
    NestedColumnBuffer<TValue> _buffer;
    NestedColumnBuffer<byte> _binaryBuffer;
    byte[] _repetitionLevels = new byte[16];
    byte[] _definitionLevels = new byte[16];
    TValue[] _values = new TValue[16];
    int _levelCount;
    int _valueCount;
    int _levelIndex;
    int _valueIndex;
    bool _binary;
    bool _hasBuffer;
    bool _buffersOpen;
    bool _usingMissing;

    internal RowApiNestedColumnReadState(RowApiNestedColumnDescriptor<TValue> descriptor)
        : base(descriptor)
    {
    }

    internal RowApiNestedLeafRow<TValue> Current
        => new(_repetitionLevels.AsSpan(0, _levelCount), _definitionLevels.AsSpan(0, _levelCount),
            _values.AsSpan(0, _valueCount));

    internal override void ResetBufferState()
    {
        DisposeBuffers();
        ClearCurrent();
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
        ClearCurrent();
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
            if (typeof(TValue) != typeof(byte[]))
                throw new NotSupportedException(
                    $"Nested binary row column '{PropertyName}' must use byte[] dense elements.");
            _binaryBuffers = rowGroup.NestedColumn<byte>(Ordinal).GetEnumerator();
        }
        else
        {
            if (RuntimeHelpers.IsReferenceOrContainsReferences<TValue>())
                throw new NotSupportedException(
                    $"Nested row column '{PropertyName}' cannot decode dense values of type {typeof(TValue)}.");
            _buffers = rowGroup.NestedColumn<TValue>(Ordinal).GetEnumerator();
        }

        _buffer = default;
        _binaryBuffer = default;
        ClearCurrent();
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

        ClearCurrent();
        if (!EnsureEntry())
            throw new CorruptParquetException($"Column '{PropertyName}' ended before the row group was complete.");
        if (CurrentRepetitionLevel != 0)
            throw new CorruptParquetException(
                $"Column '{PropertyName}' begins a generated row at repetition level {CurrentRepetitionLevel}.");

        do
        {
            CopyCurrentEntry();
            _levelIndex++;
        }
        while (EnsureEntry() && CurrentRepetitionLevel != 0);
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

    void ClearCurrent()
    {
        if (RuntimeHelpers.IsReferenceOrContainsReferences<TValue>())
            Array.Clear(_values, 0, _valueCount);
        _levelCount = 0;
        _valueCount = 0;
    }

    bool EnsureEntry()
    {
        while (!_hasBuffer || _levelIndex >= CurrentBufferCount)
        {
            if (!MoveNextBuffer())
                return false;
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
        if (_levelCount == _repetitionLevels.Length)
        {
            Array.Resize(ref _repetitionLevels, _levelCount * 2);
            Array.Resize(ref _definitionLevels, _levelCount * 2);
        }
        // The descriptor limits the leaf's levels to a byte, and the decoder rejects levels above them.
        _repetitionLevels[_levelCount] = (byte)CurrentRepetitionLevel;
        _definitionLevels[_levelCount] = (byte)definitionLevel;
        _levelCount++;
        if (definitionLevel != Descriptor.Column.MaxDefinitionLevel)
            return;

        if (_valueCount == _values.Length)
            Array.Resize(ref _values, _valueCount * 2);
        if (_binary)
        {
            if ((uint)_valueIndex >= (uint)_binaryBuffer.Values.Count)
                throw new CorruptParquetException($"Column '{PropertyName}' has fewer binary values than levels.");
            var bytes = _binaryBuffer.Values.GetValue(_valueIndex++).ToArray();
            _values[_valueCount++] = Unsafe.As<byte[], TValue>(ref bytes);
            return;
        }

        if ((uint)_valueIndex >= (uint)_buffer.Values.Count)
            throw new CorruptParquetException($"Column '{PropertyName}' has fewer dense values than levels.");
        _values[_valueCount++] = _buffer.Values.Values[_valueIndex++];
    }

    int CurrentBufferCount
        => _binary ? _binaryBuffer.Count : _buffer.Count;

    int CurrentRepetitionLevel
        => _binary ? _binaryBuffer.RepetitionLevels[_levelIndex] : _buffer.RepetitionLevels[_levelIndex];

    int CurrentDefinitionLevel
        => _binary ? _binaryBuffer.DefinitionLevels[_levelIndex] : _buffer.DefinitionLevels[_levelIndex];
}
