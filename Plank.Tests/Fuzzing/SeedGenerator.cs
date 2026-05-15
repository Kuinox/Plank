using Plank.Fuzzing;
using Plank.Schema;

namespace Plank.Tests.Fuzzing;

/// <summary>
/// Generates AFL corpus seeds that drive specific fuzz scenarios.
/// Each seed byte is consumed directly by ByteCursor (cyclic — no PRNG fallback),
/// so bytes map 1-to-1 to decoder decisions. Run WriteSeedsToCorpus to refresh fuzz/corpus/.
/// </summary>
static class SeedGenerator
{
    const string CorpusDir = "../../../../../fuzz/corpus";

    public static void WriteSeedsToCorpus()
    {
        Directory.CreateDirectory(CorpusDir);
        foreach (var (name, bytes) in AllSeeds())
        {
            var path = Path.Combine(CorpusDir, name);
            File.WriteAllBytes(path, bytes);
        }
    }

    public static IEnumerable<(string Name, byte[] Bytes)> AllSeeds()
    {
        yield return ("max-columns-rle-dict", MaxColumnsRleDictionary());
        yield return ("max-columns-delta", MaxColumnsDelta());
        yield return ("max-rowgroups-byte-array", MaxRowGroupsByteArray());
        yield return ("single-col-plain-many-rows", SingleColumnPlainManyRows());
        yield return ("all-types-plain", AllTypesPlain());
        yield return ("dict-large-cardinality", DictLargeCardinality());
    }

    /// <summary>5 columns (bool+int32 RLE-dict+int64 RLE-dict+double+byte[]), 3 row groups, 32 rows.</summary>
    static byte[] MaxColumnsRleDictionary()
    {
        var w = new SeedWriter();
        // 5 columns
        w.Int(1, 6, 5);
        w.Int(0, 5, 0);               // col0: bool
        w.Int(0, 5, 1);               // col1: int32
        w.Int(0, 4, 3);               // col1: RleDictionary
        w.Int(0, 5, 2);               // col2: int64
        w.Int(0, 4, 3);               // col2: RleDictionary
        w.Int(0, 5, 3);               // col3: double
        w.Int(0, 2, 1);               // col3: ByteStreamSplit
        w.Int(0, 5, 4);               // col4: byte[]
        w.Int(0, 3, 0);               // col4: Plain
        // 3 row groups, 32 rows each
        w.Int(1, 4, 3);
        for (var rg = 0; rg < 3; rg++)
        {
            w.Int(1, 65, 32);
            // col0 bool: 32 × bool choice
            for (var r = 0; r < 32; r++) w.Int(0, 2, r % 2);
            // col1 int32 RLE-dict: dict(8 entries) + 32 × index
            w.Int(1, 9, 8);
            for (var i = 0; i < 8; i++) w.Int(-4096, 4097, i * 100 - 400);
            for (var r = 0; r < 32; r++) w.Int(0, 8, r % 8);
            // col2 int64 RLE-dict: dict(8 entries) + 32 × index
            w.Int(1, 9, 8);
            for (var i = 0; i < 8; i++) w.Int64(-1_000_000L, 1_000_001L, i * 100_000L - 400_000L);
            for (var r = 0; r < 32; r++) w.Int(0, 8, r % 8);
            // col3 double: 32 × (int + double)
            for (var r = 0; r < 32; r++) { w.Int(-1_000_000, 1_000_001, r * 1000); w.Double(r / 32.0); }
            // col4 byte[] Plain: 32 × (len + bytes)
            for (var r = 0; r < 32; r++) { w.Int(0, 33, r % 16); w.Bytes(new byte[r % 16]); }
        }
        return w.GetBytes();
    }

    /// <summary>5 columns (bool+int32 Delta+int64 Delta+double Plain+byte[] Delta), 3 row groups, 48 rows.</summary>
    static byte[] MaxColumnsDelta()
    {
        var w = new SeedWriter();
        w.Int(1, 6, 5);
        w.Int(0, 5, 0);               // col0: bool
        w.Int(0, 5, 1);               // col1: int32
        w.Int(0, 4, 1);               // col1: DeltaBinaryPacked
        w.Int(0, 5, 2);               // col2: int64
        w.Int(0, 4, 1);               // col2: DeltaBinaryPacked
        w.Int(0, 5, 3);               // col3: double
        w.Int(0, 2, 0);               // col3: Plain
        w.Int(0, 5, 4);               // col4: byte[]
        w.Int(0, 3, 2);               // col4: DeltaByteArray
        w.Int(1, 4, 3);
        for (var rg = 0; rg < 3; rg++)
        {
            w.Int(1, 65, 48);
            for (var r = 0; r < 48; r++) w.Int(0, 2, r % 2);
            // col1 delta int32
            w.Int(-100_000, 100_001, 0);
            for (var r = 0; r < 48; r++) w.Int(-2, 11, r % 5);
            // col2 delta int64
            w.Int64(-1_000_000L, 1_000_001L, 0L);
            for (var r = 0; r < 48; r++) w.Int(-4, 8193, r % 100);
            // col3 double plain
            for (var r = 0; r < 48; r++) { w.Int(-1_000_000, 1_000_001, r * 500); w.Double(r / 48.0); }
            // col4 byte[] DeltaByteArray (shared prefix)
            var prefix = new byte[] { 0x70, 0x61, 0x72 };
            w.Int(0, 7, 3);
            w.Bytes(prefix);
            for (var r = 0; r < 48; r++)
            {
                w.Int(0, 29, r % 8);
                w.Bytes(Enumerable.Range(0, r % 8).Select(i => (byte)(i + r)).ToArray());
            }
        }
        return w.GetBytes();
    }

    /// <summary>3 columns (byte[] with each byte-array encoding), 3 row groups, 64 rows (max rows).</summary>
    static byte[] MaxRowGroupsByteArray()
    {
        var w = new SeedWriter();
        w.Int(1, 6, 3);
        w.Int(0, 5, 4); w.Int(0, 3, 0); // col0: byte[] Plain
        w.Int(0, 5, 4); w.Int(0, 3, 1); // col1: byte[] DeltaLengthByteArray
        w.Int(0, 5, 4); w.Int(0, 3, 2); // col2: byte[] DeltaByteArray
        w.Int(1, 4, 3);
        for (var rg = 0; rg < 3; rg++)
        {
            w.Int(1, 65, 64);
            // col0 plain random bytes
            for (var r = 0; r < 64; r++) { w.Int(0, 33, r % 32 + 1); w.Bytes(Enumerable.Range(0, r % 32 + 1).Select(i => (byte)(i ^ r)).ToArray()); }
            // col1 delta-length: vary lengths, random content
            for (var r = 0; r < 64; r++) { w.Int(0, 33, r % 20); w.Bytes(new byte[r % 20]); }
            // col2 delta-byte: shared prefix
            var pfxLen = 4;
            w.Int(0, 7, pfxLen);
            w.Bytes([0xDE, 0xAD, 0xBE, 0xEF]);
            for (var r = 0; r < 64; r++)
            {
                w.Int(0, 29, r % 12);
                w.Bytes(Enumerable.Range(0, r % 12).Select(i => (byte)(i * r + 1)).ToArray());
            }
        }
        return w.GetBytes();
    }

    /// <summary>1 column (int32 plain), 1 row group, 64 rows — minimal but covers max row path.</summary>
    static byte[] SingleColumnPlainManyRows()
    {
        var w = new SeedWriter();
        w.Int(1, 6, 1);
        w.Int(0, 5, 1); w.Int(0, 4, 0); // col0: int32 Plain
        w.Int(1, 4, 1);
        w.Int(1, 65, 64);
        for (var r = 0; r < 64; r++) w.Int(-1_000_000, 1_000_001, r * 15731 - 500_000);
        return w.GetBytes();
    }

    /// <summary>5 columns (one of each type, all plain), 1 row group, 16 rows.</summary>
    static byte[] AllTypesPlain()
    {
        var w = new SeedWriter();
        w.Int(1, 6, 5);
        w.Int(0, 5, 0);               // bool
        w.Int(0, 5, 1); w.Int(0, 4, 0); // int32 plain
        w.Int(0, 5, 2); w.Int(0, 4, 0); // int64 plain
        w.Int(0, 5, 3); w.Int(0, 2, 0); // double plain
        w.Int(0, 5, 4); w.Int(0, 3, 0); // byte[] plain
        w.Int(1, 4, 1);
        w.Int(1, 65, 16);
        for (var r = 0; r < 16; r++) w.Int(0, 2, r % 2);
        for (var r = 0; r < 16; r++) w.Int(-1_000_000, 1_000_001, r * 12345);
        for (var r = 0; r < 16; r++) w.Int64(-10_000_000_000L, 10_000_000_001L, (long)r * 987654321L);
        for (var r = 0; r < 16; r++) { w.Int(-1_000_000, 1_000_001, r * 1000); w.Double(r / 16.0); }
        for (var r = 0; r < 16; r++) { w.Int(0, 33, r + 1); w.Bytes(Enumerable.Range(0, r + 1).Select(i => (byte)(i + r)).ToArray()); }
        return w.GetBytes();
    }

    /// <summary>1 column (int32 plain-dict, 8 entries), 3 row groups — stresses dictionary encoding at max dict size.</summary>
    static byte[] DictLargeCardinality()
    {
        var w = new SeedWriter();
        w.Int(1, 6, 1);
        w.Int(0, 5, 1); w.Int(0, 4, 2); // col0: int32 PlainDictionary
        w.Int(1, 4, 3);
        for (var rg = 0; rg < 3; rg++)
        {
            w.Int(1, 65, 64);
            w.Int(1, 9, 8); // dict size = 8
            for (var i = 0; i < 8; i++) w.Int(-4096, 4097, (i - 4) * 1000);
            for (var r = 0; r < 64; r++) w.Int(0, 8, r % 8);
        }
        return w.GetBytes();
    }

    sealed class SeedWriter
    {
        readonly List<byte> _bytes = [];

        /// <summary>Writes the bytes needed for NextInt(min, max) to return <paramref name="value"/>.</summary>
        public void Int(int minInclusive, int maxExclusive, int value)
        {
            var raw = (uint)(value - minInclusive);
            _bytes.Add((byte)(raw & 0xFF));
            _bytes.Add((byte)((raw >> 8) & 0xFF));
            _bytes.Add((byte)((raw >> 16) & 0xFF));
            _bytes.Add((byte)((raw >> 24) & 0xFF));
        }

        /// <summary>Writes the bytes needed for NextInt64(min, max) to return <paramref name="value"/>.</summary>
        public void Int64(long minInclusive, long maxExclusive, long value)
        {
            // NextUInt64 = (NextUInt32() << 32) | NextUInt32()
            // high 32 bits come first, low 32 bits second
            var raw = (ulong)(value - minInclusive);
            var hi = (uint)(raw >> 32);
            var lo = (uint)(raw & 0xFFFF_FFFF);
            // write hi first (4 bytes LE), then lo (4 bytes LE)
            _bytes.Add((byte)(hi & 0xFF));
            _bytes.Add((byte)((hi >> 8) & 0xFF));
            _bytes.Add((byte)((hi >> 16) & 0xFF));
            _bytes.Add((byte)((hi >> 24) & 0xFF));
            _bytes.Add((byte)(lo & 0xFF));
            _bytes.Add((byte)((lo >> 8) & 0xFF));
            _bytes.Add((byte)((lo >> 16) & 0xFF));
            _bytes.Add((byte)((lo >> 24) & 0xFF));
        }

        /// <summary>Writes the bytes needed for NextDouble() to return approximately <paramref name="value"/> in [0,1).</summary>
        public void Double(double value)
        {
            // NextDouble = NextUInt64 / (ulong.MaxValue + 1.0)
            // reverse: raw = (ulong)(value * (ulong.MaxValue + 1.0))
            var raw = (ulong)(value * ((double)ulong.MaxValue + 1.0));
            var hi = (uint)(raw >> 32);
            var lo = (uint)(raw & 0xFFFF_FFFF);
            _bytes.Add((byte)(hi & 0xFF));
            _bytes.Add((byte)((hi >> 8) & 0xFF));
            _bytes.Add((byte)((hi >> 16) & 0xFF));
            _bytes.Add((byte)((hi >> 24) & 0xFF));
            _bytes.Add((byte)(lo & 0xFF));
            _bytes.Add((byte)((lo >> 8) & 0xFF));
            _bytes.Add((byte)((lo >> 16) & 0xFF));
            _bytes.Add((byte)((lo >> 24) & 0xFF));
        }

        public void Bytes(byte[] data) => _bytes.AddRange(data);

        public byte[] GetBytes() => [.. _bytes];
    }
}
