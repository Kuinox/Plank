namespace Plank.DictionaryLab.Nodes;

public sealed class TrieUtf8Dictionary : IExperimentDictionary<ReadOnlyMemory<byte>>
{
    public static string ExperimentName => "tree.trie.v1";

    public static Type? ParentExperimentType => null;

    public static string ExperimentDescription => "UTF-8 trie with per-node linked child edges.";

    Node[] _nodes = [];
    Edge[] _edges = [];
    int _nodeCount;
    int _edgeCount;

    public int Count { get; private set; }

    public void Reset(int capacity)
    {
        var nodeCapacity = Math.Max(16, capacity + 1);
        if (_nodes.Length < nodeCapacity)
            Array.Resize(ref _nodes, nodeCapacity);

        var edgeCapacity = Math.Max(32, capacity << 2);
        if (_edges.Length < edgeCapacity)
            Array.Resize(ref _edges, edgeCapacity);

        _nodeCount = 1;
        _edgeCount = 0;
        Count = 0;
        _nodes[0].FirstChild = -1;
        _nodes[0].ChildCount = 0;
        _nodes[0].TerminalIndex = -1;
    }

    public int GetOrAddIndex(ReadOnlyMemory<byte> key)
    {
        var nodeIndex = 0;
        var keySpan = key.Span;
        for (var i = 0; i < keySpan.Length; i++)
            nodeIndex = GetOrAddChild(nodeIndex, keySpan[i]);

        var terminalIndex = _nodes[nodeIndex].TerminalIndex;
        if (terminalIndex >= 0)
            return terminalIndex;

        var newIndex = Count;
        Count++;
        _nodes[nodeIndex].TerminalIndex = newIndex;
        return newIndex;
    }

    int GetOrAddChild(int nodeIndex, byte label)
    {
        var edgeIndex = _nodes[nodeIndex].FirstChild;
        while (edgeIndex >= 0)
        {
            ref var edge = ref _edges[edgeIndex];
            if (edge.Label == label)
                return edge.ChildNode;
            edgeIndex = edge.NextSibling;
        }

        var childNode = CreateNode();
        EnsureEdgeCapacity(_edgeCount + 1);
        var newEdgeIndex = _edgeCount++;
        _edges[newEdgeIndex] = new Edge
        {
            Label = label,
            ChildNode = childNode,
            NextSibling = _nodes[nodeIndex].FirstChild
        };
        _nodes[nodeIndex].FirstChild = newEdgeIndex;
        _nodes[nodeIndex].ChildCount++;
        return childNode;
    }

    int CreateNode()
    {
        EnsureNodeCapacity(_nodeCount + 1);
        var nodeIndex = _nodeCount++;
        _nodes[nodeIndex].FirstChild = -1;
        _nodes[nodeIndex].ChildCount = 0;
        _nodes[nodeIndex].TerminalIndex = -1;
        return nodeIndex;
    }

    void EnsureNodeCapacity(int target)
    {
        if (_nodes.Length >= target)
            return;

        var newCapacity = _nodes.Length == 0 ? 16 : _nodes.Length << 1;
        while (newCapacity < target)
            newCapacity <<= 1;
        Array.Resize(ref _nodes, newCapacity);
    }

    void EnsureEdgeCapacity(int target)
    {
        if (_edges.Length >= target)
            return;

        var newCapacity = _edges.Length == 0 ? 32 : _edges.Length << 1;
        while (newCapacity < target)
            newCapacity <<= 1;
        Array.Resize(ref _edges, newCapacity);
    }

    struct Node
    {
        public int FirstChild;
        public int ChildCount;
        public int TerminalIndex;
    }

    struct Edge
    {
        public byte Label;
        public int ChildNode;
        public int NextSibling;
    }
}
