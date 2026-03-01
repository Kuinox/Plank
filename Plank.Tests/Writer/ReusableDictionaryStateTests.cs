using Plank.Writing.Encoding;

namespace Plank.Tests.Writer;

internal sealed class ReusableDictionaryStateTests
{
    [Test]
    public void ResetClearsStoredReferences()
    {
        var state = new ReusableDictionaryState<object>();
        var references = Populate(state, 512);

        state.Reset(512, useMap: true, EqualityComparer<object>.Default);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        for (var i = 0; i < references.Length; i++)
        {
            if (references[i].IsAlive)
                throw new InvalidOperationException($"Reference at index {i} is still alive after reset.");
        }
    }

    static WeakReference[] Populate(ReusableDictionaryState<object> state, int count)
    {
        state.Reset(count, useMap: true, EqualityComparer<object>.Default);
        var result = new WeakReference[count];
        for (var i = 0; i < count; i++)
        {
            var value = new object();
            result[i] = new WeakReference(value);
            state.GetOrAddIndex(value);
        }

        return result;
    }
}
