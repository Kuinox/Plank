using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Numerics;
using Plank.Schema;

namespace Plank.Writing.Encoding;

static class DeltaByteArrayEncoding
{
    internal static void WriteValues<T>(Column column, ReadOnlySpan<T> values, BufferWriterFactory bufferWriters,
        ref BufferWriter writer)
        where T : notnull
    {
        RequireByteArrayColumn(column);
        if (typeof(T) == typeof(byte[]))
        {
            WriteRequiredByteArrayPayloads(column,
                Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<byte[]>>(ref values), bufferWriters, ref writer);
            return;
        }
        if (typeof(T) == typeof(ReadOnlyMemory<byte>))
        {
            WritePayloads<ReadOnlyMemory<byte>, RequiredMemoryRow>(column,
                Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<ReadOnlyMemory<byte>>>(ref values), bufferWriters,
                ref writer);
            return;
        }

        throw new InvalidOperationException(
            $"Column '{column.Name}' expects '{ParquetPhysicalType.ByteArray}' values, but got '{typeof(T)}'.");
    }

    // Required byte[] is a shared-generic reference-type instantiation. A concrete loop avoids the
    // row-access abstraction cost while the optional and memory shapes still share WritePayloads.
    static void WriteRequiredByteArrayPayloads(Column column, ReadOnlySpan<byte[]> values,
        BufferWriterFactory bufferWriters, ref BufferWriter writer)
    {
        var byteLength = checked(values.Length * sizeof(int));
        var rentedPrefixLengthsBytes = bufferWriters.RentScratch(checked((uint)Math.Max(byteLength, sizeof(int))));
        var rentedSuffixLengthsBytes = bufferWriters.RentScratch(checked((uint)Math.Max(byteLength, sizeof(int))));
        var prefixLengths = MemoryMarshal.Cast<byte, int>(rentedPrefixLengthsBytes.Span[..byteLength]);
        var suffixLengths = MemoryMarshal.Cast<byte, int>(rentedSuffixLengthsBytes.Span[..byteLength]);
        var totalSuffixBytes = 0;

        try
        {
            ReadOnlySpan<byte> previous = [];
            for (var i = 0; i < values.Length; i++)
            {
                var current = values[i] ?? throw new InvalidOperationException(
                    $"Column '{column.Name}' does not support null values.");
                var prefixLength = previous.CommonPrefixLength(current);
                var suffixLength = current.Length - prefixLength;
                prefixLengths[i] = prefixLength;
                suffixLengths[i] = suffixLength;
                totalSuffixBytes = checked(totalSuffixBytes + suffixLength);
                previous = current;
            }

            WritePrecomputedRequiredByteArrayPage(values, prefixLengths, suffixLengths, totalSuffixBytes,
                ref writer);
        }
        finally
        {
            bufferWriters.ReturnScratch(rentedPrefixLengthsBytes);
            bufferWriters.ReturnScratch(rentedSuffixLengthsBytes);
        }
    }

    /// <summary>
    /// Writes a required byte-array page whose prefix and suffix lengths were already discovered by
    /// the target-page sizing pass.
    /// </summary>
    /// <remarks>
    /// Keeping the length spans for the whole column lets the target-sized writer avoid both a second
    /// traversal of the page's jagged references and a pair of scratch-buffer rents per page. The
    /// remaining traversal copies the suffix payloads into their final contiguous destination.
    /// </remarks>
    internal static void WritePrecomputedRequiredByteArrayPage(ReadOnlySpan<byte[]> values,
        ReadOnlySpan<byte> prefixLengths, ReadOnlySpan<byte> suffixLengths, int totalSuffixBytes,
        ref BufferWriter writer)
    {
        if (prefixLengths.Length != values.Length)
            throw new ArgumentException("The prefix-length count must match the value count.",
                nameof(prefixLengths));
        if (suffixLengths.Length != values.Length)
            throw new ArgumentException("The suffix-length count must match the value count.",
                nameof(suffixLengths));
        if (totalSuffixBytes < 0)
            throw new ArgumentOutOfRangeException(nameof(totalSuffixBytes), totalSuffixBytes,
                "The suffix byte count cannot be negative.");

        DeltaBinaryPackedEncoding.WriteByteValuesAsInt32(prefixLengths, ref writer);
        DeltaBinaryPackedEncoding.WriteByteValuesAsInt32(suffixLengths, ref writer);
        WritePrecomputedSuffixPayloads(values, prefixLengths, suffixLengths, totalSuffixBytes, ref writer);
    }

    internal static void WritePrecomputedRequiredByteArrayPage(ReadOnlySpan<byte[]> values,
        ReadOnlySpan<int> prefixLengths, ReadOnlySpan<int> suffixLengths, int totalSuffixBytes,
        ref BufferWriter writer)
    {
        if (prefixLengths.Length != values.Length)
            throw new ArgumentException("The prefix-length count must match the value count.",
                nameof(prefixLengths));
        if (suffixLengths.Length != values.Length)
            throw new ArgumentException("The suffix-length count must match the value count.",
                nameof(suffixLengths));
        if (totalSuffixBytes < 0)
            throw new ArgumentOutOfRangeException(nameof(totalSuffixBytes), totalSuffixBytes,
                "The suffix byte count cannot be negative.");

        DeltaBinaryPackedEncoding.WriteInt32(prefixLengths, ref writer);
        DeltaBinaryPackedEncoding.WriteInt32(suffixLengths, ref writer);
        WritePrecomputedSuffixPayloads(values, prefixLengths, suffixLengths, totalSuffixBytes, ref writer);
    }

    static void WritePrecomputedSuffixPayloads<TLength>(ReadOnlySpan<byte[]> values,
        ReadOnlySpan<TLength> prefixLengths, ReadOnlySpan<TLength> suffixLengths, int totalSuffixBytes,
        ref BufferWriter writer)
        where TLength : unmanaged, IBinaryInteger<TLength>
    {
        if (totalSuffixBytes == 0)
            return;

        var destination = writer.GetSpan(totalSuffixBytes);
        var offset = 0;
        for (var i = 0; i < values.Length; i++)
        {
            var current = values[i]!;
            var prefixLength = int.CreateChecked(prefixLengths[i]);
            var suffixLength = int.CreateChecked(suffixLengths[i]);
            if (suffixLength == 0)
                continue;
            EncodingPrimitives.CopyPayload(current.AsSpan(prefixLength, suffixLength), destination[offset..]);
            offset += suffixLength;
        }

        writer.Advance(offset);
    }

    internal static void WriteOptionalValues<TRow, TRowAccess>(Column column, ReadOnlySpan<TRow> rows,
        BufferWriterFactory bufferWriters, ref BufferWriter writer)
        where TRowAccess : IByteArrayRow<TRow>
    {
        if (typeof(TRow) == typeof(byte[]))
        {
            WriteOptionalByteArrayPayloads(column,
                Unsafe.As<ReadOnlySpan<TRow>, ReadOnlySpan<byte[]>>(ref rows), bufferWriters, ref writer);
            return;
        }

        WritePayloads<TRow, TRowAccess>(column, rows, bufferWriters, ref writer);
    }

    static void WriteOptionalByteArrayPayloads(Column column, ReadOnlySpan<byte[]> values,
        BufferWriterFactory bufferWriters, ref BufferWriter writer)
    {
        var presentCount = 0;
        for (var i = 0; i < values.Length; i++)
            if (values[i] is not null)
                presentCount++;
        // Every DELTA_* encoding begins with a mandatory header, so a page with
        // nothing present still has to emit one. Writing nothing produced a data
        // section that neither Plank nor arrow-cpp can decode
        // ("Unexpected end of stream: InitHeader EOF").

        var byteLength = checked(presentCount * sizeof(int));
        var rentedPrefixLengthsBytes = bufferWriters.RentScratch(checked((uint)Math.Max(byteLength, sizeof(int))));
        var rentedSuffixLengthsBytes = bufferWriters.RentScratch(checked((uint)Math.Max(byteLength, sizeof(int))));
        var prefixLengths = MemoryMarshal.Cast<byte, int>(rentedPrefixLengthsBytes.Span[..byteLength]);
        var suffixLengths = MemoryMarshal.Cast<byte, int>(rentedSuffixLengthsBytes.Span[..byteLength]);
        var totalSuffixBytes = 0;

        try
        {
            ReadOnlySpan<byte> previous = [];
            var denseIndex = 0;
            for (var i = 0; i < values.Length; i++)
            {
                var current = values[i];
                if (current is null)
                    continue;
                var prefixLength = previous.CommonPrefixLength(current);
                var suffixLength = current.Length - prefixLength;
                prefixLengths[denseIndex] = prefixLength;
                suffixLengths[denseIndex] = suffixLength;
                denseIndex++;
                totalSuffixBytes = checked(totalSuffixBytes + suffixLength);
                previous = current;
            }

            DeltaBinaryPackedEncoding.WriteInt32(prefixLengths, ref writer);
            DeltaBinaryPackedEncoding.WriteInt32(suffixLengths, ref writer);
            if (totalSuffixBytes == 0)
                return;

            var destination = writer.GetSpan(totalSuffixBytes);
            var offset = 0;
            denseIndex = 0;
            for (var i = 0; i < values.Length; i++)
            {
                var current = values[i];
                if (current is null)
                    continue;
                var prefixLength = prefixLengths[denseIndex];
                var suffixLength = suffixLengths[denseIndex++];
                if (suffixLength == 0)
                    continue;
                EncodingPrimitives.CopyPayload(current.AsSpan(prefixLength, suffixLength), destination[offset..]);
                offset += suffixLength;
            }

            writer.Advance(offset);
        }
        finally
        {
            bufferWriters.ReturnScratch(rentedPrefixLengthsBytes);
            bufferWriters.ReturnScratch(rentedSuffixLengthsBytes);
        }
    }

    /// <summary>
    /// DELTA_BYTE_ARRAY layout: the delta-binary-packed prefix lengths, then the suffix lengths,
    /// then the suffix bytes. Each present value shares a prefix with the previous present value,
    /// so absent rows are skipped without breaking the chain and this serves the required and
    /// optional shapes alike.
    /// </summary>
    static void WritePayloads<TRow, TRowAccess>(Column column, ReadOnlySpan<TRow> rows,
        BufferWriterFactory bufferWriters, ref BufferWriter writer)
        where TRowAccess : IByteArrayRow<TRow>
    {
        var presentCount = ByteArrayRows.CountPresent<TRow, TRowAccess>(rows);
        // Every DELTA_* encoding begins with a mandatory header, so a page with
        // nothing present still has to emit one. Writing nothing produced a data
        // section that neither Plank nor arrow-cpp can decode
        // ("Unexpected end of stream: InitHeader EOF").

        var byteLength = checked(presentCount * sizeof(int));
        var rentedPrefixLengthsBytes = bufferWriters.RentScratch(checked((uint)Math.Max(byteLength, sizeof(int))));
        var rentedSuffixLengthsBytes = bufferWriters.RentScratch(checked((uint)Math.Max(byteLength, sizeof(int))));
        var prefixLengths = MemoryMarshal.Cast<byte, int>(rentedPrefixLengthsBytes.Span[..byteLength]);
        var suffixLengths = MemoryMarshal.Cast<byte, int>(rentedSuffixLengthsBytes.Span[..byteLength]);
        var totalSuffixBytes = 0;

        try
        {
            ReadOnlySpan<byte> previous = [];
            var denseIndex = 0;
            for (var i = 0; i < rows.Length; i++)
            {
                if (!ByteArrayRows.TryGetPayload<TRow, TRowAccess>(column, in rows[i], out var current))
                    continue;

                var prefixLength = previous.CommonPrefixLength(current);
                var suffixLength = current.Length - prefixLength;
                prefixLengths[denseIndex] = prefixLength;
                suffixLengths[denseIndex] = suffixLength;
                denseIndex++;
                totalSuffixBytes = checked(totalSuffixBytes + suffixLength);
                previous = current;
            }

            DeltaBinaryPackedEncoding.WriteInt32(prefixLengths, ref writer);
            DeltaBinaryPackedEncoding.WriteInt32(suffixLengths, ref writer);
            if (totalSuffixBytes == 0)
                return;

            var suffixDestination = writer.GetSpan(totalSuffixBytes);
            var suffixOffset = 0;
            denseIndex = 0;
            for (var i = 0; i < rows.Length; i++)
            {
                if (!ByteArrayRows.TryGetPayload<TRow, TRowAccess>(column, in rows[i], out var current))
                    continue;

                var prefixLength = prefixLengths[denseIndex];
                var suffixLength = suffixLengths[denseIndex++];
                if (suffixLength > 0)
                {
                    current.Slice(prefixLength, suffixLength).CopyTo(suffixDestination[suffixOffset..]);
                    suffixOffset += suffixLength;
                }
            }

            writer.Advance(suffixOffset);
        }
        finally
        {
            bufferWriters.ReturnScratch(rentedPrefixLengthsBytes);
            bufferWriters.ReturnScratch(rentedSuffixLengthsBytes);
        }
    }

    static void RequireByteArrayColumn(Column column)
    {
        if (column.PhysicalType != ParquetPhysicalType.ByteArray)
            throw new NotSupportedException(
                $"Encoding '{EncodingKind.DeltaByteArray}' does not support physical type '{column.PhysicalType}' for column '{column.Name}'.");
    }
}
