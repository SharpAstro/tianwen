using System;
using System.Diagnostics;
using System.IO;
using TianWen.Lib.Imaging;

namespace TianWen.UI.Benchmarks;

/// <summary>
/// Profiling driver — tight-loops a single image-decode path so an external
/// sampling profiler can attach via <c>dotnet-trace</c>. Distinct from
/// <see cref="ImageReadBenchmarks"/> because BenchmarkDotNet's iteration
/// harness adds overhead and event spans of its own to the trace, drowning
/// out the per-method samples we actually want to see.
/// <para>Usage:</para>
/// <code>
/// # Terminal A (loops the decode for 15s, prints throughput)
/// dotnet TianWen.UI.Benchmarks.dll profile cr2 --seconds 15
///
/// # Terminal B (start trace; converts to .speedscope.json on exit)
/// dotnet-trace collect --name TianWen.UI.Benchmarks --profile cpu-sampling \
///     --duration 00:00:12 --format Speedscope
/// </code>
/// <para>The benchmark process keeps decoding until the duration elapses,
/// then prints "decoded N frames in X seconds (Y ms/frame)" so you can
/// sanity-check the trace timeline against the throughput.</para>
/// </summary>
internal static class Profiling
{
    public static void Run(string format, string[] args)
    {
        var seconds = 15;
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--seconds" && int.TryParse(args[i + 1], out var s))
                seconds = s;
        }

        var fixturesDir = Path.Combine(AppContext.BaseDirectory, "Fixtures");
        var path = format.ToLowerInvariant() switch
        {
            "cr2" => Path.Combine(fixturesDir, "CR2", "_MG_7578.CR2"),
            "cr3" => Path.Combine(fixturesDir, "CR3", "Canon_EOS_R5_CRAW.CR3"),
            "tiff" => SetupSyntheticTiff(),
            _ => throw new ArgumentException(
                $"unknown format '{format}', expected cr2 / cr3 / tiff", nameof(format))
        };
        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"fixture not found: {path}");
            return;
        }

        Console.WriteLine($"Profiling decode of {format.ToUpperInvariant()} ({Path.GetFileName(path)}) for {seconds}s.");
        Console.WriteLine($"Process PID: {Environment.ProcessId} — attach trace now:");
        Console.WriteLine($"  dotnet-trace collect --process-id {Environment.ProcessId} --profile cpu-sampling --duration 00:00:{seconds - 2:D2} --format Speedscope");
        Console.WriteLine();
        Console.WriteLine("Looping decode...");

        var deadline = TimeSpan.FromSeconds(seconds);
        var sw = Stopwatch.StartNew();
        var count = 0;
        while (sw.Elapsed < deadline)
        {
            Image.TryReadImageFile(path, out _);
            count++;
        }
        sw.Stop();

        Console.WriteLine();
        Console.WriteLine($"decoded {count} frames in {sw.Elapsed.TotalSeconds:F2}s " +
            $"({sw.Elapsed.TotalMilliseconds / Math.Max(1, count):F1} ms/frame)");
    }

    /// <summary>Returns the path to a synthetic TIFF used by the TIFF
    /// profile mode, writing it on first use (same one ImageReadBenchmarks
    /// uses; we re-find it here rather than duplicate the writer).</summary>
    private static string SetupSyntheticTiff()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "TianWen.UI.Benchmarks", "ImageReadBenchmarks");
        var path = Path.Combine(tempDir, "synthetic_4096x4096_uint16.tif");
        if (File.Exists(path)) return path;
        // Force the benchmark setup to materialise it. Hacky but keeps the
        // synthetic-TIFF writer in one place — Profiling.cs doesn't need to
        // own a copy of it.
        var bench = new ImageReadBenchmarks();
        bench.Setup();
        return path;
    }
}
