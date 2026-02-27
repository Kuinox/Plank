namespace Plank.DictionaryLab;

public sealed record DictionaryNode<T>(
    string Id,
    string BaseId,
    string? ParentId,
    string Notes,
    Func<IIndexDictionary<T>> Create);
