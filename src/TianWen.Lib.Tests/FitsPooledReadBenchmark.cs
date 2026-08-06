using Shouldly;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib;
using TianWen.Lib.Imaging;
using Xunit;

namespace TianWen.Lib.Tests
{
    /// <summary>
    /// Measures the opt-in pooled FITS read against the default one over a directory of REAL frames,
    /// in a single process so the two are directly comparable. Env-gated and normally skipped -- it
    /// needs an archive, and it is a measurement, not an assertion about a machine's timings.
    ///
    /// <para>Read-only, like the other archive harnesses here: every path is opened for reading and
    /// nothing is written back.</para>
    ///
    /// <para>Run it as:
    /// <code>
    /// TIANWEN_FITS_BENCH=&lt;dir-of-fits&gt; \
    /// dotnet test TianWen.Lib.Tests -c Release --filter FullyQualifiedName~PooledVsUnpooled
    /// </code>
    /// <c>TIANWEN_FITS_BENCH_PARALLEL</c> sets the worker count (default 8, the setting that made
    /// the archive survey run out of memory on 32-bit stacks).</para>
    /// </summary>
    [Collection("Imaging")]
    public class FitsPooledReadBenchmark(ITestOutputHelper output)
    {
        private readonly record struct Measurement(
            string Mode, int Frames, int Failures, double WallSeconds,
            long AllocatedBytes, int Gen0, int Gen1, int Gen2, long PeakWorkingSetBytes);

        [Fact(Timeout = 30 * 60 * 1000)]
        public async Task PooledVsUnpooled()
        {
            if (Environment.GetEnvironmentVariable("TIANWEN_FITS_BENCH") is not { Length: > 0 } dir || !Directory.Exists(dir))
            {
                Assert.Skip("Set TIANWEN_FITS_BENCH to a directory of FITS frames to measure the pooled read.");
                return;
            }

            var ct = TestContext.Current.CancellationToken;
            var parallel = Environment.GetEnvironmentVariable("TIANWEN_FITS_BENCH_PARALLEL") is { Length: > 0 } p
                && int.TryParse(p, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : 8;

            var files = Directory.EnumerateFiles(dir, "*.fits", SearchOption.AllDirectories)
                .Concat(Directory.EnumerateFiles(dir, "*.fit", SearchOption.AllDirectories))
                .OrderBy(static f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();
            files.Count.ShouldBeGreaterThan(0, "no FITS under TIANWEN_FITS_BENCH");

            var wasEnabled = Array2DPool<float>.Enabled;
            Array2DPool<float>.Enabled = true;
            try
            {
                var totalBytes = files.Sum(static f => new FileInfo(f).Length);
                output.WriteLine($"{files.Count} frames, {totalBytes / 1024.0 / 1024.0:F0} MiB on disk, {parallel} workers");

                // Warm over EVERY file, not a sample: whichever mode runs first would otherwise pay
                // the cold-disk cost and warm the OS file cache for the other, which on a first cut
                // of this made the second pass look ~9x faster on wall clock alone.
                await RunAsync(files, pooled: false, parallel, ct);

                // Measure unpooled on both sides of the pooled pass. If the two agree, ordering is
                // not carrying the result; if they do not, the wall-clock column is not evidence.
                var unpooled = await RunAsync(files, pooled: false, parallel, ct);
                var pooledRun = await RunAsync(files, pooled: true, parallel, ct);
                var unpooledAgain = await RunAsync(files, pooled: false, parallel, ct);

                foreach (var m in new[] { unpooled, pooledRun, unpooledAgain })
                {
                    output.WriteLine(
                        $"{m.Mode,-11} {m.Frames,5} frames ({m.Failures} failed)  {m.WallSeconds,6:F2} s  " +
                        $"alloc {m.AllocatedBytes / 1024.0 / 1024.0,9:F0} MiB  " +
                        $"gen0/1/2 {m.Gen0,4}/{m.Gen1,4}/{m.Gen2,3}  peak WS {m.PeakWorkingSetBytes / 1024.0 / 1024.0,7:F0} MiB");
                }

                var unpooledWall = Math.Min(unpooled.WallSeconds, unpooledAgain.WallSeconds);
                var spread = Math.Abs(unpooled.WallSeconds - unpooledAgain.WallSeconds)
                    / Math.Max(0.001, Math.Max(unpooled.WallSeconds, unpooledAgain.WallSeconds));
                output.WriteLine($"unpooled run-to-run spread {100 * spread:F0} % " +
                    (spread > 0.25 ? "-- too noisy to read the wall-clock column" : "-- wall clock is comparable"));

                var saved = unpooled.AllocatedBytes - pooledRun.AllocatedBytes;
                output.WriteLine(
                    $"pooled saves {saved / 1024.0 / 1024.0:F0} MiB of allocation " +
                    $"({100.0 * saved / Math.Max(1, unpooled.AllocatedBytes):F1} %), " +
                    $"gen2 {unpooled.Gen2} -> {pooledRun.Gen2}, " +
                    $"wall {unpooledWall:F2} -> {pooledRun.WallSeconds:F2} s (best unpooled of two)");
                output.WriteLine(
                    $"pool: {Array2DPool<float>.HitCount} hits, {Array2DPool<float>.MissCount} misses, " +
                    $"{Array2DPool<float>.ReturnCount} returns, {Array2DPool<float>.TotalPooled} retained");

                // The point of the change: the pooled pass must not allocate MORE. Timings are
                // reported but never asserted -- they are a property of the machine, not the code.
                pooledRun.Failures.ShouldBe(unpooled.Failures);
                unpooledAgain.Failures.ShouldBe(unpooled.Failures);
                pooledRun.AllocatedBytes.ShouldBeLessThan(unpooled.AllocatedBytes);
            }
            finally
            {
                Array2DPool<float>.Enabled = wasEnabled;
            }
        }

        private static async Task<Measurement> RunAsync(List<string> files, bool pooled, int parallel, CancellationToken ct)
        {
            // Settle before sampling so the previous pass's garbage is not billed to this one.
            GC.Collect(2, GCCollectionMode.Forced, blocking: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(2, GCCollectionMode.Forced, blocking: true);

            var proc = Process.GetCurrentProcess();
            proc.Refresh();
            var (g0, g1, g2) = (GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2));
            var alloc0 = GC.GetTotalAllocatedBytes(precise: false);
            var failures = 0;
            var sw = Stopwatch.StartNew();

            await Parallel.ForEachAsync(
                files,
                new ParallelOptions { MaxDegreeOfParallelism = parallel, CancellationToken = ct },
                (path, token) =>
                {
                    try
                    {
                        if (Image.TryReadFitsFile(path, out var image, out var wcs, pooled))
                        {
                            // Touch a pixel so the read cannot be optimised away, then hand the
                            // arrays back. Release is a no-op in the unpooled mode by design.
                            var probe = image.GetChannelSpan(0)[0] + (wcs is null ? 0f : 1f);
                            GC.KeepAlive(probe);
                            image.Release();
                        }
                        else
                        {
                            Interlocked.Increment(ref failures);
                        }
                    }
                    catch (Exception)
                    {
                        Interlocked.Increment(ref failures);
                    }
                    return ValueTask.CompletedTask;
                });

            sw.Stop();
            var allocated = GC.GetTotalAllocatedBytes(precise: false) - alloc0;
            proc.Refresh();
            return new Measurement(
                pooled ? "pooled" : "unpooled", files.Count, failures, sw.Elapsed.TotalSeconds,
                allocated, GC.CollectionCount(0) - g0, GC.CollectionCount(1) - g1, GC.CollectionCount(2) - g2,
                proc.PeakWorkingSet64);
        }
    }
}
