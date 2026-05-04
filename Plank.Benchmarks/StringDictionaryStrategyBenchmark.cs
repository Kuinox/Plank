using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;
using System.Collections.Immutable;
using System.Text;
using Plank.Schema;
using Plank.Writing;
using Plank.Writing.PageStrategy;
using PlankColumn = Plank.Schema.Column;
using PlankSchema = Plank.Schema.ParquetSchema;

namespace Plank.Benchmarks;

[MemoryDiagnoser]
[SimpleJob(RunStrategy.Throughput, launchCount: 1, warmupCount: 3, iterationCount: 8)]
public class StringDictionaryStrategyBenchmark
{
    const string ColumnName = "value";

    string[] _strings = [];
    ReadOnlyMemory<byte>[] _utf8Views = [];
    byte[] _utf8Scratch = [];

    PlankColumn _column = null!;
    MemoryStream _streamA = null!;
    MemoryStream _streamB = null!;
    ParquetWriter _writerA = null!;
    ParquetWriter _writerB = null!;
    SerializedColumn<ReadOnlyMemory<byte>> _serializedUtf8Views = null!;
    SerializedColumn<string> _serializedStrings = null!;

    [Params(500_000)]
    public int Rows { get; set; }

    [Params(10, 20, 30, 40, 50, 60, 70, 80, 90, 100)]
    public int UniquePercent { get; set; }

    [GlobalSetup]
    public void GlobalSetup()
    {
        var uniqueCount = Math.Max(1, checked((int)Math.Ceiling(Rows * (UniquePercent / 100d))));
        _strings = new string[Rows];
        for (var i = 0; i < Rows; i++)
            _strings[i] = $"value-{i % uniqueCount}";

        _utf8Views = new ReadOnlyMemory<byte>[Rows];
        _utf8Scratch = new byte[Math.Max(1024, Rows * 16)];

        _column = new PlankColumn(ColumnName, ParquetPhysicalType.ByteArray,
            new ColumnOptions(ParquetRepetition.Required, [EncodingKind.PlainDictionary]));
        var schema = new PlankSchema([_column])
        {
            PageStrategiesByColumnName = ImmutableDictionary<string, IPageStrategy>.Empty
                .WithComparers(StringComparer.Ordinal)
                .Add(ColumnName, ForceDictionaryPageStrategy.Shared)
        };
        var options = new ParquetWriterOptions
        {
            Compression = CompressionKind.None
        };

        _streamA = new MemoryStream(capacity: Rows * 16);
        _streamB = new MemoryStream(capacity: Rows * 16);
        _writerA = schema.CreateWriter(_streamA, options);
        _writerB = schema.CreateWriter(_streamB, options);
        _serializedUtf8Views = _writerA.CreateSerializedColumn<ReadOnlyMemory<byte>>(_column);
        _serializedStrings = _writerB.CreateSerializedColumn<string>(_column);
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        _streamA.Dispose();
        _streamB.Dispose();
    }

    [Benchmark(Baseline = true)]
    public void PreEncodeUtf8ThenDictionary()
    {
        EncodeAllStringsToUtf8Views();
        _writerA.Reset(_streamA);
        _serializedUtf8Views.Serialize(_utf8Views);
        _writerA.StartRowGroup().Write(_serializedUtf8Views);
    }

    [Benchmark]
    public void DictionaryOnStringThenEncodeUnique()
    {
        _writerB.Reset(_streamB);
        _serializedStrings.Serialize(_strings);
        _writerB.StartRowGroup().Write(_serializedStrings);
    }

    void EncodeAllStringsToUtf8Views()
    {
        var offset = 0;
        for (var i = 0; i < _strings.Length; i++)
        {
            var value = _strings[i];
            var required = Encoding.UTF8.GetByteCount(value);
            if (offset + required > _utf8Scratch.Length)
                GrowScratch(offset + required);
            var written = Encoding.UTF8.GetBytes(value, _utf8Scratch.AsSpan(offset));
            _utf8Views[i] = new ReadOnlyMemory<byte>(_utf8Scratch, offset, written);
            offset += written;
        }
    }

    void GrowScratch(int minimumSize)
    {
        var newSize = _utf8Scratch.Length == 0 ? 1024 : _utf8Scratch.Length;
        while (newSize < minimumSize)
            newSize *= 2;
        Array.Resize(ref _utf8Scratch, newSize);
    }
}
