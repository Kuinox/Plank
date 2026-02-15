namespace Plank.Writing;

public sealed partial class ParquetWriter
{
    internal sealed partial class RowGroupState
    {
        enum PageWriteMode
        {
            SinglePage,
            SplitFixedWidthRequired,
            SplitLevelFixedWidth,
            SplitVariableWidthRequired
        }
    }
}
