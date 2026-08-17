using Plank.Schema;

namespace Plank.Fuzzing;

// Row types for the row-API fuzz target.
//
// The row writer cannot be driven without these. RowWriterBase<TSlot> is
// abstract over four methods the generator emits, and RowGroupWriterCore<TSlot>
// is generic over a generated slot type — neither can be synthesized at runtime,
// which is why the whole write pipeline (RowBufferSlot, RowValueSizeEstimator,
// PipelineRowWriterBase, RowApiColumnWriteState, RowWriterBase) measured zero
// while the columnar writer was well covered.
//
// Three shapes rather than one, because the generated code differs by property
// type in ways that matter:
//
//   fixed-width required  -> the dense, no-definition-level path
//   nullable              -> definition levels, and a separate slot layout
//   binary                -> ByteArrayRows, the length-and-offset bookkeeping
//                            the fixed-width path does not have

/// <summary>Fixed-width required columns: the densest row layout.</summary>
[ParquetSchema]
public sealed partial class RowFuzzFixed
{
    public bool Flag { get; set; }

    public int Id { get; set; }

    public long Sequence { get; set; }

    public double Measure { get; set; }
}

/// <summary>Nullable columns, so every row carries definition levels.</summary>
[ParquetSchema]
public sealed partial class RowFuzzNullable
{
    public int? Id { get; set; }

    public long? Sequence { get; set; }

    public double? Measure { get; set; }

    public bool? Flag { get; set; }
}

/// <summary>Variable-length columns, which is what reaches ByteArrayRows.</summary>
/// <remarks>
/// AllowAllocatingValues is on for the string column. The generator refuses
/// string by default because the UTF-8 conversion allocates, but that conversion
/// is its own encode and decode path and worth fuzzing; a byte[] column beside it
/// covers the non-allocating one.
/// </remarks>
[ParquetSchema(AllowAllocatingValues = true)]
public sealed partial class RowFuzzBinary
{
    public int Id { get; set; }

    public byte[] Payload { get; set; } = [];

    public string Label { get; set; } = string.Empty;
}
