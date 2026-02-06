using BenchmarkDotNet.Attributes;
using Plank.Schema;
using Plank.Writing;

namespace Plank.Benchmarks;

[MemoryDiagnoser]
public sealed class NycParquetWriteBenchmarks
{
    ParquetSchema _schema = null!;
    MemoryStream _stream = null!;
    ParquetWriter _writer = null!;
    BenchmarkDataContext _context = null!;

    [GlobalSetup]
    public void Setup()
    {
        _context = NycBenchmarkContext.Current ?? throw new InvalidOperationException("Benchmark data context was not initialized.");
        _schema = new ParquetSchema([
            new Column("VendorID", ParquetPhysicalType.Int32, ColumnOptions.Default)
        ]);
        _stream = new MemoryStream(capacity: Math.Max(4 * 1024 * 1024, _context.TotalRows * sizeof(int) * 2));
        _writer = ParquetWriter.Create(_stream, _schema, new ParquetWriterOptions
        {
            ExpectedRowGroupCount = checked((uint)_context.SourceFiles.Length),
            RowGroupOptions = new RowGroupOptions
            {
                MaxEncodedBytes = 16 * 1024 * 1024,
                MaxCompressedBytes = 16 * 1024 * 1024
            }
        });
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _writer.Dispose();
        _stream.Dispose();
    }

    [Benchmark]
    public void WriteVendorIdColumnsFromNycParquet()
    {
        for (var i = 0; i < _context.VendorIdsByFile.Length; i++)
        {
            var rowGroup = _writer.StartRowGroup();
            var write = rowGroup.WriteAsync(_schema.Columns[0], _context.VendorIdsByFile[i]);
            if (!write.IsCompletedSuccessfully)
                throw new InvalidOperationException("WriteAsync must complete synchronously in this benchmark.");
        }

        _writer.CloseFile();
        _stream.Position = 0;
        _stream.SetLength(0);
        _writer.Reset(_stream);
    }
}
