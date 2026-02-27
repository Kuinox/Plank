using System.Runtime.CompilerServices;

namespace Plank.DictionaryLab.Nodes;

public sealed class TaggedLinearProbingUltraSparseTunedLengthPrefilterNoInlineStringDictionary : IExperimentDictionary<string>
{
    public static string ExperimentName => "hash.linear.tagged.ultra-sparse.tuned.length-prefilter.noinline.v1";

    public static Type? ParentExperimentType => typeof(TaggedLinearProbingUltraSparseTunedLengthPrefilterStringDictionary);

    public static string ExperimentDescription => "Tagged ultra-sparse tuned length-prefilter probing with no-inline boundary to reduce caller code size.";

    readonly TaggedLinearProbingUltraSparseTunedLengthPrefilterStringDictionary _inner = new();

    public int Count => _inner.Count;

    public void Reset(int capacity) => _inner.Reset(capacity);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public int GetOrAddIndex(string key) => _inner.GetOrAddIndex(key);
}
