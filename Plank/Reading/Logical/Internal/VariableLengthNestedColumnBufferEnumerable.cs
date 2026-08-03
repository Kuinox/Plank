namespace Plank.Reading.Logical.Internal;

readonly struct VariableLengthNestedColumnBufferEnumerable<T>
{
    readonly NestedColumnBufferEnumerable<BinaryValueDescriptor> _inner;

    internal VariableLengthNestedColumnBufferEnumerable(NestedColumnBufferEnumerable<BinaryValueDescriptor> inner)
        => _inner = inner;

    internal Enumerator GetEnumerator()
        => new(_inner.GetEnumerator());

    internal struct Enumerator : IDisposable
    {
        NestedColumnBufferEnumerable<BinaryValueDescriptor>.Enumerator _inner;

        internal Enumerator(NestedColumnBufferEnumerable<BinaryValueDescriptor>.Enumerator inner)
            => _inner = inner;

        internal NestedColumnBuffer<T> Current
        {
            get
            {
                var current = _inner.Current;
                var values = current.Values;
                return new NestedColumnBuffer<T>(
                    new ColumnBuffer<T>(values.NativeValues, values.ValueCount, isVariableLength: true),
                    current.NativeLevels, current.Count, current.RowCount, current.StartsWithContinuation,
                    current.MaxRepetitionLevel, current.MaxDefinitionLevel);
            }
        }

        internal bool MoveNext()
            => _inner.MoveNext();

        public void Dispose()
            => _inner.Dispose();
    }
}
