using Plank.Writing.Encoding;

namespace Plank.Tests.Writer;

internal sealed class WyHashingTests
{
    [Test]
    [Arguments(17)]
    [Arguments(18)]
    [Arguments(19)]
    public void HashDoesNotReadPastSpanBoundary(int length)
        => AssertGuardBytesDoNotAffectHash(length);

    [Test]
    public void HashDoesNotReadPastSpanBoundaryAcrossLengths()
    {
        for (var length = 1; length <= 64; length++)
            AssertGuardBytesDoNotAffectHash(length);
    }

    static void AssertGuardBytesDoNotAffectHash(int length)
    {
        var first = new byte[length + 16];
        var second = new byte[length + 16];
        for (var i = 0; i < length; i++)
        {
            first[i] = (byte)(i * 17 + 3);
            second[i] = first[i];
        }

        first.AsSpan(length).Fill(0x11);
        second.AsSpan(length).Fill(0xE7);

        var firstHash = WyHashing.Hash(first.AsSpan(0, length));
        var secondHash = WyHashing.Hash(second.AsSpan(0, length));

        if (firstHash != secondHash)
            throw new InvalidOperationException(
                $"Equal {length}-byte spans produced different hashes ({firstHash} and {secondHash}) " +
                "because bytes beyond the span boundary affected the result.");
    }
}
