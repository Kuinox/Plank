using System.Collections.Immutable;
using Plank.Schema;

namespace Plank.Writing;

public sealed partial class ParquetWriter
{
    internal sealed partial class RowGroupState
    {
        sealed class RowGroupColumnStore
        {
            internal readonly Dictionary<Column, int> ColumnOrdinals;
            internal readonly Column[] Columns;
            internal readonly ColumnState[] ColumnStates;
            internal readonly TaskCompletionSource<bool>?[] WriteSignals;
            internal readonly ColumnChunkMetadata[] ColumnMetadata;
            internal int RowCount;
            internal int NextOrdinal;

            internal RowGroupColumnStore(ImmutableArray<Column> columns)
            {
                Columns = columns.IsDefault ? [] : columns.ToArray();
                ColumnStates = Columns.Length > 0 ? new ColumnState[Columns.Length] : [];
                ColumnMetadata = Columns.Length > 0 ? new ColumnChunkMetadata[Columns.Length] : [];
                WriteSignals = Columns.Length > 0 ? new TaskCompletionSource<bool>?[Columns.Length] : [];
                ColumnOrdinals = Columns.Length > 0
                    ? new Dictionary<Column, int>(Columns.Length, ReferenceEqualityComparer.Instance)
                    : new Dictionary<Column, int>(ReferenceEqualityComparer.Instance);
                for (var i = 0; i < Columns.Length; i++)
                    ColumnOrdinals[Columns[i]] = i;
                RowCount = -1;
                NextOrdinal = 0;
            }
        }

        sealed class RowGroupDrainState
        {
            internal int InProgress;
            internal int CompletionSignaled;
        }
    }
}
