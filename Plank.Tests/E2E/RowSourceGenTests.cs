using ParquetSharp;
using Plank.Writing;

#pragma warning disable CA2007
namespace Plank.Tests;

sealed class RowSourceGenTests
{
    [Test]
    public async Task GeneratedRowWriterFromSchemaPropertyWritesSupportedTypes()
    {
        var path = Path.Combine(Path.GetTempPath(), $"plank-rowgen-prop-{Guid.NewGuid():N}.parquet");
        try
        {
            await using (var stream = File.Create(path))
            using (var writer = ParquetWriter.Create(stream, GeneratedSchemaHolder.Schema))
            {
                var rowGroup = writer.StartRowGroup();
                var rows = GeneratedSchemaHolder_SchemaPlankRow.CreateWriter(rowGroup, 3);

                var r0 = rows.GetRow();
                r0.id = 1;
                r0.flag = true;
                r0.amount = 100;
                r0.ratio = 1.5f;
                r0.score = 10.25;
                r0.blob = [1, 2];
                r0.opt_int = 7;
                rows.Next();

                var r1 = rows.GetRow();
                r1.id = 2;
                r1.flag = false;
                r1.amount = 200;
                r1.ratio = 2.5f;
                r1.score = 20.5;
                r1.blob = [3, 4, 5];
                r1.opt_int = null;
                rows.Next();

                var r2 = rows.GetRow();
                r2.id = 3;
                r2.flag = true;
                r2.amount = 300;
                r2.ratio = 3.5f;
                r2.score = 30.75;
                r2.blob = [6];
                r2.opt_int = 9;
                rows.Next();

                await rows.WriteAsync();
                writer.CloseFile();
            }

            using var reader = new ParquetFileReader(path);
            using var rg = reader.RowGroup(0);

            using var idReader = rg.Column(0).LogicalReader<int>();
            using var flagReader = rg.Column(1).LogicalReader<bool>();
            using var amountReader = rg.Column(2).LogicalReader<long>();
            using var ratioReader = rg.Column(3).LogicalReader<float>();
            using var scoreReader = rg.Column(4).LogicalReader<double>();
            using var blobReader = rg.Column(5).LogicalReader<byte[]>();
            using var optionalReader = rg.Column(6).LogicalReader<int?>();

            var ids = idReader.ReadAll(3);
            var flags = flagReader.ReadAll(3);
            var amounts = amountReader.ReadAll(3);
            var ratios = ratioReader.ReadAll(3);
            var scores = scoreReader.ReadAll(3);
            var blobs = blobReader.ReadAll(3);
            var optional = optionalReader.ReadAll(3);

            await Assert.That(ids.SequenceEqual([1, 2, 3])).IsTrue();
            await Assert.That(flags.SequenceEqual([true, false, true])).IsTrue();
            await Assert.That(amounts.SequenceEqual([100L, 200L, 300L])).IsTrue();
            await Assert.That(ratios.SequenceEqual([1.5f, 2.5f, 3.5f])).IsTrue();
            await Assert.That(scores.SequenceEqual([10.25, 20.5, 30.75])).IsTrue();
            await Assert.That(blobs[0].Length).IsEqualTo(2);
            await Assert.That(blobs[0][0]).IsEqualTo((byte)1);
            await Assert.That(blobs[0][1]).IsEqualTo((byte)2);
            await Assert.That(blobs[1].Length).IsEqualTo(3);
            await Assert.That(blobs[1][0]).IsEqualTo((byte)3);
            await Assert.That(blobs[1][1]).IsEqualTo((byte)4);
            await Assert.That(blobs[1][2]).IsEqualTo((byte)5);
            await Assert.That(blobs[2].Length).IsEqualTo(1);
            await Assert.That(blobs[2][0]).IsEqualTo((byte)6);
            await Assert.That(optional[0]).IsEqualTo(7);
            await Assert.That(optional[1]).IsNull();
            await Assert.That(optional[2]).IsEqualTo(9);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Test]
    public async Task GeneratedPipelineWriterFlushesTwoRowGroups()
    {
        var path = Path.Combine(Path.GetTempPath(), $"plank-rowgen-pipeline-{Guid.NewGuid():N}.parquet");
        try
        {
            await using (var stream = File.Create(path))
            using (var writer = ParquetWriter.Create(stream, GeneratedSchemaHolder.Schema, new ParquetWriterOptions
            {
                ExpectedRowGroupCount = 2
            }))
            {
                var pipeline = GeneratedSchemaHolder_SchemaPlankRow.CreatePipelineWriter(writer, 2);

                var r0 = pipeline.GetRow();
                r0.id = 1;
                r0.flag = true;
                r0.amount = 10;
                r0.ratio = 1.1f;
                r0.score = 1.11;
                r0.blob = [1];
                r0.opt_int = 5;
                pipeline.Next();

                var r1 = pipeline.GetRow();
                r1.id = 2;
                r1.flag = false;
                r1.amount = 20;
                r1.ratio = 2.2f;
                r1.score = 2.22;
                r1.blob = [2];
                r1.opt_int = null;
                pipeline.Next();
                await pipeline.FlushAsync();

                var r2 = pipeline.GetRow();
                r2.id = 3;
                r2.flag = true;
                r2.amount = 30;
                r2.ratio = 3.3f;
                r2.score = 3.33;
                r2.blob = [3];
                r2.opt_int = 7;
                pipeline.Next();

                var r3 = pipeline.GetRow();
                r3.id = 4;
                r3.flag = false;
                r3.amount = 40;
                r3.ratio = 4.4f;
                r3.score = 4.44;
                r3.blob = [4];
                r3.opt_int = null;
                pipeline.Next();
                await pipeline.FlushAsync();
                await pipeline.CompleteAsync();
                writer.CloseFile();
            }

            using var reader = new ParquetFileReader(path);
            await Assert.That(reader.FileMetaData.NumRowGroups).IsEqualTo(2);
            await Assert.That(reader.FileMetaData.NumRows).IsEqualTo(4L);
            using var rg0 = reader.RowGroup(0);
            using var rg1 = reader.RowGroup(1);
            using var id0 = rg0.Column(0).LogicalReader<int>();
            using var id1 = rg1.Column(0).LogicalReader<int>();
            var values0 = id0.ReadAll(2);
            var values1 = id1.ReadAll(2);
            await Assert.That(values0.SequenceEqual([1, 2])).IsTrue();
            await Assert.That(values1.SequenceEqual([3, 4])).IsTrue();
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }
}
