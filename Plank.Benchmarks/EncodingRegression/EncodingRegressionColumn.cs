using System.Diagnostics;
using System.Security.Cryptography;
using Plank.Schema;
using Plank.Writing;

namespace Plank.Benchmarks.EncodingRegression;

interface IEncodingRegressionColumn
{
    EncodingRegressionCase Case { get; }

    int RowCount { get; }

    long ValueCount { get; }

    /// <summary>Writes one complete file and returns its bytes. Used for the untimed audit pass.</summary>
    byte[] WriteCompleteFile();

    /// <summary>Binds a reusable writer to <paramref name="stream"/> for the timed passes.</summary>
    void Attach(MemoryStream stream);

    /// <summary>Encodes the column once and returns the encode time only.</summary>
    TimeSpan EncodeOnce();

    /// <summary>Bytes produced by the most recent <see cref="EncodeOnce"/>.</summary>
    long LastEncodedLength { get; }
}

sealed class EncodingRegressionColumn<T> : IEncodingRegressionColumn
{
    readonly ParquetSchema _schema;
    readonly T[] _values;
    readonly long _valueCount;
    MemoryStream? _stream;
    ParquetWriter? _writer;
    SerializedColumn<T>? _serialized;

    internal EncodingRegressionColumn(EncodingRegressionCase regressionCase, ParquetSchema schema, T[] values,
        long valueCount)
    {
        Case = regressionCase;
        _schema = schema;
        _values = values;
        _valueCount = valueCount;
    }

    public EncodingRegressionCase Case { get; }

    public int RowCount
        => _values.Length;

    public long ValueCount
        => _valueCount;

    public long LastEncodedLength { get; private set; }

    public byte[] WriteCompleteFile()
    {
        using var stream = new MemoryStream();
        var writer = _schema.CreateWriter(stream, CreateOptions());
        var serialized = writer.CreateSerializedColumn<T>(_schema.LeafColumns[0]);
        serialized.Serialize(_values);
        var rowGroup = writer.StartRowGroup();
        rowGroup.Write(serialized);
        writer.CloseFile();
        return stream.ToArray();
    }

    public void Attach(MemoryStream stream)
    {
        _stream = stream;
        ResetStream();
        _writer = _schema.CreateWriter(stream, CreateOptions());
        _serialized = _writer.CreateSerializedColumn<T>(_schema.LeafColumns[0]);
    }

    public TimeSpan EncodeOnce()
    {
        var writer = _writer ?? throw new InvalidOperationException("Attach must be called before EncodeOnce.");
        var serialized = _serialized!;
        ResetStream();
        writer.Reset(_stream!);
        var rowGroup = writer.StartRowGroup();

        // Only the encode is timed. The row-group write that follows performs compression framing and I/O,
        // which is not the code under test and would dilute the measurement.
        var stopwatch = Stopwatch.StartNew();
        serialized.Serialize(_values);
        stopwatch.Stop();

        rowGroup.Write(serialized);
        LastEncodedLength = _stream!.Length;
        return stopwatch.Elapsed;
    }

    void ResetStream()
    {
        _stream!.Position = 0;
        _stream.SetLength(0);
    }

    static ParquetWriterOptions CreateOptions()
        => new()
        {
            Compression = CompressionKind.None
        };

    internal static string HashFile(byte[] contents)
        => Convert.ToHexStringLower(SHA256.HashData(contents));
}
