namespace Plank;

static class ParquetCrc32
{
    internal const uint InitialState = uint.MaxValue;

    internal static uint Append(uint state, ReadOnlySpan<byte> source)
    {
        for (var i = 0; i < source.Length; i++)
        {
            state ^= source[i];
            state = state >> 4 ^ LookupNibble(state & 0x0F);
            state = state >> 4 ^ LookupNibble(state & 0x0F);
        }

        return state;
    }

    internal static uint Complete(uint state)
        => ~state;

    internal static uint Compute(ReadOnlySpan<byte> source)
        => Complete(Append(InitialState, source));

    static uint LookupNibble(uint value)
        => value switch
        {
            0 => 0x00000000U,
            1 => 0x1DB71064U,
            2 => 0x3B6E20C8U,
            3 => 0x26D930ACU,
            4 => 0x76DC4190U,
            5 => 0x6B6B51F4U,
            6 => 0x4DB26158U,
            7 => 0x5005713CU,
            8 => 0xEDB88320U,
            9 => 0xF00F9344U,
            10 => 0xD6D6A3E8U,
            11 => 0xCB61B38CU,
            12 => 0x9B64C2B0U,
            13 => 0x86D3D2D4U,
            14 => 0xA00AE278U,
            15 => 0xBDBDF21CU,
            _ => throw new ArgumentOutOfRangeException(nameof(value))
        };
}
