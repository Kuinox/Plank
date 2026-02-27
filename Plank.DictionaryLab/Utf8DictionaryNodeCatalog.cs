namespace Plank.DictionaryLab;

public static class Utf8DictionaryNodeCatalog
{
    public static IReadOnlyList<DictionaryNode<ReadOnlyMemory<byte>>> Nodes { get; } =
        ExperimentNodeCatalogBuilder.Build<ReadOnlyMemory<byte>>();

    public static DictionaryNode<ReadOnlyMemory<byte>> Get(string id)
    {
        for (var i = 0; i < Nodes.Count; i++)
        {
            var node = Nodes[i];
            if (node.Id == id)
                return node;
        }

        throw new ArgumentOutOfRangeException(nameof(id), id, "Unknown node id.");
    }
}
