namespace Plank.Writing;

internal sealed partial class ColumnSemanticRegistry
{
    internal enum LogicalSemanticState : byte
    {
        None = 0,
        Int32Plain = 1,
        Int32Date = 2,
        Int64Plain = 3,
        Int64TimestampMicrosUtc = 4,
        Int64TimeMicros = 5,
        ByteArrayPlain = 6,
        ByteArrayUtf8 = 7
    }
}
