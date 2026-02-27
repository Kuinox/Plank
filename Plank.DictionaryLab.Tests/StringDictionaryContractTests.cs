using Plank.DictionaryLab;

namespace Plank.DictionaryLab.Tests;

internal sealed class StringDictionaryContractTests
{
    [Test]
    public void StringImplementationsPreserveStableIndexesAndCount()
    {
        for (var i = 0; i < DictionaryNodeCatalog.Nodes.Count; i++)
        {
            var node = DictionaryNodeCatalog.Nodes[i];
            var dictionary = node.Create();
            Validate(node.Id, dictionary);
        }
    }

    static void Validate(string id, IIndexDictionary<string> dictionary)
    {
        var values = new[] { "a", "b", "a", "c", "b", "d", "e", "d", "a" };
        var expected = new Dictionary<string, int>(StringComparer.Ordinal);

        dictionary.Reset(16);
        for (var i = 0; i < values.Length; i++)
        {
            var value = values[i];
            var actualIndex = dictionary.GetOrAddIndex(value);
            if (!expected.TryGetValue(value, out var expectedIndex))
            {
                expectedIndex = expected.Count;
                expected.Add(value, expectedIndex);
            }

            if (actualIndex != expectedIndex)
                throw new InvalidOperationException($"Node '{id}' returned wrong index for '{value}': expected {expectedIndex}, got {actualIndex}.");
        }

        if (dictionary.Count != expected.Count)
            throw new InvalidOperationException($"Node '{id}' returned wrong count: expected {expected.Count}, got {dictionary.Count}.");
    }

    [Test]
    public void StringImplementationsResetState()
    {
        for (var i = 0; i < DictionaryNodeCatalog.Nodes.Count; i++)
        {
            var node = DictionaryNodeCatalog.Nodes[i];
            var dictionary = node.Create();

            dictionary.Reset(8);
            dictionary.GetOrAddIndex("alpha");
            dictionary.GetOrAddIndex("beta");
            dictionary.Reset(8);

            if (dictionary.Count != 0)
                throw new InvalidOperationException($"Node '{node.Id}' should be empty after reset.");

            var first = dictionary.GetOrAddIndex("alpha");
            var second = dictionary.GetOrAddIndex("alpha");
            if (first != 0 || second != 0 || dictionary.Count != 1)
                throw new InvalidOperationException($"Node '{node.Id}' failed reset semantics.");
        }
    }
}
