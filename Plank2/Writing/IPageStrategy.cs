using System.Collections.Generic;
using Plank.Schema;

namespace Plank2.Writing;

public interface IPageStrategy
{
    DictionaryMode GetDictionaryMode(Column column);

    bool ShouldDropDictionary<T>(Column column, IReadOnlyDictionary<T, int> dictionary, int totalRowCount, int rowsSeen)
        where T : notnull;

    bool ShouldStartNewDataPage(Column column, int totalRowCount, int rowsWritten, int currentPageRowCount);
}
