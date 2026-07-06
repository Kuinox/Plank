using System.Runtime.InteropServices;
using Plank.Reading.Logical;

namespace Plank.RowApi;

sealed class RowApiColumnReadState<T> : RowApiColumnReadState
{
    readonly T[] _missing;
    ColumnPageEnumerable<T>.Enumerator _pages;
    ReadOnlyMemory<T> _page;
    bool _pagesOpen;

    internal RowApiColumnReadState(RowApiColumnDescriptor<T> descriptor)
        : base(descriptor)
    {
        _missing = [default!];
        _pages = default;
        _page = default;
        CurrentArray = [];
        CurrentIndex = -1;
        _pagesOpen = false;
    }

    internal T[] CurrentArray;

    internal override void ResetPageState()
    {
        DisposePages();
        _page = default;
        CurrentArray = [];
        CurrentIndex = -1;
    }

    internal override void SetMissingValue()
    {
        DisposePages();
        _page = default;
        CurrentArray = _missing;
        CurrentIndex = 0;
    }

    internal override void Open(RowGroupReader rowGroup)
    {
        ArgumentNullException.ThrowIfNull(rowGroup);

        DisposePages();
        _pages = rowGroup.Column<T>(Ordinal).Pages.GetEnumerator();
        _pagesOpen = true;
        _page = default;
        CurrentArray = [];
        CurrentIndex = -1;
    }

    internal override void Advance()
    {
        if (!Projected)
            return;

        CurrentIndex++;
        while ((uint)CurrentIndex >= (uint)_page.Length)
        {
            if (!_pages.MoveNext())
                throw new InvalidDataException($"Column '{PropertyName}' ended before the row group was complete.");

            _page = _pages.Current.Values;
            CurrentArray = GetArray(_page, PropertyName);
            CurrentIndex = 0;
            if (_page.Length == 0)
                CurrentIndex = -1;
        }
    }

    internal override void DisposePages()
    {
        if (!_pagesOpen)
            return;

        _pages.Dispose();
        _pagesOpen = false;
    }

    static T[] GetArray(ReadOnlyMemory<T> memory, string propertyName)
    {
        if (MemoryMarshal.TryGetArray(memory, out var segment) && segment.Array is not null)
            return segment.Array;

        throw new InvalidOperationException($"Column '{propertyName}' page is not array-backed.");
    }
}
