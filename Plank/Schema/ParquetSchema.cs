using System.Collections.Immutable;

namespace Plank.Schema;

public sealed record ParquetSchema(ImmutableArray<Column> Columns)
{
    public void Validate()
    {
        if (Columns.IsDefault)
            return;

        foreach (var column in Columns)
            column.Validate();
    }

}
