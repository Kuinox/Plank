using Plank.Reading.Logical;

namespace Plank.Tests.Reading;

/// <summary>
/// DELTA_BYTE_ARRAY is defined for FIXED_LEN_BYTE_ARRAY as well as BYTE_ARRAY,
/// and both of the ways a fixed-length column is read have to decode it.
/// </summary>
/// <remarks>
/// Plank's writer never emits the combination, so nothing here had it. A
/// fixed-length column also reaches the reader twice over: unannotated it comes
/// back as a byte span, and annotated DECIMAL or UUID it goes through a
/// converter into decimal or Guid — a different decoder, which is why the
/// encoding failed as "cannot be decoded into pooled values of type
/// 'System.Decimal'" in one case and "…of type 'BinaryValueDescriptor'" in the
/// other.
/// </remarks>
internal sealed class DeltaByteArrayFixedLengthTests
{
    // Small decimals share most of their sixteen big-endian bytes, so
    // consecutive values carry long prefixes — which is the part of the encoding
    // worth exercising, and the part a fixed-length column has to bound.
    static readonly decimal[] Decimals =
        [1.5m, 1.625m, 1.875m, -2.25m, -2.5m, 1000.125m, 1000.25m, 0m, -0.001m, 999999.999m];

    [Test]
    public async Task ReadsDecimalsWrittenAsDeltaByteArray()
    {
        var bytes = Write(new ParquetSharp.Column<decimal>("value",
            ParquetSharp.LogicalType.Decimal(precision: 29, scale: 3)),
            column => column.LogicalWriter<decimal>().WriteBatch(Decimals));

        using var reader = new ParquetReader();
        reader.Reset(new MemoryStream(bytes, writable: false));
        var read = new List<decimal>();
        foreach (var buffer in reader.RowGroups[0].Column<decimal>(reader.Schema.LeafColumns[0]))
            read.AddRange(buffer.Values.ToArray());

        await Assert.That(read).IsEquivalentTo(Decimals);
    }

    [Test]
    public async Task ReadsNullableDecimalsWrittenAsDeltaByteArray()
    {
        var values = new decimal?[Decimals.Length];
        for (var i = 0; i < values.Length; i++)
            values[i] = i % 3 == 0 ? null : Decimals[i];

        var bytes = Write(new ParquetSharp.Column<decimal?>("value",
            ParquetSharp.LogicalType.Decimal(precision: 29, scale: 3)),
            column => column.LogicalWriter<decimal?>().WriteBatch(values));

        using var reader = new ParquetReader();
        reader.Reset(new MemoryStream(bytes, writable: false));
        var read = new List<decimal?>();
        foreach (var buffer in reader.RowGroups[0].Column<decimal?>(reader.Schema.LeafColumns[0]))
            read.AddRange(buffer.Values.ToArray());

        await Assert.That(read).IsEquivalentTo(values);
    }

    /// <remarks>
    /// The same file read the other way. Binding the file's own schema gives no
    /// converter for UUID, so the column comes back as its sixteen bytes — which
    /// is the reader's contract, and the path the binary decoders serve.
    /// </remarks>
    [Test]
    public async Task ReadsFixedLengthBytesWrittenAsDeltaByteArray()
    {
        var values = new Guid[8];
        for (var i = 0; i < values.Length; i++)
            values[i] = new Guid($"0000000{i:x}-0000-0000-0000-00000000000{i:x}");

        var bytes = Write(new ParquetSharp.Column<Guid>("value"),
            column => column.LogicalWriter<Guid>().WriteBatch(values));

        using var reader = new ParquetReader();
        reader.Reset(new MemoryStream(bytes, writable: false));
        var read = new List<byte[]>();
        foreach (var buffer in reader.RowGroups[0].Column<byte>(reader.Schema.LeafColumns[0]))
            for (var i = 0; i < buffer.Count; i++)
                read.Add(buffer.GetValue(i).ToArray());

        await Assert.That(read.Count).IsEqualTo(values.Length);
        for (var i = 0; i < values.Length; i++)
            await Assert.That(read[i]).IsEquivalentTo(values[i].ToByteArray(bigEndian: true));
    }

    [Test]
    public async Task LargeFixedLengthDeltaByteArrayPagesBatchBothReadPaths()
    {
        var decimals = new decimal[40_003];
        for (var i = 0; i < decimals.Length; i++)
            decimals[i] = Decimals[i % Decimals.Length];
        var decimalBytes = Write(new ParquetSharp.Column<decimal>("value",
                ParquetSharp.LogicalType.Decimal(precision: 29, scale: 3)),
            column => column.LogicalWriter<decimal>().WriteBatch(decimals));

        using (var reader = new ParquetReader())
        {
            reader.Reset(new MemoryStream(decimalBytes, writable: false));
            var read = new List<decimal>(decimals.Length);
            var bufferCount = 0;
            foreach (var buffer in reader.RowGroups[0].Column<decimal>(reader.Schema.LeafColumns[0]))
            {
                bufferCount++;
                read.AddRange(buffer.Values);
            }
            await Assert.That(bufferCount).IsGreaterThan(1);
            await Assert.That(read).IsEquivalentTo(decimals);
        }

        var guids = new Guid[40_003];
        for (var i = 0; i < guids.Length; i++)
            guids[i] = new Guid(i, 0, 0, new byte[8]);
        var guidBytes = Write(new ParquetSharp.Column<Guid>("value"),
            column => column.LogicalWriter<Guid>().WriteBatch(guids));

        using (var reader = new ParquetReader())
        {
            reader.Reset(new MemoryStream(guidBytes, writable: false));
            var valueIndex = 0;
            var bufferCount = 0;
            foreach (var buffer in reader.RowGroups[0].Column<byte>(reader.Schema.LeafColumns[0]))
            {
                bufferCount++;
                for (var i = 0; i < buffer.Count; i++, valueIndex++)
                    await Assert.That(buffer.GetValue(i).SequenceEqual(
                        guids[valueIndex].ToByteArray(bigEndian: true))).IsTrue();
            }
            await Assert.That(bufferCount).IsGreaterThan(1);
            await Assert.That(valueIndex).IsEqualTo(guids.Length);
        }
    }

    static byte[] Write(ParquetSharp.Column column, Action<ParquetSharp.ColumnWriter> write)
    {
        using var stream = new MemoryStream();
        using var properties = new ParquetSharp.WriterPropertiesBuilder()
            .Compression(ParquetSharp.Compression.Uncompressed)
            .DisableDictionary()
            .Encoding(ParquetSharp.Encoding.DeltaByteArray)
            .Build();
        using (var writer = new ParquetSharp.ParquetFileWriter(stream, [column], null, properties, null,
            leaveOpen: true))
        {
            using (var rowGroup = writer.AppendRowGroup())
            using (var columnWriter = rowGroup.NextColumn())
                write(columnWriter);
            writer.Close();
        }

        return stream.ToArray();
    }
}
