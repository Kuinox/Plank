namespace Plank;

static class AlpEncodingPrimitives
{
    internal const int FloatMaxExponent = 10;
    internal const int DoubleMaxExponent = 18;

    static ReadOnlySpan<float> FloatPowersOfTen =>
    [
        1e0f, 1e1f, 1e2f, 1e3f, 1e4f, 1e5f, 1e6f, 1e7f, 1e8f, 1e9f, 1e10f
    ];

    static ReadOnlySpan<float> FloatInversePowersOfTen =>
    [
        1e0f, 1e-1f, 1e-2f, 1e-3f, 1e-4f, 1e-5f, 1e-6f, 1e-7f, 1e-8f, 1e-9f, 1e-10f
    ];

    static ReadOnlySpan<double> DoublePowersOfTen =>
    [
        1e0, 1e1, 1e2, 1e3, 1e4, 1e5, 1e6, 1e7, 1e8, 1e9,
        1e10, 1e11, 1e12, 1e13, 1e14, 1e15, 1e16, 1e17, 1e18
    ];

    static ReadOnlySpan<double> DoubleInversePowersOfTen =>
    [
        1e0, 1e-1, 1e-2, 1e-3, 1e-4, 1e-5, 1e-6, 1e-7, 1e-8, 1e-9,
        1e-10, 1e-11, 1e-12, 1e-13, 1e-14, 1e-15, 1e-16, 1e-17, 1e-18
    ];

    internal static bool TryEncode(float value, int exponent, int factor, out int encoded)
    {
        encoded = 0;
        if (!float.IsFinite(value) ||
            value == 0 && BitConverter.SingleToInt32Bits(value) < 0)
            return false;

        var scaled = value * FloatPowersOfTen[exponent] * FloatInversePowersOfTen[factor];
        var rounded = MathF.Round(scaled, MidpointRounding.ToEven);
        if (!float.IsFinite(rounded) || rounded < int.MinValue || rounded >= 2_147_483_648f)
            return false;

        encoded = (int)rounded;
        var decoded = Decode(encoded, exponent, factor);
        return BitConverter.SingleToInt32Bits(decoded) == BitConverter.SingleToInt32Bits(value);
    }

    internal static bool TryEncode(double value, int exponent, int factor, out long encoded)
    {
        encoded = 0;
        if (!double.IsFinite(value) ||
            value == 0 && BitConverter.DoubleToInt64Bits(value) < 0)
            return false;

        var scaled = value * DoublePowersOfTen[exponent] * DoubleInversePowersOfTen[factor];
        var rounded = Math.Round(scaled, MidpointRounding.ToEven);
        if (!double.IsFinite(rounded) || rounded < long.MinValue || rounded >= 9_223_372_036_854_775_808d)
            return false;

        encoded = (long)rounded;
        var decoded = Decode(encoded, exponent, factor);
        return BitConverter.DoubleToInt64Bits(decoded) == BitConverter.DoubleToInt64Bits(value);
    }

    internal static float Decode(int encoded, int exponent, int factor)
        => (float)encoded * FloatPowersOfTen[factor] * FloatInversePowersOfTen[exponent];

    internal static double Decode(long encoded, int exponent, int factor)
        => (double)encoded * DoublePowersOfTen[factor] * DoubleInversePowersOfTen[exponent];
}
