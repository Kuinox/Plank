using Plank.Reading.Logical.Internal;

namespace Plank.Tests.Reading;

[NotInParallel]
internal sealed class BooleanBitUnpackerTests
{
    /// <summary>
    /// The unpacker walks a bit at a time until it reaches a byte boundary, then expands 64, 32 or 8
    /// values per step depending on what the machine supports, then finishes a bit at a time again. So
    /// every combination of leading offset and trailing remainder has to agree with reading bit by bit.
    /// </summary>
    [Test]
    public void UnpackMatchesBitByBitReadingAtEveryOffsetAndLength()
    {
        var payload = new byte[256];
        new Random(20260817).NextBytes(payload);

        for (var bitOffset = 0; bitOffset <= 17; bitOffset++)
            for (var length = 0; length <= 200; length++)
            {
                var actual = new bool[length];
                BooleanBitUnpacker.Unpack(payload, bitOffset, actual);

                for (var i = 0; i < length; i++)
                {
                    var bitIndex = bitOffset + i;
                    var expected = ((payload[bitIndex >> 3] >> (bitIndex & 7)) & 1) != 0;
                    if (actual[i] != expected)
                        throw new InvalidOperationException(
                            $"Unpacking {length} values from bit {bitOffset} produced {actual[i]} at "
                            + $"index {i}, expected {expected}.");
                }
            }
    }

    /// <summary>
    /// The wide paths read whole machine words past the value they are expanding, so a payload holding
    /// exactly the bytes the values need must not be over-read.
    /// </summary>
    [Test]
    public void UnpackStaysInsideAPayloadHoldingOnlyTheRequiredBytes()
    {
        for (var length = 1; length <= 300; length++)
        {
            var payload = new byte[(length + 7) / 8];
            new Random(length).NextBytes(payload);
            var actual = new bool[length];

            BooleanBitUnpacker.Unpack(payload, 0, actual);

            for (var i = 0; i < length; i++)
            {
                var expected = ((payload[i >> 3] >> (i & 7)) & 1) != 0;
                if (actual[i] != expected)
                    throw new InvalidOperationException(
                        $"Unpacking {length} values produced {actual[i]} at index {i}, expected {expected}.");
            }
        }
    }

    [Test]
    public void UnpackWritesNothingForAnEmptyDestination()
    {
        BooleanBitUnpacker.Unpack([], 0, []);
        BooleanBitUnpacker.Unpack([0xFF], 3, []);
    }
}
