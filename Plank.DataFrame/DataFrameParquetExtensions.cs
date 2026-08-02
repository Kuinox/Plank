using Microsoft.Data.Analysis;
using Plank.Reading;
using Plank.Reading.Logical;
using Plank.Schema;
using Plank.Writing;

namespace Plank.DataFrame;

/// <summary>Synchronous Parquet integration for Microsoft.Data.Analysis DataFrames.</summary>
public static class DataFrameParquetExtensions
{
    /// <summary>Reads a flat Parquet file into a DataFrame.</summary>
    /// <remarks>The source stream remains open.</remarks>
    public static Microsoft.Data.Analysis.DataFrame ReadDataFrame(this Stream stream,
        ParquetReaderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var reader = new ParquetReader(options);
        reader.Reset(stream);
        return reader.ToDataFrame();
    }

    /// <summary>Materializes every row group and supported flat column from a Plank reader into a DataFrame.</summary>
    public static Microsoft.Data.Analysis.DataFrame ToDataFrame(this ParquetReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ValidateFlatSchema(reader.Schema);

        var rowGroups = reader.RowGroups;
        var rowCount = 0L;
        for (var rowGroupIndex = 0; rowGroupIndex < rowGroups.Count; rowGroupIndex++)
            rowCount = checked(rowCount + checked((long)rowGroups[rowGroupIndex].RowCount));

        var leaves = reader.Schema.LeafColumns;
        var codecs = new DataFrameColumnCodec[leaves.Length];
        var columns = new DataFrameColumn[leaves.Length];
        for (var columnIndex = 0; columnIndex < leaves.Length; columnIndex++)
        {
            var codec = DataFrameColumnCodec.CreateForReading(leaves[columnIndex], rowCount);
            codecs[columnIndex] = codec;
            columns[columnIndex] = codec.Column;
        }

        var offset = 0L;
        for (var rowGroupIndex = 0; rowGroupIndex < rowGroups.Count; rowGroupIndex++)
        {
            var rowGroup = rowGroups[rowGroupIndex];
            for (var columnIndex = 0; columnIndex < codecs.Length; columnIndex++)
                codecs[columnIndex].Read(rowGroup, leaves[columnIndex], offset);
            offset = checked(offset + checked((long)rowGroup.RowCount));
        }

        return new Microsoft.Data.Analysis.DataFrame(columns);
    }

    /// <summary>Writes a DataFrame as a flat Parquet file.</summary>
    /// <param name="dataFrame">The DataFrame to write.</param>
    /// <param name="stream">The destination stream. Plank closes it after writing the footer.</param>
    /// <param name="options">Optional Plank writer options.</param>
    /// <param name="rowGroupSize">The maximum number of rows in each row group.</param>
    public static void WriteParquet(this Microsoft.Data.Analysis.DataFrame dataFrame, Stream stream,
        ParquetWriterOptions? options = null, int rowGroupSize = 1024 * 1024)
    {
        ArgumentNullException.ThrowIfNull(dataFrame);
        ArgumentNullException.ThrowIfNull(stream);
        if (rowGroupSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(rowGroupSize), rowGroupSize,
                "Row-group size must be greater than zero.");

        var codecs = new DataFrameColumnCodec[dataFrame.Columns.Count];
        var definitions = new ColumnDefinition[codecs.Length];
        for (var columnIndex = 0; columnIndex < codecs.Length; columnIndex++)
        {
            var codec = DataFrameColumnCodec.CreateForWriting(dataFrame.Columns[columnIndex]);
            codecs[columnIndex] = codec;
            definitions[columnIndex] = codec.Definition;
        }

        var schema = new ParquetSchema([.. definitions]);
        var writer = schema.CreateWriter(stream, options);
        for (var columnIndex = 0; columnIndex < codecs.Length; columnIndex++)
            codecs[columnIndex].BindWriter(writer, schema.LeafColumns[columnIndex]);

        var rowCount = dataFrame.Rows.Count;
        for (var offset = 0L; offset < rowCount;)
        {
            var count = checked((int)Math.Min(rowCount - offset, rowGroupSize));
            var rowGroup = writer.StartRowGroup();
            for (var columnIndex = 0; columnIndex < codecs.Length; columnIndex++)
                codecs[columnIndex].Write(rowGroup, offset, count);
            offset += count;
        }

        writer.CloseFile();
    }

    static void ValidateFlatSchema(ParquetSchema schema)
    {
        for (var i = 0; i < schema.Definitions.Length; i++)
            if (schema.Definitions[i].Kind != NodeKind.Leaf ||
                schema.Definitions[i].Repetition == ParquetRepetition.Repeated)
                throw new NotSupportedException(
                    $"Parquet schema node '{schema.Definitions[i].Name}' is nested or repeated. DataFrame integration supports flat scalar columns only.");
    }
}
