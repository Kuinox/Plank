using System.Runtime.CompilerServices;

namespace Plank.DictionaryLab.Nodes;

public sealed class TaggedLinearProbingUltraSparseTunedNoInlineStringDictionary : IExperimentDictionary<string>
{
    public static string ExperimentName => "hash.linear.tagged.ultra-sparse.tuned.noinline.v1";

    public static Type? ParentExperimentType => typeof(TaggedLinearProbingUltraSparseStringDictionary.Tuned);

    public static string ExperimentDescription => "Tagged ultra-sparse tuned probing with no-inline boundary to reduce caller code size and register pressure.";

    readonly TaggedLinearProbingUltraSparseStringDictionary.Tuned _inner = new();

    public int Count => _inner.Count;

    public void Reset(int capacity) => _inner.Reset(capacity);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public int GetOrAddIndex(string key) => _inner.GetOrAddIndex(key);
}
