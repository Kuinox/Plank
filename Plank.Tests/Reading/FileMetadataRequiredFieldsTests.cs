using System.Buffers.Binary;
using Plank.Reading;
using Plank.Reading.Physical;

namespace Plank.Tests.Reading;

internal sealed class FileMetadataRequiredFieldsTests
{
    [Test]
    public void RejectsProvidedMalformedFile()
    {
        var file = Convert.FromBase64String(
            "UEFSMRUEFRUCGSxIBnNjaGVtYRUCABUCJQAYAWMAE6gAGf//////MRUEFRUCGSxIBnNjaFNtYRUCABUCJQD/////JQAQGRgBYxUOFqhGFti/ARaQoAEmol4mCBwYBNsFAAAYBAAAAAAWACgE2wUAABgEAAAAABERAAAVxqABFRgWmKABFS4AFti/ARaoRiYIFpCgAQA5HBwAAACUAAAAUEFSMQ==");
        using var reader = new ParquetFileReader();
        Assert.Throws<CorruptParquetException>(() => reader.Reset(new MemoryStream(file, writable: false)));
    }

    [Test]
    [Arguments(1, "version")]
    [Arguments(2, "schema")]
    [Arguments(3, "num_rows")]
    [Arguments(4, "row_groups")]
    public void RejectsMissingRequiredField(int missing, string name)
    {
        var file = CreateFile(missing);
        using var reader = new ParquetFileReader();
        var error = Assert.Throws<CorruptParquetException>(() => reader.Reset(new MemoryStream(file, writable: false)));
        if (!error.Message.Contains($"FileMetaData.{name}", StringComparison.Ordinal))
            throw new InvalidOperationException($"Unexpected error: {error.Message}");
    }

    [Test]
    public void EmptyRowGroupsAreValid()
    {
        using var reader = new ParquetFileReader();
        reader.Reset(new MemoryStream(CreateFile(), writable: false));
        if (reader.Metadata.RowGroupCount != 0)
            throw new InvalidOperationException("Expected no row groups.");
    }

    [Test]
    [Arguments(0x03, 0xa8)] // BYTE rather than I64
    [Arguments(0x06, 0x01)] // I64 -1
    public void RejectsInvalidRowCount(int wireType, int value)
    {
        using var reader = new ParquetFileReader();
        Assert.Throws<CorruptParquetException>(() => reader.Reset(new MemoryStream(CreateFile(rowCountType: (byte)wireType, rowCountValue: (byte)value), writable: false)));
    }

    [Test]
    public void RejectsDuplicateRowCount()
    {
        using var reader = new ParquetFileReader();
        Assert.Throws<CorruptParquetException>(() => reader.Reset(new MemoryStream(CreateFile(duplicateRowCount: true), writable: false)));
    }

    static byte[] CreateFile(int missing = 0, byte rowCountType = 0x06, byte rowCountValue = 0, bool duplicateRowCount = false)
    {
        var footer = new List<byte>();
        // Explicit field IDs make omissions independent of compact field-id deltas.
        if (missing != 1) footer.AddRange([0x05, 0x02, 0x02]);
        if (missing != 2) footer.AddRange([0x09, 0x04, 0x2c, 0x48, 0x06, 0x73, 0x63, 0x68, 0x65, 0x6d, 0x61, 0x15, 0x02, 0x00, 0x15, 0x02, 0x25, 0x00, 0x18, 0x01, 0x63, 0x00]);
        if (missing != 3) footer.AddRange([rowCountType, 0x06, rowCountValue]);
        if (duplicateRowCount) footer.AddRange([0x06, 0x06, 0x00]);
        if (missing != 4) footer.AddRange([0x09, 0x08, 0x0c]);
        footer.Add(0);
        var file = new byte[4 + footer.Count + 8];
        "PAR1"u8.CopyTo(file);
        footer.CopyTo(file, 4);
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(4 + footer.Count), footer.Count);
        "PAR1"u8.CopyTo(file.AsSpan(file.Length - 4));
        return file;
    }
}
