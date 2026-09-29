#!/usr/bin/env python3
"""Generate the constant-width portable INT32 mini-block unpackers."""

from pathlib import Path
import sys


OUTPUT = (
    Path(__file__).resolve().parents[1]
    / "Plank/Reading/Logical/Internal/DeltaBinaryPackedDecoder.FastInt32.cs"
)


def generate() -> str:
    lines = [
        "// Generated for every legal INT32 residual width by tools/generate-int32-unpack.py.",
        "using System.Buffers.Binary;",
        "using System.Runtime.CompilerServices;",
        "using System.Runtime.InteropServices;",
        "",
        "namespace Plank.Reading.Logical.Internal;",
        "",
        "static partial class DeltaBinaryPackedDecoder",
        "{",
        "    static void DecodeInt32MiniBlockFast(ReadOnlySpan<byte> packed, int bitWidth, long minDelta,",
        "        ref long previous, Span<int> destination)",
        "    {",
        "        switch (bitWidth)",
        "        {",
    ]
    for width in range(1, 57):
        lines.append(
            f"            case {width}: DecodeInt32MiniBlockFastW{width}(packed, minDelta, ref previous, destination); return;"
        )
    lines += [
        "            default: throw new ArgumentOutOfRangeException(nameof(bitWidth));",
        "        }",
        "    }",
    ]

    for width in range(1, 57):
        mask = f"0x{(1 << width) - 1:X}UL"
        lines += [
            "",
            "    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]",
            f"    static void DecodeInt32MiniBlockFastW{width}(ReadOnlySpan<byte> packed, long minDelta,",
            "        ref long previous, Span<int> destination)",
            "    {",
            "        ref var source = ref MemoryMarshal.GetReference(packed);",
            "        ref var target = ref MemoryMarshal.GetReference(destination);",
            "        var current = previous;",
            "        for (var group = 0; group < 4; group++)",
            "        {",
            f"            var byteOffset = group * {width};",
        ]
        for word in range((width + 7) // 8):
            lines += [
                f"            var word{word} = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + {word * 8}));",
                f"            if (!BitConverter.IsLittleEndian) word{word} = BinaryPrimitives.ReverseEndianness(word{word});",
            ]
        for lane in range(8):
            bit = lane * width
            word = bit // 64
            shift = bit % 64
            expression = f"word{word}" if shift == 0 else f"(word{word} >> {shift})"
            if shift + width > 64:
                expression = f"({expression} | (word{word + 1} << {64 - shift}))"
            lines += [
                f"            current = unchecked(current + minDelta + (long)({expression} & {mask}));",
                f"            Unsafe.Add(ref target, group * 8 + {lane}) = unchecked((int)current);",
            ]
        lines += ["        }", "        previous = current;", "    }"]
    lines.append("}")
    return "\n".join(lines) + "\n"


if __name__ == "__main__":
    generated = generate()
    if "--check" in sys.argv:
        if OUTPUT.read_text() != generated:
            raise SystemExit("generated INT32 unpackers are out of date")
    else:
        OUTPUT.write_text(generated)
