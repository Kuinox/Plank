using System.Text;
using Plank.DictionaryLab;

namespace Plank.DictionaryLab.Tests;

internal sealed class Utf8DictionaryContractTests
{
    [Test]
    public void Utf8ImplementationsPreserveStableIndexesAndCount()
    {
        for (var i = 0; i < Utf8DictionaryNodeCatalog.Nodes.Count; i++)
        {
            var node = Utf8DictionaryNodeCatalog.Nodes[i];
            var dictionary = node.Create();
            Validate(node.Id, dictionary);
        }
    }

    static void Validate(string id, IIndexDictionary<ReadOnlyMemory<byte>> dictionary)
    {
        var words = new[] { "a", "b", "a", "c", "b", "d", "e", "d", "a" };
        var values = new ReadOnlyMemory<byte>[words.Length];
        for (var i = 0; i < words.Length; i++)
            values[i] = Encoding.UTF8.GetBytes(words[i]);

        var expected = new Dictionary<string, int>(StringComparer.Ordinal);

        dictionary.Reset(16);
        for (var i = 0; i < words.Length; i++)
        {
            var key = words[i];
            var actualIndex = dictionary.GetOrAddIndex(values[i]);
            if (!expected.TryGetValue(key, out var expectedIndex))
            {
                expectedIndex = expected.Count;
                expected.Add(key, expectedIndex);
            }

            if (actualIndex != expectedIndex)
                throw new InvalidOperationException($"Node '{id}' returned wrong index for '{key}': expected {expectedIndex}, got {actualIndex}.");
        }

        if (dictionary.Count != expected.Count)
            throw new InvalidOperationException($"Node '{id}' returned wrong count: expected {expected.Count}, got {dictionary.Count}.");
    }
}
