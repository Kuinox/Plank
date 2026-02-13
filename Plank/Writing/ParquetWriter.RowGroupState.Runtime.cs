using System.Collections.Immutable;
using Plank.Schema;

namespace Plank.Writing;

public sealed partial class ParquetWriter
{
    internal sealed partial class RowGroupState
    {
        sealed class RowGroupColumnStore
        {
            internal readonly RowGroupSchemaState Schema;
            internal readonly RowGroupDataState Data;
            internal readonly RowGroupWriteState Write;
            internal readonly RowGroupProgressState Progress;

            internal RowGroupColumnStore(ImmutableArray<Column> columns)
            {
                Schema = new RowGroupSchemaState(columns);
                Data = new RowGroupDataState(Schema.Columns.Length);
                Write = new RowGroupWriteState(Schema.Columns.Length);
                Progress = new RowGroupProgressState();
            }
        }

        sealed class RowGroupSchemaState
        {
            internal readonly Dictionary<Column, int> ColumnOrdinals;
            internal readonly Column[] Columns;

            internal RowGroupSchemaState(ImmutableArray<Column> columns)
            {
                Columns = columns.IsDefault ? [] : columns.ToArray();
                ColumnOrdinals = Columns.Length > 0
                    ? new Dictionary<Column, int>(Columns.Length, ReferenceEqualityComparer.Instance)
                    : new Dictionary<Column, int>(ReferenceEqualityComparer.Instance);
                for (var i = 0; i < Columns.Length; i++)
                    ColumnOrdinals[Columns[i]] = i;
            }
        }

        sealed class RowGroupDataState
        {
            internal readonly ColumnState[] ColumnStates;
            internal readonly ColumnChunkMetadata[] ColumnMetadata;

            internal RowGroupDataState(int count)
            {
                ColumnStates = count > 0 ? new ColumnState[count] : [];
                ColumnMetadata = count > 0 ? new ColumnChunkMetadata[count] : [];
            }
        }

        sealed class RowGroupWriteState
        {
            internal readonly TaskCompletionSource<bool>?[] Signals;

            internal RowGroupWriteState(int count)
                => Signals = count > 0 ? new TaskCompletionSource<bool>?[count] : [];
        }

        sealed class RowGroupProgressState
        {
            internal int RowCount;
            internal int NextOrdinal;

            internal RowGroupProgressState()
            {
                RowCount = -1;
                NextOrdinal = 0;
            }
        }

    }
}
