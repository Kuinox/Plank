namespace Plank.Writing;

public sealed class PageList
{
    Page[] _pages;
    int _count;

    public PageList(int initialCapacity)
    {
        if (initialCapacity < 0)
            throw new ArgumentOutOfRangeException(nameof(initialCapacity), initialCapacity,
                "Initial capacity must be non-negative.");
        _pages = new Page[initialCapacity];
        _count = 0;
    }

    public int Count
        => _count;

    public ref Page Add()
    {
        EnsureCapacity(_count + 1);
        return ref _pages[_count++];
    }

    public ref Page this[int index]
    {
        get
        {
            if ((uint)index >= (uint)_count)
                throw new ArgumentOutOfRangeException(nameof(index), index, "Page index is out of range.");
            return ref _pages[index];
        }
    }

    public void Clear()
        => _count = 0;

    public void RemoveLast()
    {
        if (_count == 0)
            throw new InvalidOperationException("Cannot remove a page from an empty page list.");
        _count--;
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
