namespace Plank.Writing;

internal sealed partial class ColumnSemanticRegistry
{
    internal enum RepeatedElementState : byte
    {
        None = 0,
        Required = 1,
        Optional = 2
    }
}
