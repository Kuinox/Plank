using Plank.Schema;

namespace Plank.Untyped;

sealed class UntypedSchemaPlan
{
    internal enum StepKind
    {
        Group,
        List,
        Map,
        Leaf
    }

    internal enum MapSide
    {
        None,
        Key,
        Value
    }

    internal readonly record struct PathStep(StepKind Kind, string? Name, int PresenceDefinitionLevel,
        int EntryDefinitionLevel, int RepetitionDepth, MapSide Side = MapSide.None);

    internal sealed class LeafPlan
    {
        internal LeafPlan(PathStep[] steps, int repeatedDepth, bool scalarNullable, int[] entryDefinitionLevels)
        {
            Steps = steps;
            RepeatedDepth = repeatedDepth;
            ScalarNullable = scalarNullable;
            EntryDefinitionLevels = entryDefinitionLevels;
        }

        internal LeafColumn Column = null!;

        internal PathStep[] Steps { get; }

        internal int RepeatedDepth { get; }

        internal bool ScalarNullable { get; }

        internal int[] EntryDefinitionLevels { get; }
    }

    readonly LeafPlan[] _leaves;

    internal UntypedSchemaPlan(ParquetSchema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        Schema = schema;
        var leaves = new List<LeafPlan>(schema.LeafColumns.Length);
        var path = new List<PathStep>(8);
        for (var i = 0; i < schema.Definitions.Length; i++)
            BuildNode(schema.Definitions[i], named: true, definitionLevel: 0, repetitionDepth: 0,
                optionalSinceRepetition: false, path, leaves);

        if (leaves.Count != schema.LeafColumns.Length)
            throw new InvalidOperationException("Untyped schema projection did not match the flattened leaf count.");
        for (var i = 0; i < leaves.Count; i++)
        {
            leaves[i].Column = schema.LeafColumns[i];
            if (leaves[i].RepeatedDepth != leaves[i].Column.MaxRepetitionLevel ||
                leaves[i].Steps[^1].PresenceDefinitionLevel != leaves[i].Column.MaxDefinitionLevel)
                throw new InvalidOperationException(
                    $"Untyped schema levels did not match leaf '{leaves[i].Column.Path}'.");
        }
        _leaves = leaves.ToArray();
    }

    internal ParquetSchema Schema { get; }

    internal ReadOnlySpan<LeafPlan> Leaves
        => _leaves;

    internal Dictionary<string, object?> CanonicalizeRow(IReadOnlyDictionary<string, object?> row)
    {
        ArgumentNullException.ThrowIfNull(row);
        var result = new Dictionary<string, object?>(Schema.Definitions.Length, StringComparer.Ordinal);
        for (var i = 0; i < Schema.Definitions.Length; i++)
        {
            var definition = Schema.Definitions[i];
            row.TryGetValue(definition.Name, out var value);
            result[definition.Name] = CanonicalizeNode(definition, value, definition.Name);
        }
        return result;
    }

    internal object? ExtractLeaf(Dictionary<string, object?> row, LeafPlan leaf)
        => Extract(row, leaf.Steps, 0);

    internal void ApplyLeaf(LeafPlan leaf, Dictionary<string, object?> row, ReadOnlySpan<int> repeatedIndexes,
        int definitionLevel, object? value)
    {
        object? container = row;
        var direct = default(ValueSlot);
        var hasDirect = false;
        for (var i = 0; i < leaf.Steps.Length; i++)
        {
            var step = leaf.Steps[i];
            var slot = GetSlot(container, direct, hasDirect, step.Name);
            switch (step.Kind)
            {
                case StepKind.Group:
                    if (definitionLevel < step.PresenceDefinitionLevel)
                    {
                        slot.Value = null;
                        return;
                    }
                    var group = slot.Value as Dictionary<string, object?>;
                    if (group is null)
                    {
                        if (slot.Value is not null)
                            throw new InvalidOperationException("Untyped row paths disagree about a group value.");
                        group = new Dictionary<string, object?>(StringComparer.Ordinal);
                        slot.Value = group;
                    }
                    container = group;
                    direct = default;
                    hasDirect = false;
                    break;
                case StepKind.List:
                    if (definitionLevel < step.PresenceDefinitionLevel)
                    {
                        slot.Value = null;
                        return;
                    }
                    var list = slot.Value as List<object?>;
                    if (list is null)
                    {
                        if (slot.Value is not null)
                            throw new InvalidOperationException("Untyped row paths disagree about a list value.");
                        list = [];
                        slot.Value = list;
                    }
                    if (definitionLevel < step.EntryDefinitionLevel)
                        return;
                    var listIndex = repeatedIndexes[step.RepetitionDepth];
                    if (listIndex < 0)
                        throw new InvalidOperationException("Repeated list entry did not have a materialized index.");
                    EnsureListIndex(list, listIndex);
                    direct = ValueSlot.ForList(list, listIndex);
                    hasDirect = true;
                    container = null;
                    break;
                case StepKind.Map:
                    if (definitionLevel < step.PresenceDefinitionLevel)
                    {
                        slot.Value = null;
                        return;
                    }
                    var map = slot.Value as UntypedMap;
                    if (map is null)
                    {
                        if (slot.Value is not null)
                            throw new InvalidOperationException("Untyped row paths disagree about a map value.");
                        map = new UntypedMap();
                        slot.Value = map;
                    }
                    if (definitionLevel < step.EntryDefinitionLevel)
                        return;
                    var mapIndex = repeatedIndexes[step.RepetitionDepth];
                    if (mapIndex < 0)
                        throw new InvalidOperationException("Repeated map entry did not have a materialized index.");
                    while (map.Entries.Count <= mapIndex)
                        map.Entries.Add(new UntypedMap.Entry());
                    direct = ValueSlot.ForMap(map.Entries[mapIndex], step.Side);
                    hasDirect = true;
                    container = null;
                    break;
                case StepKind.Leaf:
                    slot.Value = value;
                    return;
                default:
                    throw new InvalidOperationException($"Unsupported untyped path step '{step.Kind}'.");
            }
        }
        throw new InvalidOperationException("Untyped leaf path did not terminate in a leaf.");
    }

    static void BuildNode(ColumnDefinition node, bool named, int definitionLevel, int repetitionDepth,
        bool optionalSinceRepetition, List<PathStep> path, List<LeafPlan> leaves)
    {
        var repetition = Normalize(node.Repetition);
        if (repetition == ParquetRepetition.Repeated)
        {
            var entryDefinitionLevel = checked(definitionLevel + 1);
            var depth = checked(repetitionDepth + 1);
            path.Add(new PathStep(StepKind.List, named ? node.Name : null, definitionLevel,
                entryDefinitionLevel, depth));
            try
            {
                BuildRepeatedNode(node, entryDefinitionLevel, depth, path, leaves);
            }
            finally
            {
                path.RemoveAt(path.Count - 1);
            }
            return;
        }

        var nextDefinitionLevel = definitionLevel + (repetition == ParquetRepetition.Optional ? 1 : 0);
        switch (node.Kind)
        {
            case NodeKind.Group:
                path.Add(new PathStep(StepKind.Group, named ? node.Name : null, nextDefinitionLevel, 0, 0));
                try
                {
                    for (var i = 0; i < node.Children.Length; i++)
                        BuildNode(node.Children[i], named: true, nextDefinitionLevel, repetitionDepth,
                            optionalSinceRepetition || repetition == ParquetRepetition.Optional, path, leaves);
                }
                finally
                {
                    path.RemoveAt(path.Count - 1);
                }
                return;
            case NodeKind.List:
                if (node.Children.Length != 1)
                    throw new NotSupportedException($"List '{node.Name}' must contain exactly one element definition.");
                AddList(node, named, nextDefinitionLevel, repetitionDepth, path, leaves);
                return;
            case NodeKind.Map:
                if (node.Children.Length is < 1 or > 2)
                    throw new NotSupportedException($"Map '{node.Name}' must contain a key and an optional value definition.");
                AddMap(node, named, nextDefinitionLevel, repetitionDepth, path, leaves);
                return;
            case NodeKind.Leaf:
                path.Add(new PathStep(StepKind.Leaf, named ? node.Name : null, nextDefinitionLevel, 0, 0));
                try
                {
                    AddLeaf(path, leaves, repetitionDepth,
                        optionalSinceRepetition || repetition == ParquetRepetition.Optional);
                }
                finally
                {
                    path.RemoveAt(path.Count - 1);
                }
                return;
            default:
                throw new NotSupportedException($"Unsupported schema node kind '{node.Kind}'.");
        }
    }

    static void BuildRepeatedNode(ColumnDefinition node, int definitionLevel, int repetitionDepth,
        List<PathStep> path, List<LeafPlan> leaves)
    {
        switch (node.Kind)
        {
            case NodeKind.Leaf:
                path.Add(new PathStep(StepKind.Leaf, null, definitionLevel, 0, 0));
                try
                {
                    AddLeaf(path, leaves, repetitionDepth, scalarNullable: false);
                }
                finally
                {
                    path.RemoveAt(path.Count - 1);
                }
                return;
            case NodeKind.Group:
                path.Add(new PathStep(StepKind.Group, null, definitionLevel, 0, 0));
                try
                {
                    for (var i = 0; i < node.Children.Length; i++)
                        BuildNode(node.Children[i], named: true, definitionLevel, repetitionDepth,
                            optionalSinceRepetition: false, path, leaves);
                }
                finally
                {
                    path.RemoveAt(path.Count - 1);
                }
                return;
            default:
                throw new NotSupportedException("Legacy repeated list and map annotations are not supported by the untyped adapter.");
        }
    }

    static void AddList(ColumnDefinition node, bool named, int containerDefinitionLevel, int repetitionDepth,
        List<PathStep> path, List<LeafPlan> leaves)
    {
        var depth = checked(repetitionDepth + 1);
        var entryDefinitionLevel = checked(containerDefinitionLevel + 1);
        path.Add(new PathStep(StepKind.List, named ? node.Name : null, containerDefinitionLevel,
            entryDefinitionLevel, depth));
        try
        {
            BuildNode(node.Children[0], named: false, entryDefinitionLevel, depth,
                optionalSinceRepetition: false, path, leaves);
        }
        finally
        {
            path.RemoveAt(path.Count - 1);
        }
    }

    static void AddMap(ColumnDefinition node, bool named, int containerDefinitionLevel, int repetitionDepth,
        List<PathStep> path, List<LeafPlan> leaves)
    {
        var depth = checked(repetitionDepth + 1);
        var entryDefinitionLevel = checked(containerDefinitionLevel + 1);
        for (var i = 0; i < node.Children.Length; i++)
        {
            var side = i == 0 ? MapSide.Key : MapSide.Value;
            path.Add(new PathStep(StepKind.Map, named ? node.Name : null, containerDefinitionLevel,
                entryDefinitionLevel, depth, side));
            try
            {
                BuildNode(node.Children[i], named: false, entryDefinitionLevel, depth,
                    optionalSinceRepetition: false, path, leaves);
            }
            finally
            {
                path.RemoveAt(path.Count - 1);
            }
        }
    }

    static void AddLeaf(List<PathStep> path, List<LeafPlan> leaves, int repeatedDepth, bool scalarNullable)
    {
        var entryDefinitionLevels = new int[repeatedDepth + 1];
        for (var i = 0; i < path.Count; i++)
        {
            var step = path[i];
            if (step.Kind is StepKind.List or StepKind.Map)
                entryDefinitionLevels[step.RepetitionDepth] = step.EntryDefinitionLevel;
        }
        for (var depth = 1; depth < entryDefinitionLevels.Length; depth++)
            if (entryDefinitionLevels[depth] == 0)
                throw new InvalidOperationException($"Untyped leaf path is missing repetition depth {depth}.");
        leaves.Add(new LeafPlan(path.ToArray(), repeatedDepth, scalarNullable, entryDefinitionLevels));
    }

    static object? CanonicalizeNode(ColumnDefinition node, object? value, string path)
    {
        if (value is null)
        {
            if (Normalize(node.Repetition) == ParquetRepetition.Repeated)
                return new List<object?>();
            if (Normalize(node.Repetition) == ParquetRepetition.Required)
                throw new ArgumentException($"Required value '{path}' is missing or null.");
            return null;
        }

        if (Normalize(node.Repetition) == ParquetRepetition.Repeated)
            return CanonicalizeSequence(node with { Repetition = ParquetRepetition.Required }, value, path);

        return node.Kind switch
        {
            NodeKind.Leaf => value,
            NodeKind.Group => CanonicalizeGroup(node, value, path),
            NodeKind.List => CanonicalizeList(node, value, path),
            NodeKind.Map => CanonicalizeMap(node, value, path),
            _ => throw new NotSupportedException($"Unsupported schema node kind '{node.Kind}'.")
        };
    }

    static Dictionary<string, object?> CanonicalizeGroup(ColumnDefinition node, object value, string path)
    {
        if (value is not IReadOnlyDictionary<string, object?> dictionary)
            throw new ArgumentException($"Group '{path}' must be an IReadOnlyDictionary<string, object?>.");
        var result = new Dictionary<string, object?>(node.Children.Length, StringComparer.Ordinal);
        for (var i = 0; i < node.Children.Length; i++)
        {
            var child = node.Children[i];
            dictionary.TryGetValue(child.Name, out var childValue);
            result[child.Name] = CanonicalizeNode(child, childValue, $"{path}.{child.Name}");
        }
        return result;
    }

    static List<object?> CanonicalizeList(ColumnDefinition node, object value, string path)
    {
        if (node.Children.Length != 1)
            throw new NotSupportedException($"List '{path}' must contain exactly one element definition.");
        return CanonicalizeSequence(node.Children[0], value, path);
    }

    static List<object?> CanonicalizeSequence(ColumnDefinition element, object value, string path)
    {
        if (value is string || value is not System.Collections.IEnumerable sequence)
            throw new ArgumentException($"Repeated value '{path}' must implement IEnumerable.");
        var result = new List<object?>();
        var index = 0;
        foreach (var item in sequence)
        {
            result.Add(CanonicalizeNode(element, item, $"{path}[{index}]"));
            index++;
        }
        return result;
    }

    static UntypedMap CanonicalizeMap(ColumnDefinition node, object value, string path)
    {
        if (node.Children.Length is < 1 or > 2)
            throw new NotSupportedException($"Map '{path}' must contain a key and an optional value definition.");
        var key = node.Children[0];
        var valueNode = node.Children.Length == 2 ? node.Children[1] : null;
        return UntypedMap.FromObject(value,
            entryKey => CanonicalizeNode(key, entryKey, $"{path}.key"),
            entryValue => valueNode is null ? null : CanonicalizeNode(valueNode, entryValue, $"{path}.value"));
    }

    static object? Extract(object? current, ReadOnlySpan<PathStep> steps, int index)
    {
        if (index == steps.Length)
            return current;
        var step = steps[index];
        var value = step.Name is null ? current : GetNamedValue(current, step.Name);
        if (value is null)
            return null;
        switch (step.Kind)
        {
            case StepKind.Group:
                return Extract(value, steps, index + 1);
            case StepKind.List:
            {
                var list = (List<object?>)value;
                var result = new object?[list.Count];
                for (var i = 0; i < result.Length; i++)
                    result[i] = Extract(list[i], steps, index + 1);
                return result;
            }
            case StepKind.Map:
            {
                var map = (UntypedMap)value;
                var result = new object?[map.Entries.Count];
                for (var i = 0; i < result.Length; i++)
                {
                    var entry = map.Entries[i];
                    result[i] = Extract(step.Side == MapSide.Key ? entry.Key : entry.Value, steps, index + 1);
                }
                return result;
            }
            case StepKind.Leaf:
                return value;
            default:
                throw new InvalidOperationException($"Unsupported untyped path step '{step.Kind}'.");
        }
    }

    static object? GetNamedValue(object? container, string name)
    {
        if (container is not Dictionary<string, object?> dictionary)
            throw new InvalidOperationException($"Untyped path expected a group while resolving '{name}'.");
        return dictionary.TryGetValue(name, out var value) ? value : null;
    }

    static ValueSlot GetSlot(object? container, ValueSlot direct, bool hasDirect, string? name)
    {
        if (name is null)
        {
            if (!hasDirect)
                throw new InvalidOperationException("Untyped path is missing a direct value slot.");
            return direct;
        }
        if (container is not Dictionary<string, object?> dictionary)
            throw new InvalidOperationException($"Untyped path expected a group while resolving '{name}'.");
        return ValueSlot.ForDictionary(dictionary, name);
    }

    static void EnsureListIndex(List<object?> list, int index)
    {
        while (list.Count <= index)
            list.Add(null);
    }

    static ParquetRepetition Normalize(ParquetRepetition repetition)
        => repetition == ParquetRepetition.Unspecified ? ParquetRepetition.Required : repetition;

    readonly struct ValueSlot
    {
        readonly Dictionary<string, object?>? _dictionary;
        readonly string? _name;
        readonly List<object?>? _list;
        readonly int _index;
        readonly UntypedMap.Entry? _entry;
        readonly MapSide _side;

        ValueSlot(Dictionary<string, object?> dictionary, string name)
        {
            _dictionary = dictionary;
            _name = name;
            _list = null;
            _index = 0;
            _entry = null;
            _side = MapSide.None;
        }

        ValueSlot(List<object?> list, int index)
        {
            _dictionary = null;
            _name = null;
            _list = list;
            _index = index;
            _entry = null;
            _side = MapSide.None;
        }

        ValueSlot(UntypedMap.Entry entry, MapSide side)
        {
            _dictionary = null;
            _name = null;
            _list = null;
            _index = 0;
            _entry = entry;
            _side = side;
        }

        internal object? Value
        {
            get
            {
                if (_dictionary is not null)
                    return _dictionary.TryGetValue(_name!, out var value) ? value : null;
                if (_list is not null)
                    return _list[_index];
                if (_entry is not null)
                    return _side == MapSide.Key ? _entry.Key : _entry.Value;
                throw new InvalidOperationException("Untyped value slot is not initialized.");
            }
            set
            {
                if (_dictionary is not null)
                    _dictionary[_name!] = value;
                else if (_list is not null)
                    _list[_index] = value;
                else if (_entry is not null)
                {
                    if (_side == MapSide.Key)
                        _entry.Key = value;
                    else
                        _entry.Value = value;
                }
                else
                    throw new InvalidOperationException("Untyped value slot is not initialized.");
            }
        }

        internal static ValueSlot ForDictionary(Dictionary<string, object?> dictionary, string name)
            => new(dictionary, name);

        internal static ValueSlot ForList(List<object?> list, int index)
            => new(list, index);

        internal static ValueSlot ForMap(UntypedMap.Entry entry, MapSide side)
            => new(entry, side);
    }
}
