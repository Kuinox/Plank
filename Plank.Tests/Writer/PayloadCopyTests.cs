using Plank.Writing.Encoding;

namespace Plank.Tests.Writer;

/// <summary>
/// Short BYTE_ARRAY payloads are copied and compared inline rather than through CoreLib, so these pin
/// every length band those helpers branch on - including the ones where the copy's two loads overlap -
/// that the copy still refuses a destination too small for the payload, and that the comparison agrees
/// with SequenceCompareTo everywhere.
/// </summary>
internal sealed class PayloadCopyTests
{
    [Test]
    public void EveryLengthCopiesExactly()
    {
        for (var length = 0; length <= 40; length++)
        {
            var source = new byte[length];
            for (var i = 0; i < length; i++)
                source[i] = (byte)(i + 1);

            var destination = new byte[length];
            EncodingPrimitives.CopyPayload(source, destination);

            if (!destination.AsSpan().SequenceEqual(source))
                throw new InvalidOperationException(
                    $"Length {length} copied wrong: expected {Convert.ToHexString(source)}, "
                    + $"got {Convert.ToHexString(destination)}.");
        }
    }

    [Test]
    public void TheCopyStaysInsideThePayloadLength()
    {
        // A ladder that rounds the length up would spill into whatever follows in the page buffer.
        for (var length = 0; length <= 40; length++)
        {
            var source = new byte[length];
            source.AsSpan().Fill(0xAB);
            var destination = new byte[length + 8];
            destination.AsSpan().Fill(0xCD);

            EncodingPrimitives.CopyPayload(source, destination);

            for (var i = length; i < destination.Length; i++)
                if (destination[i] != 0xCD)
                    throw new InvalidOperationException(
                        $"Length {length} wrote past its payload at offset {i}.");
        }
    }

    [Test]
    public void ADestinationTooSmallThrows()
    {
        var threw = false;
        try
        {
            EncodingPrimitives.CopyPayload(new byte[8], new byte[7]);
        }
        catch (ArgumentOutOfRangeException)
        {
            threw = true;
        }

        if (!threw)
            throw new InvalidOperationException("A destination shorter than the payload has to throw.");
    }

    [Test]
    public void ComparisonAgreesWithSequenceCompareTo()
    {
        ReadOnlySpan<byte> alphabet = "abz"u8;
        var values = new List<byte[]> { Array.Empty<byte>() };
        for (var length = 1; length <= 12; length++)
            for (var seed = 0; seed < 24; seed++)
            {
                var value = new byte[length];
                var state = (uint)(seed + length * 7);
                for (var i = 0; i < length; i++)
                {
                    value[i] = alphabet[(int)(state % (uint)alphabet.Length)];
                    state = state * 31u + 11u;
                }

                values.Add(value);
            }

        foreach (var left in values)
            foreach (var right in values)
            {
                var expected = Math.Sign(((ReadOnlySpan<byte>)left).SequenceCompareTo(right));
                var actual = Math.Sign(EncodingPrimitives.ComparePayload(left, right));
                if (expected != actual)
                    throw new InvalidOperationException(
                        $"Comparing {Convert.ToHexString(left)} with {Convert.ToHexString(right)} gave "
                        + $"{actual}, expected {expected}.");
        }
    }

    [Test]
    public void EqualityAgreesWithSequenceEqualForEveryLengthBand()
    {
        for (var length = 0; length <= 40; length++)
        {
            var left = new byte[length];
            for (var i = 0; i < length; i++)
                left[i] = (byte)(i * 29 + length);
            var right = left.ToArray();

            if (!EncodingPrimitives.PayloadEquals(left, right))
                throw new InvalidOperationException($"Equal payloads of length {length} compared unequal.");

            for (var changedIndex = 0; changedIndex < length; changedIndex++)
            {
                right[changedIndex]++;
                if (EncodingPrimitives.PayloadEquals(left, right))
                    throw new InvalidOperationException(
                        $"Payloads of length {length} compared equal after byte {changedIndex} changed.");
                right[changedIndex]--;
            }

            if (EncodingPrimitives.PayloadEquals(left, new byte[length + 1]))
                throw new InvalidOperationException($"Payloads with different lengths compared equal at {length}.");
        }
    }
}
