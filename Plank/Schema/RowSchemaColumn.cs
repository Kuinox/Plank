using Plank.Writing.PageStrategy;

namespace Plank.Schema;

public sealed record RowSchemaColumn
{
    public RowSchemaColumn(string name, ParquetPhysicalType physicalType, Type clrType, ColumnOptions? options = null,
        LogicalType? logicalType = null, IPageStrategy? pageStrategy = null, ParquetValueConverter? converter = null)
        : this(name, physicalType, clrType, fieldId: null, options, logicalType, pageStrategy, converter)
    {
    }

    public RowSchemaColumn(string name, ParquetPhysicalType physicalType, Type clrType, int fieldId,
        ColumnOptions? options = null, LogicalType? logicalType = null, IPageStrategy? pageStrategy = null,
        ParquetValueConverter? converter = null)
        : this(name, physicalType, clrType, (int?)fieldId, options, logicalType, pageStrategy, converter)
    {
    }

    RowSchemaColumn(string name, ParquetPhysicalType physicalType, Type clrType, int? fieldId, ColumnOptions? options,
        LogicalType? logicalType, IPageStrategy? pageStrategy, ParquetValueConverter? converter)
    {
        Name = name;
        PhysicalType = physicalType;
        ClrType = clrType;
        Options = options ?? ColumnOptions.Default;
        LogicalType = logicalType;
        PageStrategy = pageStrategy;
        Converter = converter;
        FieldId = fieldId;
        EncodingCompatibility.Validate(Name, PhysicalType, Options);
        ColumnDefinition.ValidateConverter(Name, PhysicalType, Options, Converter);
        if (Converter is not null && !Converter.SupportsValueType(ClrType))
            throw new ArgumentException(
                $"Converter for '{Converter.ValueType}' cannot materialize row schema CLR type '{ClrType}'.",
                nameof(converter));
    }

    public string Name { get; }

    public ParquetPhysicalType PhysicalType { get; }

    public Type ClrType { get; }

    public ColumnOptions Options { get; }

    public LogicalType? LogicalType { get; }

    public IPageStrategy? PageStrategy { get; }

    /// <summary>Gets the custom CLR value converter, if one is declared.</summary>
    public ParquetValueConverter? Converter { get; }

    public int? FieldId { get; }

    internal ColumnDefinition ToDefinition()
        => ColumnDefinition.Leaf(Name, PhysicalType, Options, LogicalType, PageStrategy, Converter) with
        {
            FieldId = FieldId
        };

}
