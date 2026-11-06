using System.Buffers.Binary;
using Plank.Reading;
using Plank.Reading.Physical;

namespace Plank.Tests.Reading;

internal sealed class SizeStatisticsTests
{
    const string RepetitionSample = "UEFSMRUGFZ4HFZ4HXBXIARUAFcgBFQ4VBhUAMhwYBAKIf6AYBP/tKXkWACgEAoh/oBgE/+0peRERAAAAyAEBgAEEZAABAgICAVJJJZVUUkkllVRSSSWVVFJJJZVUUkkllQYAAACAAQRkCAECAgICWGGFFVZYYYUVVlhhhRVWWGGFFVZYYYUVFgAAAAAAAAAAAAAAEtaH/+0peQKIf6D/////AAAAABLWh//tKXkCiH+g/////wAAAAAS1of/7Sl5Aoh/oP////8AAAAAEtaH/+0peQKIf6D/////AAAAABLWh//tKXkCiH+g/////wAAAAAS1of/7Sl5Aoh/oP////8AAAAAEtaH/+0peQKIf6D/////AAAAABLWh//tKXkCiH+g/////wAAAAAS1of/7Sl5Aoh/oP////8AAAAAEtaH/+0peQKIf6D/////AAAAABLWh//tKXkCiH+g/////wAAAAAS1of/7Sl5Aoh/oP////8AAAAAEtaH/+0peQKIf6D/////AAAAABLWh//tKXkCiH+g/////wAAAAAS1of/7Sl5Aoh/oP////8AAAAAEtaH/+0peQKIf6D/////AAAAABLWh/////8AAAAAEtaH/+0peQKIf6D/////AAAAABLWh//tKXkCiH+g/////wAAAAAS1of/7Sl5Aoh/oP////8VBBksNQAYBnNjaGVtYRUCABUOFQgVAhgBYyUKFQwVEixcFQwVEgAAABbIARkcGRwmABwVDhklBg4ZGAFjFQAWyAEWjggWjggmCDwYBAKIf6AYBP/tKXkWACgEAoh/oBgE/+0peRERABkcFQAVDhUCADwpJhkmAMgBAAAAFo4IFsgBJggWjggAGRwYDEFSUk9XOnNjaGVtYRisAS8vLy8vM2dBQUFBUUFBQUFBQUFLQUF3QUJnQUZBQWdBQ2dBQUFBQUJCQUFNQUFBQUNBQUlBQUFBQkFBSUFBQUFCQUFBQUFFQUFBQVVBQUFBRUFBVUFBZ0FCZ0FIQUF3QUFBQVFBQkFBQUFBQUFBRUhFQUFBQUJ3QUFBQUVBQUFBQUFBQUFBRUFBQUJqQUFBQUNBQU1BQVFBQ0FBSUFBQUFDUUFBQUFZQUFBQT0AGCBwYXJxdWV0LWNwcC1hcnJvdyB2ZXJzaW9uIDI2LjAuMBkcHAAAAHABAABQQVIx";
    const string DefinitionSample = "UEFSMRUEFZYBFZYBTBUGFQASAAAVAAAAAQEAAAAAAAAAAADwPwAAAAAAAABAFQAAAAEBAAAAAAAAAAAACEAAAAAAAAAQQBUAAAABAQAAAAAAAAAAAAAAAQAAAAAAAAAVABUUFRQsFQgVEBUGFQYcNgIoFQEBAAAAAAAAAAAA8D8AAAAAAAAAQBgVAQEAAAAAAAAAAAAAAAAAAAAAAAAAEREAAAACAAAAAwsCAyQAFQQZLDUAGAZzY2hlbWEVAgAVDCUCGAFnABYIGRwZHCYAHBUMGTUABhAZGAFnFQAWCBbUAhbUAia+ASYIHDYCKBUBAQAAAAAAAAAAAPA/AAAAAAAAAEAYFQEBAAAAAAAAAAAAAAAAAAAAAAAAABERABksFQQVABUCABUAFRAVAgA8Fn4ZBhlGAgYADCIcFwAAAAAAABTAFwAAAAAAAAhAFwAAAAAAAAAAFwAAAAAAABpAABkVARMFBQUFBQUFBQUBAQEBAQEBAQEBAQEBAQEBAQECAEFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUEAABbUAhYIJggW1AIAGRwYDEFSUk9XOnNjaGVtYRigAS8vLy8vM0FBQUFBUUFBQUFBQUFLQUF3QUJnQUZBQWdBQ2dBQUFBQUJCQUFNQUFBQUNBQUlBQUFBQkFBSUFBQUFCQUFBQUFFQUFBQVVBQUFBRUFBVUFBZ0FCZ0FIQUF3QUFBQVFBQkFBQUFBQUFBRUVFQUFBQUJnQUFBQUVBQUFBQUFBQUFBRUFBQUJuQUFBQUJBQUVBQVFBQUFBQUFBQUEAGCBwYXJxdWV0LWNwcC1hcnJvdyB2ZXJzaW9uIDI2LjAuMBkcHAAAAJ4CAABQQVIx";
    const string BooleanSample = "UEFSMRUGFQIVAlwVChUAFQoVABUAFQAyPBgBARgB6AMAAAEBGAEAEREAAAANGRECGRgBABkYAQEVABkWAAAZHBYIFVIWAAAAFQIZLEgGc2NoZW1hFQIAFQAlABgBYwAWChkcGRwmABwVABkVBBkYAWMVABYKFlIWUiYIfBYKJggWfAA5HAEBGAEAEREAABZ8FRQWWhUiABZSFgomCBZSADkcHAAAAF4AAABQQVIx";

    [Test]
    [Arguments(0, "Repetition")]
    [Arguments(1, "Definition")]
    [Arguments(2, "Unencoded")]
    public void RejectsSamples(int sample, string message)
    {
        var bytes = Convert.FromBase64String(new[] { RepetitionSample, DefinitionSample, BooleanSample }[sample]);
        using var reader = new ParquetFileReader();
        var error = Assert.Throws<CorruptParquetException>(() => reader.Reset(new MemoryStream(bytes)));
        if (!error.Message.Contains(message, StringComparison.Ordinal)) throw new InvalidOperationException(error.Message);
    }

    [Test]
    [Arguments(0, 1, 1)] // required
    [Arguments(1, 1, 2)] // optional
    [Arguments(2, 2, 4)] // optional list of optional elements
    [Arguments(0, 0, 0)] // empty lists
    public void AcceptsHistograms(int shape, int repetitions, int definitions)
        => Open(BuildFile(shape, 1, Histograms(repetitions, definitions)));

    [Test]
    [Arguments(0, 0, 0)]
    [Arguments(1, 0, 1)]
    [Arguments(2, 1, 3)]
    public void StoresSchemaLevelsWithoutStatistics(int shape, int repetition, int definition)
    {
        using var reader = new ParquetFileReader();
        reader.Reset(new MemoryStream(BuildFile(shape, 1, null)));
        var column = reader.Metadata.ColumnSchema(0);
        if (column.MaxRepetitionLevel != repetition || column.MaxDefinitionLevel != definition ||
            column.IsRepeated != (repetition > 0) || column.HasDefinitionLevels != (definition > 0))
            throw new InvalidOperationException("Physical schema levels or derived flags are incorrect.");
    }

    [Test]
    public void AcceptsMissingStatisticsAndByteArrayBytes()
    {
        Open(BuildFile(0, 1, null));
        Open(BuildFile(0, 6, [0x16, 0x0e, 0]));
    }

    [Test]
    public void SkipsMismatchedThriftFieldTypes()
    {
        // Field 3 is i64, not list, as in 9484. Field 1 is also wrong.
        Open(BuildFile(0, 0, [0x15, 0x02, 0x26, 0x02, 0]));
    }

    [Test]
    [Arguments(0, 2, 1, "Repetition")]
    [Arguments(1, 1, 4, "Definition")]
    [Arguments(2, 1, 4, "Repetition")]
    public void RejectsWrongHistogramSizes(int shape, int repetitions, int definitions, string message)
    {
        var bytes = BuildFile(shape, 1, Histograms(repetitions, definitions));
        using var reader = new ParquetFileReader();
        var error = Assert.Throws<CorruptParquetException>(() => reader.Reset(new MemoryStream(bytes)));
        if (!error.Message.Contains(message, StringComparison.Ordinal)) throw new InvalidOperationException(error.Message);
    }

    [Test]
    public void RejectsUnencodedBytesOnBoolean()
    {
        using var reader = new ParquetFileReader();
        Assert.Throws<CorruptParquetException>(() => reader.Reset(new MemoryStream(BuildFile(0, 0, [0x16, 0, 0]))));
    }

    static void Open(byte[] file)
    {
        using var reader = new ParquetFileReader();
        reader.Reset(new MemoryStream(file));
    }

    static byte[] Histograms(int repetitions, int definitions)
    {
        var bytes = new List<byte> { 0x29, (byte)((repetitions << 4) | 6) };
        bytes.AddRange(new byte[repetitions]);
        bytes.Add(0x19);
        bytes.Add((byte)((definitions << 4) | 6));
        bytes.AddRange(new byte[definitions]);
        bytes.Add(0);
        return bytes.ToArray();
    }

    // Minimal zero-row footer, constructed directly because Plank's writer does not emit size statistics.
    // shape 2 has optional group -> repeated group -> optional leaf (the three LIST levels).
    static byte[] BuildFile(int shape, byte physicalType, byte[]? statistics)
    {
        var footer = new List<byte> { 0x15, 0x02, 0x19, (byte)(shape == 2 ? 0x4c : 0x2c) };
        footer.AddRange([0x48, 0x06, (byte)'s', (byte)'c', (byte)'h', (byte)'e', (byte)'m', (byte)'a', 0x15, 0x02, 0]);
        if (shape == 2)
        {
            footer.AddRange([0x35, 0x02, 0x18, 1, (byte)'a', 0x15, 0x02, 0x15, 0x06, 0]);
            footer.AddRange([0x35, 0x04, 0x18, 1, (byte)'b', 0x15, 0x02, 0]);
        }
        footer.AddRange([0x15, (byte)(physicalType * 2), 0x25, (byte)(shape == 0 ? 0 : 2), 0x18, 1, (byte)'c', 0]);
        footer.AddRange([0x16, 0, 0x19, 0x1c, 0x19, 0x1c, 0x26, 0, 0x1c]);
        footer.AddRange([0x15, (byte)(physicalType * 2), 0x19, 0x15, 0, 0x19, (byte)(shape == 2 ? 0x38 : 0x18)]);
        if (shape == 2) footer.AddRange([1, (byte)'a', 1, (byte)'b']);
        footer.AddRange([1, (byte)'c', 0x15, 0, 0x16, 0, 0x16, 0, 0x16, 0, 0x26, 8]);
        if (statistics != null)
        {
            footer.AddRange([0x0c, 0x20]); // absolute field id 16, struct
            footer.AddRange(statistics);
        }
        footer.AddRange([0, 0, 0x16, 0, 0x16, 0, 0, 0]);
        var file = new byte[4 + footer.Count + 8];
        "PAR1"u8.CopyTo(file);
        footer.CopyTo(file, 4);
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(file.Length - 8), footer.Count);
        "PAR1"u8.CopyTo(file.AsSpan(file.Length - 4));
        return file;
    }
}
