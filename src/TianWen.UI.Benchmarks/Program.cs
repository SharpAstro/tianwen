using BenchmarkDotNet.Running;
using TianWen.UI.Benchmarks;

// Profiling mode: `dotnet TianWen.UI.Benchmarks.dll profile cr2|cr3|tiff [--seconds N]`
// — tight-loops one decode for N seconds (default 15) so a sampling profiler
// (e.g. `dotnet-trace collect --name TianWen.UI.Benchmarks --profile cpu-sampling`)
// has enough samples to pinpoint hotspots. Bypasses BenchmarkDotNet's harness
// because BDN's iteration framework adds its own overhead to the trace.
if (args.Length >= 2 && args[0] == "profile")
{
    Profiling.Run(args[1], args);
    return;
}

BenchmarkSwitcher.FromAssembly(typeof(ImageBenchmarks).Assembly).Run(args);
