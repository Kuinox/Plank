using System.Buffers.Binary;
using Plank.Schema;

namespace Plank.Writing.Encoding;

/// <summary>Reusable level and physical value streams for one repeated column page.</summary>
sealed class RepeatedPageValues<TValue>(BufferWriterFactory buffers, LeafProjectionInfo schema) : IDisposable
    where TValue : notnull
{
    LevelRuns _repetitions = new(buffers, schema.MaxRepetitionLevel);
    LevelRuns _definitions = new(buffers, schema.MaxDefinitionLevel);
    internal readonly List<TValue> Values = [];
    internal int Count { get; private set; }

    internal (int Repetitions, int Definitions) WriteLevels(ref BufferWriter page, bool lengthPrefixes)
        => (_repetitions.WriteTo(ref page, lengthPrefixes), _definitions.WriteTo(ref page, lengthPrefixes));

    internal void Clear()
    {
        _repetitions.Reset();
        _definitions.Reset();
        Count = 0;
        Values.Clear();
    }

    public void Dispose()
    {
        _repetitions.Dispose();
        _definitions.Dispose();
    }

    internal void Flatten<TRow, TOptional, TProbe>(ReadOnlySpan<TRow> rows, LeafProjectionInfo projection,
        string columnName) where TProbe : IOptionalRow<TOptional, TValue>
    {
        if (!rows.IsEmpty && projection.MaxRepetitionLevel == 1)
        {
            var canBeNull = projection.MaxDefinitionLevel > projection.RepeatedDefinitionLevels[0];
            if (typeof(TRow) == typeof(TOptional[]) && !canBeNull && typeof(TOptional) != typeof(TValue))
                throw new InvalidOperationException($"Column '{columnName}' expects required list elements.");
            if (typeof(TRow) == typeof(TValue[]) && projection.ElementOptional &&
                (typeof(TOptional) != typeof(TValue) || TProbe.ValueRequired))
                throw new InvalidOperationException($"Column '{columnName}' has optional list elements; use a nullable row element type.");
            if (typeof(TRow) != typeof(TOptional[]) && typeof(TRow) != typeof(TValue[]))
                throw new InvalidOperationException($"Array type '{typeof(TRow)}' is incompatible with column '{columnName}'.");
        }

        for (var i = 0; i < rows.Length; i++)
            Visit<TOptional, TProbe>(rows[i], 0, 0, in projection, columnName);
    }

    void Visit<TOptional, TProbe>(object? node, int depth, int firstRepetition, in LeafProjectionInfo projection,
        string columnName) where TProbe : IOptionalRow<TOptional, TValue>
    {
        var repeated = projection.RepeatedDefinitionLevels[depth];
        if (node is null)
        {
            var parent = depth == 0 ? 0 : projection.RepeatedDefinitionLevels[depth - 1];
            if (repeated - parent < 2)
                throw new InvalidOperationException($"Column '{columnName}' has a null array at required collection depth {depth + 1}.");
            AddLevels(firstRepetition, repeated - 2);
            return;
        }

        if (node is not Array array || array.Rank != 1)
            throw new InvalidOperationException($"Column '{columnName}' expects a jagged array.");
        if (array.Length == 0)
        {
            if (!projection.IsList && projection.MaxRepetitionLevel == 1)
                throw new InvalidOperationException($"Empty rows are not supported for repeated column '{columnName}'.");
            AddLevels(firstRepetition, repeated - 1);
            return;
        }

        var repetition = depth + 1;
        if (repetition < projection.MaxRepetitionLevel)
        {
            if (array is not object?[] children)
                throw new InvalidOperationException($"Column '{columnName}' expects another array at depth {repetition}.");
            for (var i = 0; i < children.Length; i++)
                Visit<TOptional, TProbe>(children[i], repetition, i == 0 ? firstRepetition : repetition, in projection, columnName);
            return;
        }

        var max = projection.MaxDefinitionLevel;
        if (array is TOptional[] optional)
        {
            for (var i = 0; i < optional.Length; i++)
            {
                var present = TProbe.IsPresent(in optional[i]);
                if (present)
                    Values.Add(TProbe.GetValue(in optional[i]));
                else if (max == repeated)
                    throw new InvalidOperationException($"Column '{columnName}' has a null required leaf.");
                AddLevels(i == 0 ? firstRepetition : repetition, present ? max : repeated);
            }
        }
        else if (array is TValue[] required)
        {
            Values.AddRange(required.AsSpan());
            _repetitions.Add(firstRepetition, 1);
            if (required.Length > 1)
                _repetitions.Add(repetition, required.Length - 1);
            _definitions.Add(max, required.Length);
            Count = checked(Count + required.Length);
        }
        else if (depth > 0 && array is object?[] objects)
        {
            // The low-level nested writer also accepts jagged arrays of boxed physical values.
            for (var i = 0; i < objects.Length; i++)
            {
                var item = objects[i];
                if (item is TValue value)
                    Values.Add(value);
                else if (item is not null || max == repeated)
                    throw new InvalidOperationException($"Incompatible leaf value in '{columnName}'.");
                AddLevels(i == 0 ? firstRepetition : repetition, item is null ? repeated : max);
            }
        }
        else
            throw new InvalidOperationException($"Array of type '{array.GetType()}' is incompatible with column '{columnName}'.");
    }

    /// <summary>Appends levels and values that a generated row writer has already flattened.</summary>
    internal void AddFlattened(ReadOnlySpan<byte> repetitionLevels, ReadOnlySpan<byte> definitionLevels,
        ReadOnlySpan<TValue> values)
    {
        AddRuns(ref _repetitions, repetitionLevels);
        AddRuns(ref _definitions, definitionLevels);
        Count = checked(Count + repetitionLevels.Length);
        Values.AddRange(values);
    }

    static void AddRuns(ref LevelRuns runs, ReadOnlySpan<byte> levels)
    {
        var start = 0;
        while (start < levels.Length)
        {
            var value = levels[start];
            var length = levels[start..].IndexOfAnyExcept(value);
            if (length < 0)
                length = levels.Length - start;
            runs.Add(value, length);
            start += length;
        }
    }

    void AddLevels(int repetition, int definition)
    {
        _repetitions.Add(repetition, 1);
        _definitions.Add(definition, 1);
        Count = checked(Count + 1);
    }

    // The two streams occupy separate sections of a Parquet page. Encode runs as the tree
    // is visited, then copy their encoded bytes into the page in wire order.
    struct LevelRuns(BufferWriterFactory buffers, int maximum) : IDisposable
    {
        BufferWriter _encoded = buffers.CreatePageBufferWriter();
        readonly int _bitWidth = EncodingPrimitives.GetBitWidthFromMaxValue(maximum);
        int _value;
        int _length;

        internal void Add(int value, int length)
        {
            if (value == _value)
            {
                _length = checked(_length + length);
                return;
            }
            EncodingPrimitives.WriteRleRun(_value, _length, _bitWidth, ref _encoded);
            _value = value;
            _length = length;
        }

        internal int WriteTo(ref BufferWriter page, bool lengthPrefix)
        {
            EncodingPrimitives.WriteRleRun(_value, _length, _bitWidth, ref _encoded);
            _length = 0;
            if (lengthPrefix)
            {
                BinaryPrimitives.WriteInt32LittleEndian(page.GetSpan(sizeof(int)), _encoded.WrittenLength);
                page.Advance(sizeof(int));
            }
            page.CopyFrom(ref _encoded);
            return _encoded.WrittenLength;
        }

        internal void Reset()
        {
            _length = 0;
            _encoded.Reset();
        }

        public void Dispose() => _encoded.Dispose();
    }
}
