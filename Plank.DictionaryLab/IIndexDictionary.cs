namespace Plank.DictionaryLab;

public interface IIndexDictionary<T>
{
    int Count { get; }

    void Reset(int capacity);

    int GetOrAddIndex(T key);
}

public interface IExperimentDictionary<T> : IIndexDictionary<T>
{
    static abstract string ExperimentName { get; }

    static abstract Type? ParentExperimentType { get; }

    static abstract string ExperimentDescription { get; }
}
