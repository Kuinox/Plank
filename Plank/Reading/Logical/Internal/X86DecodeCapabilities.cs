using System.Runtime.Intrinsics.X86;

namespace Plank.Reading.Logical.Internal;

static class X86DecodeCapabilities
{
    // AMD family 17h (Zen 1/2) implements PDEP in microcode. Instruction support
    // alone must not select that path, including for fields smaller than a byte.
    internal static readonly bool UsePdep = Bmi2.X64.IsSupported && !HasSlowPdep();

    // Initialize the CPU choice before callers are compiled, so the JIT can
    // discard the unused kernel branch.
    static X86DecodeCapabilities() { }

    static bool HasSlowPdep()
    {
        if (!X86Base.IsSupported) return false;
        var (_, ebx, ecx, edx) = X86Base.CpuId(0, 0);
        if (ebx != 0x68747541 || edx != 0x69746e65 || ecx != 0x444d4163) // AuthenticAMD
            return false;
        var (eax, _, _, _) = X86Base.CpuId(1, 0);
        var family = (eax >> 8) & 0xf;
        if (family == 0xf) family += (eax >> 20) & 0xff;
        return family == 0x17;
    }
}
