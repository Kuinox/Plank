namespace Plank.Reading.Logical.Internal;

readonly struct VariableLengthColumnBufferEnumerable<T>
{
    readonly ColumnBufferEnumerable<BinaryValueDescriptor> _inner;

    internal VariableLengthColumnBufferEnumerable(ColumnBufferEnumerable<BinaryValueDescriptor> inner)
        => _inner = inner;

    internal Enumerator GetEnumerator()
        => new(_inner.GetEnumerator());

    internal struct Enumerator : IDisposable
    {
        ColumnBufferEnumerable<BinaryValueDescriptor>.Enumerator _inner;

        internal Enumerator(ColumnBufferEnumerable<BinaryValueDescriptor>.Enumerator inner)
            => _inner = inner;

        internal ColumnBuffer<T> Current
        {
            get
            {
                var current = _inner.Current;
                return new ColumnBuffer<T>(current.NativeValues, current.ValueCount,
                    isVariableLength: true);
            }
        }

        internal bool MoveNext()
            => _inner.MoveNext();

        public void Dispose()
            => _inner.Dispose();
    }
}
