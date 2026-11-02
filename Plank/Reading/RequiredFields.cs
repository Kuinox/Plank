namespace Plank.Reading;

// Field IDs and names are from parquet-format/src/main/thrift/parquet.thrift.
// The descriptors are shared by every reader of a struct; tracking itself is allocation-free.
internal sealed class RequiredFieldSet
{
    readonly string _structure;
    readonly (int Id, string Name)[] _fields;
    readonly uint _mask;

    internal RequiredFieldSet(string structure, params (int Id, string Name)[] fields)
    {
        _structure = structure;
        _fields = fields;
        foreach (var (id, _) in fields)
            _mask |= 1u << id;
    }

    internal void Validate(uint seen)
    {
        if ((seen & _mask) == _mask)
            return;
        foreach (var (id, name) in _fields)
            if ((seen & (1u << id)) == 0)
                throw new CorruptParquetException($"Missing required {_structure}.{name} field.");
    }
}

internal struct RequiredFields
{
    uint _seen;
    internal void Add(int id)
    {
        if ((uint)id < 32)
            _seen |= 1u << id;
    }
    internal readonly void Validate(RequiredFieldSet fields) => fields.Validate(_seen);
}

internal static class ParquetRequiredFields
{
    internal static readonly RequiredFieldSet PageHeader = new("PageHeader", (1, "type"), (2, "uncompressed_page_size"), (3, "compressed_page_size"));
    internal static readonly RequiredFieldSet DataPageHeader = new("DataPageHeader", (1, "num_values"), (2, "encoding"), (3, "definition_level_encoding"), (4, "repetition_level_encoding"));
    internal static readonly RequiredFieldSet DataPageHeaderV2 = new("DataPageHeaderV2", (1, "num_values"), (2, "num_nulls"), (3, "num_rows"), (4, "encoding"), (5, "definition_levels_byte_length"), (6, "repetition_levels_byte_length"));
    internal static readonly RequiredFieldSet DictionaryPageHeader = new("DictionaryPageHeader", (1, "num_values"), (2, "encoding"));
    internal static readonly RequiredFieldSet ColumnMetaData = new("ColumnMetaData", (1, "type"), (2, "encodings"), (3, "path_in_schema"), (4, "codec"), (5, "num_values"), (6, "total_uncompressed_size"), (7, "total_compressed_size"), (9, "data_page_offset"));
    internal static readonly RequiredFieldSet RowGroup = new("RowGroup", (1, "columns"), (2, "total_byte_size"), (3, "num_rows"));
    internal static readonly RequiredFieldSet KeyValue = new("KeyValue", (1, "key"));
    internal static readonly RequiredFieldSet SchemaElement = new("SchemaElement", (4, "name"));
    internal static readonly RequiredFieldSet TimeType = new("TimeType", (1, "isAdjustedToUTC"), (2, "unit"));
    internal static readonly RequiredFieldSet TimestampType = new("TimestampType", (1, "isAdjustedToUTC"), (2, "unit"));
    internal static readonly RequiredFieldSet SortingColumn = new("SortingColumn", (1, "column_idx"), (2, "descending"), (3, "nulls_first"));
    internal static readonly RequiredFieldSet BloomFilterHeader = new("BloomFilterHeader", (1, "numBytes"), (2, "algorithm"), (3, "hash"), (4, "compression"));
    internal static readonly RequiredFieldSet BoundingBox = new("BoundingBox", (1, "xmin"), (2, "xmax"), (3, "ymin"), (4, "ymax"));
    internal static readonly RequiredFieldSet OffsetIndex = new("OffsetIndex", (1, "page_locations"));
    internal static readonly RequiredFieldSet PageLocation = new("PageLocation", (1, "offset"), (2, "compressed_page_size"), (3, "first_row_index"));
}
