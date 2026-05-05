using BenchmarkDotNet.Attributes;
using Plank.Schema;
using Plank.Writing;
using Plank.Writing.Encoding;
using Plank.Writing.PageStrategy;
using PlankColumn = Plank.Schema.Column;
using PlankSchema = Plank.Schema.ParquetSchema;

namespace Plank.Benchmarks;

[MemoryDiagnoser]
[SimpleJob]
public class FloatPlainInvestigationBenchmark
{
    readonly LeafProjectionInfo _flatProjection = new(IsList: false, ListOptional: false, ElementOptional: false,
        MaxRepetitionLevel: 0, MaxDefinitionLevel: 0);

    MemoryStream _stream = null!;
    PlankColumn _column = null!;
    Plank.Writing.ParquetWriter _writer = null!;
    SerializedColumn<float> _serialized = null!;
    BufferWriterFactory _bufferWriters;
    BufferWriter _plainWriter;
    PageList _pages = null!;
    DefaultStrategy _strategy = null!;
    ReusableDictionaryState<float> _dictionaryState = null!;
    float[] _values = [];

    [Params(1_000_000)]
    public int Rows { get; set; }

    [GlobalSetup]
    public void GlobalSetup()
    {
        _values = new float[Rows];
        for (var i = 0; i < _values.Length; i++)
            _values[i] = (i % 10_000) / 3f;

        _stream = new MemoryStream(capacity: Rows * sizeof(float) + 4096);
        _column = new PlankColumn("value", ParquetPhysicalType.Float,
            new ColumnOptions(ParquetRepetition.Required, [EncodingKind.Plain]));
        _writer = new PlankSchema([_column]).CreateWriter(_stream, new ParquetWriterOptions
        {
            Compression = CompressionKind.None
        });
        _serialized = _writer.CreateSerializedColumn<float>(_column);
        _bufferWriters = new BufferWriterFactory(DefaultParquetBufferPool.Shared, 64 * 1024, 320 * 1024,
            40 * 1024 * 1024, 64 * 1024);
        _plainWriter = _bufferWriters.CreatePageBufferWriter();
        _pages = new PageList(4);
        _strategy = new DefaultStrategy(_column, 1024 * 1024);
        _dictionaryState = new ReusableDictionaryState<float>();
    }

    [GlobalCleanup]
    public void GlobalCleanup()
        => _stream.Dispose();

    [Benchmark]
    public long StatisticsOnly()
        => ColumnStatistics.Create(_column, _values, 0).MaxBits;

    [Benchmark]
    public int PlainEncodeOnly()
    {
        _plainWriter.Reset();
        PlainEncoding.WriteValues(_column, _values, ref _plainWriter);
        return _plainWriter.WrittenLength;
    }

    [Benchmark]
    public int EncodePagesOnly()
    {
        Plank.Writing.Encoding.Encoding.Encode(_bufferWriters, _column, _values, _strategy, _pages, _flatProjection,
            _dictionaryState);
        return _pages.Count;
    }

    [Benchmark]
    public int SerializeOnly()
    {
        _serialized.Serialize(_values);
        var length = _serialized.Pages[0].Content.WrittenLength;
        _serialized.Consume();
        return length;
    }

    [Benchmark]
    public long WriteSerializedOnly()
    {
        _serialized.Serialize(_values);
        _stream.Position = 0;
        _stream.SetLength(0);
        _writer.Reset(_stream);
        var rowGroup = _writer.StartRowGroup();
        rowGroup.Write(_serialized);
        return _stream.Length;
    }
}
