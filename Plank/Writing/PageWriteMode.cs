namespace Plank;

public enum PageWriteMode
{
    Buffered = 0,
    BackpatchRequired = 1,
    BackpatchFallbackToBuffer = 2
}
