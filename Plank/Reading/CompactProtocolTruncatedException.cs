namespace Plank.Reading;

/// <summary>
/// Signals that a compact-protocol payload known to be a prefix ran out of bytes,
/// and how many more the parse needs before it can get further.
/// </summary>
/// <remarks>
/// Only raised by a reader constructed over a deliberately partial buffer, which
/// today means one caller: the page-header probe, which cannot know a header's
/// length until it has parsed it. Everywhere else a payload that ends early is a
/// corrupt file and still raises <see cref="CorruptParquetException"/>.
///
/// It exists because "not all here yet" and "malformed" used to be told apart by
/// comparing an exception's Message against a literal, and the two conditions do
/// not raise the same message. A half-read binary field reports the bound its
/// length prefix broke, not the end of the payload, so a page header carrying
/// statistics — which every mainstream writer emits and Plank's own writer does
/// not — aborted the probe instead of extending it.
///
/// <see cref="MissingBytes"/> is a lower bound: the field being parsed needs at
/// least that many more. Extending by exactly it and parsing again is what keeps
/// the probe from ever reading a byte of page payload.
/// </remarks>
sealed class CompactProtocolTruncatedException(int missingBytes)
    : Exception($"The compact protocol payload needs at least {missingBytes} more bytes.")
{
    internal int MissingBytes { get; } = missingBytes;
}
