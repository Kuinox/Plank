using System.Reflection;
using System.Runtime.InteropServices;
using SharpFuzz.Common;

namespace Plank.Fuzzing.Reader.Target;

// AFL++ persistent mode harness without fork.
// Implements the fork-server + shmem-testcase protocol directly so the
// .NET process is never forked — it loops in-place for its entire lifetime.
internal static unsafe class AflPersistentHarness
{
    private const int CtlFd = 198;          // AFL++ writes here to say "go"
    private const int StFd = 199;           // we write handshake / PID / fault here
    private const int MapSize = 65536;
    private const int FaultNone = 0;
    private const int FaultCrash = 2;
    private const string LastInputPath = "/tmp/plank-fuzz-last-input.bin";
    private const int SaveInterval = 10_000;

    [DllImport("libc")] static extern IntPtr shmat(int shmid, IntPtr shmaddr, int shmflg);
    [DllImport("libc")] static extern int shmdt(IntPtr shmaddr);
    [DllImport("libc")] static extern int getpid();
    [DllImport("libc")] static extern nint write(int fd, void* buf, nuint count);
    [DllImport("libc")] static extern nint read(int fd, void* buf, nuint count);

    public static void Run(Action<byte[]> execute)
    {
        if (!TryGetEnvInt("__AFL_SHM_ID", out int shmId))
        {
            // Running outside AFL++ — execute once from stdin
            using var ms = new MemoryStream();
            Console.OpenStandardInput().CopyTo(ms);
            execute(ms.ToArray());
            return;
        }

        // Map AFL++ coverage bitmap and wire it into SharpFuzz IL instrumentation
        IntPtr coverageMem = shmat(shmId, IntPtr.Zero, 0);
        byte* coveragePtr = (byte*)coverageMem;
        Trace.SharedMem = coveragePtr;
        SetupCoreCoverage(coveragePtr);

        // Map shmem testcase buffer (AFL++ 4.x: [u32 len][data...])
        byte* fuzzPtr = null;
        IntPtr fuzzMem = IntPtr.Zero;
        if (TryGetEnvInt("__AFL_SHM_FUZZ_ID", out int shmFuzzId))
        {
            fuzzMem = shmat(shmFuzzId, IntPtr.Zero, 0);
            fuzzPtr = (byte*)fuzzMem;
        }

        // Warm-up: run once with an empty input to trigger all .NET static
        // constructors and JIT tier-0 compilations before entering the AFL loop.
        // Without this, the first real execution sees different coverage than
        // subsequent ones (cctors only fire once), causing AFL instability aborts.
        try { execute(Array.Empty<byte>()); } catch { }
        new Span<byte>(coveragePtr, MapSize).Clear();
        Trace.PrevLocation = 0;

        int pid = getpid();

        // Fork-server handshake: use the "old model" (write 0) which AFL++
        // already handles correctly. Attempting FS_OPT_ENABLED requires a
        // multi-step negotiation we don't implement; mismatching it causes
        // protocol desync and "No instrumentation detected" aborts.
        WriteU32(StFd, 0);

        int execCount = 0;

        while (true)
        {
            if (!ReadU32(CtlFd, out _)) break;   // AFL++ gone

            WriteU32(StFd, (uint)pid);            // "child PID" (we are the child, no fork)

            // Reset coverage bitmap and edge-tracking seed
            new Span<byte>(coveragePtr, MapSize).Clear();
            Trace.PrevLocation = 0;

            // Read testcase from shmem (or stdin as fallback)
            byte[] input = fuzzPtr != null
                ? new Span<byte>(fuzzPtr + 4, (int)*(uint*)fuzzPtr).ToArray()
                : ReadStdin();

            if (++execCount % SaveInterval == 0)
                File.WriteAllBytes(LastInputPath, input);

            int fault = FaultNone;
            try { execute(input); }
            catch { fault = FaultCrash; }

            WriteU32(StFd, (uint)fault);

            if (fault == FaultCrash)
            {
                // FailFast exits immediately — no chance for AFL++ SIGKILL to race
                shmdt(coverageMem);
                if (fuzzMem != IntPtr.Zero) shmdt(fuzzMem);
                Environment.FailFast("fuzz target crashed");
            }
        }
    }

    // If System.Private.CoreLib itself was instrumented, its injected Trace type
    // also needs the shm pointer. This is an uncommon case but handle it correctly.
    static void SetupCoreCoverage(byte* ptr)
    {
        var traceFullName = typeof(Trace).FullName;
        var coreType = typeof(object).Assembly.GetTypes()
            .FirstOrDefault(t => t.FullName == traceFullName);
        if (coreType == null) return;
        coreType.GetField("SharedMem")?.SetValue(null,
            System.Reflection.Pointer.Box(ptr, typeof(byte*)));
        coreType.GetField("PrevLocation")?.SetValue(null, 0);
    }

    static byte[] ReadStdin()
    {
        using var ms = new MemoryStream();
        Console.OpenStandardInput().CopyTo(ms);
        return ms.ToArray();
    }

    static bool TryGetEnvInt(string name, out int value)
    {
        value = 0;
        var s = Environment.GetEnvironmentVariable(name);
        return s != null && int.TryParse(s, out value);
    }

    static void WriteU32(int fd, uint value) => write(fd, &value, 4);

    static bool ReadU32(int fd, out uint value)
    {
        uint v = 0;
        bool ok = read(fd, &v, 4) == 4;
        value = v;
        return ok;
    }
}
