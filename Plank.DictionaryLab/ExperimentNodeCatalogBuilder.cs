using System.Reflection;

namespace Plank.DictionaryLab;

static class ExperimentNodeCatalogBuilder
{
    public static IReadOnlyList<DictionaryNode<T>> Build<T>()
    {
        var assembly = typeof(IIndexDictionary<>).Assembly;
        var dictionaryType = typeof(IExperimentDictionary<T>);
        var candidates = assembly
            .GetTypes()
            .Where(static type => type is { IsClass: true, IsAbstract: false, IsGenericTypeDefinition: false })
            .Where(type => dictionaryType.IsAssignableFrom(type))
            .ToArray();

        var idByType = new Dictionary<Type, string>(candidates.Length);
        for (var i = 0; i < candidates.Length; i++)
        {
            var type = candidates[i];
            var id = ReadStringProperty(type, nameof(IExperimentDictionary<T>.ExperimentName));
            idByType.Add(type, id);
        }

        var nodes = new List<DictionaryNode<T>>(candidates.Length);
        for (var i = 0; i < candidates.Length; i++)
        {
            var type = candidates[i];
            var id = idByType[type];
            var parentType = ReadTypeProperty(type, nameof(IExperimentDictionary<T>.ParentExperimentType));
            var parentId = parentType is null ? null : idByType[parentType];
            var description = ReadStringProperty(type, nameof(IExperimentDictionary<T>.ExperimentDescription));
            var baseId = id.StartsWith("tree.", StringComparison.Ordinal) ? "btree" : "hashtable";
            nodes.Add(new DictionaryNode<T>(
                Id: id,
                BaseId: baseId,
                ParentId: parentId,
                Notes: description,
                Create: () => (IIndexDictionary<T>)Activator.CreateInstance(type)!));
        }

        nodes.Sort(static (left, right) => StringComparer.Ordinal.Compare(left.Id, right.Id));
        return nodes;
    }

    static string ReadStringProperty(Type type, string propertyName)
    {
        var property = type.GetProperty(propertyName, BindingFlags.Public | BindingFlags.Static);
        if (property is null)
            throw new InvalidOperationException($"Missing static property '{propertyName}' on '{type.Name}'.");
        var value = property.GetValue(null) as string;
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException($"Static property '{propertyName}' on '{type.Name}' is null or empty.");
        return value;
    }

    static Type? ReadTypeProperty(Type type, string propertyName)
    {
        var property = type.GetProperty(propertyName, BindingFlags.Public | BindingFlags.Static);
        if (property is null)
            throw new InvalidOperationException($"Missing static property '{propertyName}' on '{type.Name}'.");
        return property.GetValue(null) as Type;
    }
}
