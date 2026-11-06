using Plank.Reading;
using Plank.Reading.Logical;
using Plank.Schema;
using Plank.Writing;

namespace Plank.Tests.Reading;

internal sealed class EmptyDeltaHeaderTests
{
    // Arrow-rejected repeated BYTE_ARRAY pages whose delta length stream has an invalid layout.
    [Test]
    [Arguments("UEFSMRUAFUgVTCwVBhUMFQYVBhwYAwMDAxgBARYAKAMDAwMYAQEREQAAACSMBgAAAAIAAgECAQMAAAAD+AIAgP//AAECAAAAAgIDAAAAAwMDGRECGRgBARkYAwMDAxUAGRYAABkcFggVmgEWAAAAFQIZLEgGc2NoZW1hFQIAFQwlBBgBYwAWBBkcGRwmABwVDBklAAYZGAFjFQIWBhaWARaaASYIPBgDAwMDGAEBFgAoAwMDAxgBARERAAAWyAEVFhaiARUmABaWARYEJggWmgEAORwcAAAAaQAAAFBBUjE=")]
    [Arguments("UEFSMRUAFUgVTCwVBhUMFQYVBhwYAwMDAxgBARYAKAMDAwMYAQEREQAAACSMBgAAAAIAAgECAAQAAAAD+AIAgBIAAAECAAAAAgIDAAAAAwMDGRECGRgBARkYAwP9AhUAGRYAABkcFggVmgEWAAAAFQIZLEgGc2NoZW1hFQIAFQwlBBgBYwAWBBkcGRwmABwVDBklAAYZGAFjFQIWBhaWARaaASYIPBgDAwMDGAEBFgAoAwMDAxgBARERAAAWyAEVFhaiARUmABaWARYEJggWmgEAORwcAAAAaQAAAFBBUjE=")]
    [Arguments("UEFSMRUAFUgVTCwVBhUMFQYVBhwYAwMDAxgBARYAKAMDAwMYAQEREQAAACSMBgAAAAIAAgECAAQAAAAD+ALuf///AAECAAAAAgIDAAAAAwMDGRECGRgBARkYAwMDAxUAGRYAABkcFggVmgEWAAAAFQIZLEgGc2NoZW1hFQIAFQwlBBgBYwAWBBkcGRwmABwVDBklAAYZGAFjFQIWBhaWARaaASYIPBgDAwMDGAEBFgAoAwMDAxgBARERAAAWyAEVFhaiARUmABaWARYEJggWmgEAORwcAAAAaQAAAFBBUjE=")]
    [Arguments("UEFSMRUAFUgVTCwVBhUMFQYVBhwYAwMDAxgBARYAKAMDAwMYAQEREQAAACSMBgAAAAIAAgECAAQAAAAD+AIAgP//ACECAAAAAgIDAAAAAwMDGRECGRgBARkYAwMDAxUAGRYAABkcFggVmgEWAAAAFQIZLEgGc2NoZW1hFQIAFQwlBBgBYwAWBBkcGRwmABwVDBklAAYZGAFjFQIWBhaWARaaASYIPBgDAwMDGAEBFgAoAwMDAxgBARERAAAWyAEVFhaiARUmABaWARYEJggWmgEAORwcAAAAaQAAAFBBUjE=")]
    [Arguments("UEFSMRUAFUgVTCwVBhUMFQYVBjwYAwMDAxgBARYAKAMDAwMYAQEREQAAACSMGQAAAAIAAgECAAQAAP8EAQIBAQAAAAECAAAAAgIDAAAAA8gDGRECGRgBARkYAwMDAxUAGRYAABkcFggVmgEWAAAAFQIZLEgGc2NoZW1hFQIAFQwlBBgBYwAWBBkcGRwmABwVDBklAAYZGAFjFQIWBhaWARaaASYIPBgDAwMDGAEbFgAoAwMDAxgBARERAAAWyAEVFhaiARUmABaWARYEJggWmgEAORwcAAAAaQAAAFBBUjE=")]
    public void RejectsInvalidDeltaHeaderEvenWithoutPhysicalValues(string sample)
    {
        Assert.Throws<CorruptParquetException>(() =>
        {
            using var reader = new ParquetReader();
            reader.Reset(new MemoryStream(Convert.FromBase64String(sample)));
            foreach (var buffer in reader.RowGroups[0].NestedColumn<byte>(0))
                _ = buffer.Values.Count;
        });
    }

    [Test]
    [Arguments(EncodingKind.DeltaLengthByteArray)]
    [Arguments(EncodingKind.DeltaByteArray)]
    public void ReadsValidZeroPhysicalValues(EncodingKind encoding)
    {
        var schema = new ParquetSchema([
            ColumnDefinition.OptionalLeaf("value", ParquetPhysicalType.ByteArray,
                new ColumnOptions(encodings: [encoding]))
        ]);
        using var output = new MemoryStream();
        using (var writer = schema.CreateWriter(output, new ParquetWriterOptions { Compression = CompressionKind.None }))
        {
            var column = writer.CreateSerializedColumn<byte[]?>(schema.LeafColumns[0]);
            column.Serialize([null, null]);
            writer.StartRowGroup().Write(column);
            writer.CloseFile();
        }
        using var reader = new ParquetReader();
        reader.Reset(new MemoryStream(output.ToArray()));
        var count = 0;
        foreach (var buffer in reader.RowGroups[0].NestedColumn<byte>(0))
        {
            count += buffer.DefinitionLevels.Length;
            if (buffer.Values.Count != 0) throw new InvalidOperationException("Unexpected values.");
        }
        if (count != 2) throw new InvalidOperationException("Missing definitions.");
    }
}
