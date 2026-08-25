using Plank.Reading.Logical.Internal;
namespace Plank.Reading.Logical;

/// <summary>Owns the pooled buffers used to expose a column chunk's data-page metadata.</summary>
/// <remarks>Dispose the collection when inspection is complete. Enumeration does not allocate.</remarks>
public sealed class ParquetDataPageMetadataCollection : IDisposable
{
    PageMetadataHandle _handle;

    internal ParquetDataPageMetadataCollection(PageMetadataHandle handle)
        => _handle = handle;

    public int Count
    {
        get
        {
            _handle.ThrowIfDisposed();
            return _handle.Count;
        }
    }

    public ParquetBoundaryOrder BoundaryOrder
    {
        get
        {
            _handle.ThrowIfDisposed();
            return _handle.BoundaryOrder;
        }
    }

    public ParquetDataPageMetadata this[int index]
        => _handle.GetMetadata(index);

    public Enumerator GetEnumerator()
    {
        _handle.ThrowIfDisposed();
        return new Enumerator(this);
    }

    public void Dispose()
        => _handle.Dispose();

    internal ParquetDataPageMetadata GetMetadata(int index)
        => _handle.GetMetadata(index);

    void ThrowIfDisposed()
        => _handle.ThrowIfDisposed();

    public struct Enumerator
    {
        readonly ParquetDataPageMetadataCollection? _owner;
        int _index;

        internal Enumerator(ParquetDataPageMetadataCollection owner)
        {
            _owner = owner;
            _index = -1;
        }

        public ParquetDataPageMetadata Current
        {
            get
            {
                var owner = _owner ?? throw new InvalidOperationException("The enumerator is not initialized.");
                if ((uint)_index >= (uint)owner.Count)
                    throw new InvalidOperationException("The enumerator is not positioned on a page.");
                return owner.GetMetadata(_index);
            }
        }

        public bool MoveNext()
        {
            var owner = _owner ?? throw new InvalidOperationException("The enumerator is not initialized.");
            owner.ThrowIfDisposed();
            if (_index + 1 >= owner.Count)
                return false;
            _index++;
            return true;
        }
    }
}
