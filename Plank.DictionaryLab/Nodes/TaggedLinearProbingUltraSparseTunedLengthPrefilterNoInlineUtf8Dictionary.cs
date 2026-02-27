using System.Runtime.CompilerServices;

namespace Plank.DictionaryLab.Nodes;

public sealed class TaggedLinearProbingUltraSparseTunedLengthPrefilterNoInlineUtf8Dictionary : IExperimentDictionary<ReadOnlyMemory<byte>>
{
    public static string ExperimentName => "hash.linear.tagged.ultra-sparse.tuned.length-prefilter.noinline.v1";

    public static Type? ParentExperimentType => typeof(TaggedLinearProbingUltraSparseTunedLengthPrefilterUtf8Dictionary);

    public static string ExperimentDescription => "UTF-8 tagged ultra-sparse tuned length-prefilter probing with no-inline boundary to reduce caller code size.";

    readonly TaggedLinearProbingUltraSparseTunedLengthPrefilterUtf8Dictionary _inner = new();

    public int Count => _inner.Count;

    public void Reset(int capacity) => _inner.Reset(capacity);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public int GetOrAddIndex(ReadOnlyMemory<byte> key) => _inner.GetOrAddIndex(key);
}
