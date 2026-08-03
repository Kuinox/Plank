using System.Security.Principal;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Exporters.Json;
using BenchmarkDotNet.Jobs;

namespace Plank.Benchmarks;

public sealed class OptimizationBenchmarkConfig : ManualConfig
{
    public OptimizationBenchmarkConfig()
    {
        AddJob(Job.ShortRun.DontEnforcePowerPlan());
        AddDiagnoser(MemoryDiagnoser.Default);
        AddDiagnoser(new DisassemblyDiagnoser(new DisassemblyDiagnoserConfig(
            maxDepth: 3,
            printSource: true,
            exportGithubMarkdown: true,
            exportCombinedDisassemblyReport: true)));
        AddExporter(JsonExporter.Full);

        var hardwareCounterSetting = Environment.GetEnvironmentVariable("PLANK_BENCHMARK_HARDWARE_COUNTERS");
        var enableHardwareCounters = hardwareCounterSetting == "1" ||
                                     hardwareCounterSetting is null && IsElevatedWindowsProcess();
        if (enableHardwareCounters)
            AddHardwareCounters(HardwareCounter.BranchInstructions, HardwareCounter.BranchMispredictions);
    }

    static bool IsElevatedWindowsProcess()
    {
        if (!OperatingSystem.IsWindows())
            return false;

        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
