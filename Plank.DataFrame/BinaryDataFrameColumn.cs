using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Microsoft.Data.Analysis;

namespace Plank.DataFrame;

/// <summary>A DataFrame column whose values are nullable binary payloads.</summary>
public sealed class BinaryDataFrameColumn : DataFrameColumn, IEnumerable<byte[]?>
{
    readonly List<byte[]?> _values;
    long _nullCount;

    /// <summary>Creates an empty binary column.</summary>
    public BinaryDataFrameColumn(string name)
        : base(name, 0, typeof(byte[]))
    {
        _values = [];
    }

    /// <summary>Creates a binary column containing <paramref name="values"/>.</summary>
    public BinaryDataFrameColumn(string name, IEnumerable<byte[]?> values)
        : this(name)
    {
        ArgumentNullException.ThrowIfNull(values);
        foreach (var value in values)
            Append(value);
    }

    internal BinaryDataFrameColumn(string name, long length)
        : base(name, length, typeof(byte[]))
    {
        var count = checked((int)length);
        _values = new List<byte[]?>(count);
        for (var i = 0; i < count; i++)
            _values.Add(null);
        _nullCount = length;
    }

    /// <inheritdoc />
    public override long NullCount
        => _nullCount;

    /// <summary>Gets or sets the payload at <paramref name="rowIndex"/>.</summary>
    [SuppressMessage("Performance", "CA1819:Properties should not return arrays",
        Justification = "The column represents Parquet binary values as byte arrays.")]
    public new byte[]? this[long rowIndex]
    {
        get => _values[GetIndex(rowIndex)];
        set => SetBinaryValue(rowIndex, value);
    }

    /// <summary>Appends a payload to the column.</summary>
    public void Append(byte[]? value)
    {
        _values.Add(value);
        Length++;
        if (value is null)
            _nullCount++;
    }

    /// <inheritdoc />
    public IEnumerator<byte[]?> GetEnumerator()
        => _values.GetEnumerator();

    protected override object? GetValue(long rowIndex)
        => this[rowIndex];

    protected override IReadOnlyList<object?> GetValues(long startIndex, int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        var start = GetSliceStart(startIndex, length);
        var values = new object?[length];
        for (var i = 0; i < values.Length; i++)
            values[i] = _values[start + i];
        return values;
    }

    protected override void SetValue(long rowIndex, object? value)
    {
        if (value is not null and not byte[])
            throw new ArgumentException($"Binary columns accept '{typeof(byte[])}' values.", nameof(value));
        SetBinaryValue(rowIndex, (byte[]?)value);
    }

    protected override IEnumerator GetEnumeratorCore()
        => GetEnumerator();

    protected override void Resize(long length)
    {
        if (length < 0 || length > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(length));
        if (length == Length)
            return;

        var newLength = checked((int)length);
        if (length < Length)
        {
            for (var i = newLength; i < _values.Count; i++)
                if (_values[i] is null)
                    _nullCount--;
            _values.RemoveRange(newLength, _values.Count - newLength);
            Length = length;
            return;
        }

        var added = length - Length;
        for (var i = Length; i < length; i++)
            _values.Add(null);
        Length = length;
        _nullCount += added;
    }

    protected override DataFrameColumn CloneImplementation(DataFrameColumn mapIndices, bool invertMapIndices,
        long numberOfNullsToAppend)
    {
        var clone = mapIndices is null
            ? CloneValues()
            : CloneMapped(mapIndices, invertMapIndices);
        for (var i = 0L; i < numberOfNullsToAppend; i++)
            clone.Append(null);
        return clone;
    }

    protected override DataFrameColumn CloneImplementation(long numberOfNullsToAppend = 0)
    {
        var clone = CloneValues();
        for (var i = 0L; i < numberOfNullsToAppend; i++)
            clone.Append(null);
        return clone;
    }

    /// <inheritdoc />
    public override Dictionary<long, ICollection<long>> GetGroupedOccurrences(DataFrameColumn other,
        out HashSet<long> otherColumnNullIndices)
    {
        if (other is not BinaryDataFrameColumn binary)
            throw new ArgumentException("The other column must be a binary column.", nameof(other));

        otherColumnNullIndices = [];
        var occurrences = new Dictionary<byte[], List<long>>(ByteArrayComparer.Instance);
        for (var i = 0L; i < binary.Length; i++)
        {
            var value = binary[i];
            if (value is null)
            {
                otherColumnNullIndices.Add(i);
                continue;
            }

            if (!occurrences.TryGetValue(value, out var indices))
            {
                indices = [];
                occurrences.Add(value, indices);
            }
            indices.Add(i);
        }

        var grouped = new Dictionary<long, ICollection<long>>();
        for (var i = 0L; i < Length; i++)
            if (this[i] is { } value && occurrences.TryGetValue(value, out var indices))
                grouped.Add(i, indices);
        return grouped;
    }

    protected override DataFrameColumn FillNullsImplementation(object value, bool inPlace)
    {
        if (value is not byte[] replacement)
            throw new ArgumentException($"Binary columns accept '{typeof(byte[])}' values.", nameof(value));

        var column = inPlace ? this : CloneValues();
        for (var i = 0; i < column._values.Count; i++)
            if (column._values[i] is null)
                column._values[i] = replacement;
        column._nullCount = 0;
        return column;
    }

    protected override DataFrameColumn DropNullsImplementation()
        => new BinaryDataFrameColumn(Name, _values.Where(static value => value is not null));

    protected override PrimitiveDataFrameColumn<long> GetSortIndices(bool ascending, bool putNullValuesLast)
    {
        var nonNull = new List<long>(checked((int)(Length - NullCount)));
        var nulls = new List<long>(checked((int)NullCount));
        for (var i = 0L; i < Length; i++)
            if (this[i] is null)
                nulls.Add(i);
            else
                nonNull.Add(i);

        nonNull.Sort((left, right) => ByteArrayComparer.Instance.Compare(this[left], this[right]));
        if (!ascending)
            nonNull.Reverse();

        var result = putNullValuesLast ? nonNull.Concat(nulls) : nulls.Concat(nonNull);
        return new PrimitiveDataFrameColumn<long>("SortIndices", result);
    }

    BinaryDataFrameColumn CloneValues()
        => new(Name, _values);

    BinaryDataFrameColumn CloneMapped(DataFrameColumn mapIndices, bool invertMapIndices)
    {
        if (mapIndices.DataType == typeof(bool))
        {
            var selected = new BinaryDataFrameColumn(Name);
            for (var i = 0L; i < mapIndices.Length && i < Length; i++)
                if (mapIndices[i] is true)
                    selected.Append(this[i]);
            return selected;
        }

        if (mapIndices.DataType != typeof(int) && mapIndices.DataType != typeof(long))
            throw new ArgumentException("Map indices must contain bool, int, or long values.", nameof(mapIndices));

        var mapped = new BinaryDataFrameColumn(Name);
        for (var i = 0L; i < mapIndices.Length; i++)
        {
            var mapIndex = invertMapIndices ? mapIndices.Length - 1 - i : i;
            mapped.Append(mapIndices[mapIndex] is null
                ? null
                : this[Convert.ToInt64(mapIndices[mapIndex], CultureInfo.InvariantCulture)]);
        }
        return mapped;
    }

    void SetBinaryValue(long rowIndex, byte[]? value)
    {
        var index = GetIndex(rowIndex);
        var previous = _values[index];
        if (previous is null && value is not null)
            _nullCount--;
        else if (previous is not null && value is null)
            _nullCount++;
        _values[index] = value;
    }

    int GetIndex(long rowIndex)
    {
        if ((ulong)rowIndex >= (ulong)Length)
            throw new ArgumentOutOfRangeException(nameof(rowIndex));
        return checked((int)rowIndex);
    }

    int GetSliceStart(long startIndex, int length)
    {
        if (startIndex < 0 || startIndex > Length - length)
            throw new ArgumentOutOfRangeException(nameof(startIndex));
        return checked((int)startIndex);
    }

    sealed class ByteArrayComparer : IEqualityComparer<byte[]>, IComparer<byte[]?>
    {
        public static readonly ByteArrayComparer Instance = new();

        public bool Equals(byte[]? left, byte[]? right)
            => ReferenceEquals(left, right) || left is not null && right is not null && left.AsSpan().SequenceEqual(right);

        public int GetHashCode(byte[] value)
        {
            var hash = new HashCode();
            for (var i = 0; i < value.Length; i++)
                hash.Add(value[i]);
            return hash.ToHashCode();
        }

        public int Compare(byte[]? left, byte[]? right)
        {
            if (ReferenceEquals(left, right))
                return 0;
            if (left is null)
                return -1;
            if (right is null)
                return 1;
            return left.AsSpan().SequenceCompareTo(right);
        }
    }
}
