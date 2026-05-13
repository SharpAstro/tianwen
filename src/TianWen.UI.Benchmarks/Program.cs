using System;
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

// One-shot smoke run for the LosslessJpeg benchmark — useful when BDN
// reports "non-zero exit code" but hides the actual exception. Calls
// Setup() + the benchmark method once and lets any exception escape.
if (args.Length >= 1 && args[0] == "verify-ljpeg")
{
    var b = new LosslessJpegBenchmarks();
    b.Setup();
    var r = b.DecodeRawIfdStrip();
    Console.WriteLine($"OK {r.Width}x{r.Height} components={r.Components} precision={r.Precision} samples={r.Samples.Length}");
    return;
}

BenchmarkSwitcher.FromAssembly(typeof(ImageBenchmarks).Assembly).Run(args);
