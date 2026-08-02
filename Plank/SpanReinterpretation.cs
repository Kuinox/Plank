using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Plank;

static class SpanReinterpretation
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static Span<TTo> Cast<TFrom, TTo>(Span<TFrom> values)
    {
        Debug.Assert(Unsafe.SizeOf<TFrom>() == Unsafe.SizeOf<TTo>());
        ref var first = ref Unsafe.As<TFrom, TTo>(ref MemoryMarshal.GetReference(values));
        return MemoryMarshal.CreateSpan(ref first, values.Length);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ReadOnlySpan<TTo> Cast<TFrom, TTo>(ReadOnlySpan<TFrom> values)
    {
        Debug.Assert(Unsafe.SizeOf<TFrom>() == Unsafe.SizeOf<TTo>());
        ref var first = ref Unsafe.As<TFrom, TTo>(ref MemoryMarshal.GetReference(values));
        return MemoryMarshal.CreateReadOnlySpan(ref first, values.Length);
    }
}
