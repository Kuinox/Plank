namespace Plank.Writing.PageStrategy;

sealed class PageStrategyContext
{
    internal PageStrategyContext(IPageStrategy strategy)
        => Strategy = strategy;

    internal readonly IPageStrategy Strategy;
    internal int DictionarySortOrder = (int)global::Plank.Writing.DictionarySortOrder.Unknown;
}
