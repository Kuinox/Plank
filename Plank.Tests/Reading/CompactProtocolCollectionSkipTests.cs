using System.Buffers.Binary;
using Plank.Reading;
using Plank.Reading.Logical;
using Plank.Reading.Physical;
using Plank.Schema;
using Plank.Writing;

namespace Plank.Tests.Reading;

internal sealed class CompactProtocolCollectionSkipTests
{
    [Test]
    [Arguments(14, 0)] [Arguments(15, 0)]
    [Arguments(14, 1)] [Arguments(15, 1)]
    public void InvalidListAndSetElementTypesAreRejected(int type, int count)
    {
        foreach (var collection in new[] { CompactProtocolType.List, CompactProtocolType.Set })
        {
            var reader = new CompactProtocolReader([(byte)((count << 4) | type), 0]);
            try { reader.Skip(collection); }
            catch (CorruptParquetException) { continue; }
            throw new InvalidOperationException("Invalid collection type was accepted.");
        }
    }

    [Test]
    [Arguments(0)] [Arguments(1)] [Arguments(2)] [Arguments(13)]
    public void ValidCollectionHeadersAreAccepted(int type)
    {
        var reader = new CompactProtocolReader([(byte)type]);
        var (count, elementType) = reader.ReadListHeader(CompactProtocolType.List);
        if (count != 0 || (int)elementType != type)
            throw new InvalidOperationException("Unexpected collection header.");
    }

    [Test]
    [Arguments(1)] [Arguments(2)]
    public void BothBooleanTypeIdsSkipCollectionValues(int type)
    {
        var reader = new CompactProtocolReader([(byte)(0x10 | type), 1]);
        reader.Skip(CompactProtocolType.List);
        if (reader.Offset != 2) throw new InvalidOperationException("Boolean value was not consumed.");
    }

    [Test]
    [Arguments(14)] [Arguments(15)]
    public void InvalidFieldTypesAreRejected(int type)
    {
        var reader = new CompactProtocolReader([(byte)(0x10 | type)]);
        reader.BeginRootStruct();
        try { reader.TryReadFieldHeader(out _, out _, out _); }
        catch (CorruptParquetException) { return; }
        throw new InvalidOperationException("Invalid field type was accepted.");
    }

    [Test]
    [Arguments(0)] [Arguments(1)] [Arguments(2)] [Arguments(3)]
    public void SkipsMapsAndReadsFollowingField(int variant)
    {
        byte[] unknown = variant switch
        {
            0 => [0x1b, 0], // empty map
            1 => [0x1b, 2, 0x58, 2, 1, (byte)'a', 4, 1, (byte)'b'],
            2 => [0x1b, 1, 0x5c, 2, 0x15, 84, 0],
            _ => [0x1c, 0x1b, 1, 0x58, 2, 1, (byte)'a', 0]
        };
        byte[] data = [.. unknown, 0x15, 84, 0];
        var reader = new CompactProtocolReader(data);
        reader.BeginRootStruct();
        if (!reader.TryReadFieldHeader(out _, out var type, out var inlineBool))
            throw new InvalidOperationException("Missing unknown field.");
        reader.Skip(type, inlineBool);
        if (!reader.TryReadFieldHeader(out var id, out type, out _) || id != 2 ||
            reader.ReadI32(type) != 42 || reader.TryReadFieldHeader(out _, out _, out _))
            throw new InvalidOperationException("Skipping map lost the following field.");
    }

    [Test]
    [Arguments(14, false)] [Arguments(15, false)]
    [Arguments(14, true)] [Arguments(15, true)]
    public void InvalidMapTypesAreRejected(int type, bool key)
    {
        var types = key ? (type << 4) | 5 : 0x50 | type;
        var reader = new CompactProtocolReader([1, (byte)types, 0, 0]);
        try { reader.Skip(CompactProtocolType.Map); }
        catch (CorruptParquetException) { return; }
        throw new InvalidOperationException("Invalid map type was accepted.");
    }

    [Test]
    [Arguments(0)] [Arguments(1)] [Arguments(2)] [Arguments(3)]
    public void SkipsUuidAndReadsFollowingField(int variant)
    {
        byte[] uuid = [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15];
        byte[] unknown = variant switch
        {
            0 => [0x1d, .. uuid], // UUID field
            1 => [0x19, 0x1d, .. uuid], // list<uuid>
            2 => [0x1b, 1, 0xd5, .. uuid, 2], // map<uuid,i32>
            _ => [0x1b, 1, 0x5d, 2, .. uuid] // map<i32,uuid>
        };
        var reader = new CompactProtocolReader([.. unknown, 0x15, 84, 0]);
        reader.BeginRootStruct();
        if (!reader.TryReadFieldHeader(out _, out var type, out var inlineBool))
            throw new InvalidOperationException("Missing UUID field.");
        reader.Skip(type, inlineBool);
        if (!reader.TryReadFieldHeader(out var id, out type, out _) || id != 2 ||
            reader.ReadI32(type) != 42 || reader.TryReadFieldHeader(out _, out _, out _))
            throw new InvalidOperationException("Skipping UUID lost the following field.");
    }

    [Test]
    public void TruncatedUuidIsRejected()
    {
        var reader = new CompactProtocolReader(new byte[15]);
        try { reader.Skip(CompactProtocolType.Uuid); }
        catch (CorruptParquetException) { return; }
        throw new InvalidOperationException("Truncated UUID was accepted.");
    }

    [Test]
    public void OversizedCollectionsAndDepthAreBounded()
    {
        CheckBound([0xf5, 100], CompactProtocolType.List, false);
        CheckBound([100, 0x55], CompactProtocolType.Map, false);
        CheckBound([0xf5, 100], CompactProtocolType.List, true);
        CheckBound([100, 0x55], CompactProtocolType.Map, true);
        var reader = new CompactProtocolReader([1, 0x55, 2, 2]);
        try { reader.Skip(CompactProtocolType.Map, remainingDepth: 1); }
        catch (CorruptParquetException) { return; }
        throw new InvalidOperationException("Map did not enforce the nesting limit.");
    }

    static void CheckBound(byte[] data, CompactProtocolType type, bool truncated)
    {
        var reader = new CompactProtocolReader(data, bufferMayBeTruncated: truncated);
        try { reader.Skip(type); }
        catch (CompactProtocolTruncatedException) when (truncated) { return; }
        catch (CorruptParquetException) when (!truncated) { return; }
        throw new InvalidOperationException("Collection byte bound was not enforced.");
    }

    [Test]
    public void InvalidEmptyListFixtureIsRejected()
    {
        var file = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Reading", "Fixtures", "EmptyListType15.parquet"));
        try
        {
            using var stream = new MemoryStream(file, writable: false);
            using var reader = new ParquetFileReader();
            reader.Reset(stream);
            using var pages = reader.OpenPages(0, 0);
            while (pages.MoveNext()) { }
        }
        catch (CorruptParquetException) { return; }
        throw new InvalidOperationException("Invalid empty list was accepted.");
    }

    [Test]
    public void EmptyUuidListInPageHeaderIsAccepted()
    {
        var file = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Reading", "Fixtures", "EmptyListType15.parquet"));
        // Statistics field 15, long-form list of zero elements with type 15.
        var header = file.AsSpan().IndexOf(new byte[] { 0xf9, 0xff, 0 });
        if (header < 0) throw new InvalidOperationException("Fixture list header not found.");
        file[header + 1] = 0xfd;
        using var stream = new MemoryStream(file, writable: false);
        using var reader = new ParquetFileReader();
        reader.Reset(stream);
        using var pages = reader.OpenPages(0, 0);
        while (pages.MoveNext()) { }
    }

    [Test]
    public void WriterFooterWithUnknownMapReadsValues()
    {
        var schema = new ParquetSchema([ColumnDefinition.RequiredLeaf("value", ParquetPhysicalType.Int32)]);
        using var stream = new MemoryStream();
        using (var writer = schema.CreateWriter(stream))
        {
            var column = writer.CreateSerializedColumn<int>(schema.LeafColumns[0]);
            column.Serialize([1, 2, 3]);
            writer.StartRowGroup().Write(column);
            writer.CloseFile();
        }
        var file = stream.ToArray();
        var footerLength = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(file.Length - 8));
        var stop = file.Length - 9;
        if (file[stop] != 0) throw new InvalidOperationException("Missing footer stop.");
        // Explicit field id 100, map<i32,binary> {1: "a"}, then original stop/trailer.
        byte[] map = [0x0b, 0xc8, 1, 1, 0x58, 2, 1, (byte)'a'];
        byte[] mutated = [.. file.AsSpan(0, stop), .. map, .. file.AsSpan(stop)];
        BinaryPrimitives.WriteInt32LittleEndian(mutated.AsSpan(mutated.Length - 8), footerLength + map.Length);
        using var source = new MemoryReadSource(mutated);
        using var reader = schema.CreateReader(source);
        var values = new List<int>();
        foreach (var group in reader.RowGroups)
            foreach (var buffer in group.Column<int>(0))
                values.AddRange(buffer.Values);
        if (!values.SequenceEqual([1, 2, 3])) throw new InvalidOperationException("Values changed.");
    }
}
