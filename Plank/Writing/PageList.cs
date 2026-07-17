namespace Plank.Writing;

internal sealed class PageList
{
    Page[] _pages;

    public PageList(uint initialCapacity)
    {
        if (initialCapacity > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(initialCapacity), initialCapacity,
                $"Initial capacity must be <= {int.MaxValue}.");

        _pages = new Page[checked((int)initialCapacity)];
        Count = 0;
    }

    public int Count { get; private set; }

    public ref Page Add()
    {
        EnsureCapacity(Count + 1);
        return ref _pages[Count++];
    }

    public ref Page this[int index]
    {
        get
        {
            if ((uint)index >= (uint)Count)
                throw new ArgumentOutOfRangeException(nameof(index), index, "Page index is out of range.");
            return ref _pages[index];
        }
    }

    public void Clear()
        => Count = 0;

    public void RemoveLast()
    {
        if (Count == 0)
            throw new InvalidOperationException("Cannot remove a page from an empty page list.");
        Count--;
    }

    void EnsureCapacity(int required)
    {
        if (required <= _pages.Length)
            return;

        var previousCapacity = _pages.Length;
        var newCapacity = previousCapacity == 0 ? 4 : previousCapacity;
        while (newCapacity < required)
            newCapacity = checked(newCapacity * 2);

        Array.Resize(ref _pages, newCapacity);
        ParquetMetrics.PageListAllocations.Add(1);
    }
}
