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
    /// The measurement P1 of <c>docs/plans/frame-lifecycle.md</c> waits on: what "make
    /// <c>Calibrator.Apply</c> always copy" actually costs on the NO-MASTERS path, which is the path
    /// a first-time user with no calibration folder takes for every light.
    /// </summary>
    /// <remarks>
    /// <para><b>The two shapes, differing by exactly one term.</b> Today, with no masters,
    /// <c>Apply</c> returns its own input, so <c>RawLightDecoder</c> must PREDICT that and read
    /// unpooled -- the read's destination arrays become the retained frame. Under always-copy the
    /// read is unconditionally pooled, <c>Apply</c> copies into a fresh set, and the rented arrays go
    /// back. Both shapes therefore hand the caller exactly one freshly allocated frame; the delta is
    /// one full-frame memcpy plus the pool round-trip.</para>
    /// <para><b>Why that framing is the whole point.</b> The plan argued the copy would be "paid for
    /// by the large-object churn the pooled read then stops producing". That holds only if the read
    /// destination and the retained output are DIFFERENT arrays, which is true when a master is
    /// present (<c>Subtract</c>/<c>Divide</c> allocate their own destination and the raw dies at
    /// once) and false when none is, because there the read destination IS the output. This measures
    /// whether the argument survives contact.</para>
    /// <para>The copy term is the real one: <c>SubtractiveChromaticNoise(ScnrMode.None)</c> returns
    /// <c>new Image(CopyChannelData(), ...)</c> and nothing else, so it is precisely the duplication
    /// an always-copy <c>Apply</c> would perform, taken from shipped code rather than restated here.</para>
    /// <para>Env-gated and normally skipped, like its neighbours -- it needs real frames and it is a
    /// measurement, not an assertion about a machine's timings:
    /// <code>
    /// TIANWEN_FITS_BENCH=&lt;dir-of-fits&gt; TIANWEN_FITS_BENCH_REPEATS=20 \
    /// dotnet test TianWen.Lib.Tests -c Release --filter FullyQualifiedName~NoMastersCalibrationShapes
    /// </code></para>
    /// </remarks>
    [Collection("Imaging")]
    public class CalibrationOwnershipShapeBenchmark(ITestOutputHelper output)
    {
        private readonly record struct Shape(
            string Name, int Frames, int Failures, double WallSeconds,
            long AllocatedBytes, int Gen0, int Gen1, int Gen2);

        [Fact(Timeout = 30 * 60 * 1000)]
        public async Task NoMastersCalibrationShapes()
        {
            if (Environment.GetEnvironmentVariable("TIANWEN_FITS_BENCH") is not { Length: > 0 } dir || !Directory.Exists(dir))
            {
                Assert.Skip("Set TIANWEN_FITS_BENCH to a directory of FITS frames to price the always-copy shape.");
                return;
            }

            var ct = TestContext.Current.CancellationToken;
            var repeats = Environment.GetEnvironmentVariable("TIANWEN_FITS_BENCH_REPEATS") is { Length: > 0 } r
                && int.TryParse(r, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : 10;

            var found = Directory.EnumerateFiles(dir, "*.fits", SearchOption.AllDirectories)
                .Concat(Directory.EnumerateFiles(dir, "*.fit", SearchOption.AllDirectories))
                .OrderBy(static f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();
            found.Count.ShouldBeGreaterThan(0, "no FITS under TIANWEN_FITS_BENCH");

            var files = Enumerable.Range(0, repeats).SelectMany(_ => found).ToList();
            var bytes = found.Sum(static f => new FileInfo(f).Length);
            output.WriteLine($"{found.Count} distinct frames x {repeats} = {files.Count} reads, {bytes / 1024.0 / 1024.0:F0} MiB distinct on disk");

            var wasEnabled = Array2DPool<float>.Enabled;
            Array2DPool<float>.Enabled = true;
            try
            {
                // Warm over the whole set first: whichever shape ran first would otherwise pay the
                // cold-disk cost and warm the file cache for the other.
                await RunAsync(files, alwaysCopy: false, ct);

                // Today's shape on both sides of the candidate. If the two agree, ordering is not
                // carrying the result; if they do not, the wall-clock column is not evidence.
                var today = await RunAsync(files, alwaysCopy: false, ct);
                var copying = await RunAsync(files, alwaysCopy: true, ct);
                var todayAgain = await RunAsync(files, alwaysCopy: false, ct);

                foreach (var s in new[] { today, copying, todayAgain })
                {
                    output.WriteLine(
                        $"{s.Name,-22} {s.Frames,5} reads ({s.Failures} failed)  {s.WallSeconds,7:F2} s  " +
                        $"alloc {s.AllocatedBytes / 1024.0 / 1024.0,9:F0} MiB  gen0/1/2 {s.Gen0,4}/{s.Gen1,4}/{s.Gen2,3}");
                }

                var spread = Math.Abs(today.WallSeconds - todayAgain.WallSeconds)
                    / Math.Max(0.001, Math.Max(today.WallSeconds, todayAgain.WallSeconds));
                var baseline = Math.Min(today.WallSeconds, todayAgain.WallSeconds);
                output.WriteLine($"today run-to-run spread {100 * spread:F1} % " +
                    (spread > 0.10 ? "-- the wall-clock delta below must exceed this to mean anything" : "-- stable"));
                output.WriteLine(
                    $"always-copy costs {100.0 * (copying.WallSeconds - baseline) / baseline:+0.0;-0.0} % wall, " +
                    $"{(copying.AllocatedBytes - today.AllocatedBytes) / 1024.0 / 1024.0:+0;-0} MiB allocation " +
                    $"({100.0 * (copying.AllocatedBytes - today.AllocatedBytes) / Math.Max(1, today.AllocatedBytes):+0.0;-0.0} %), " +
                    $"gen2 {today.Gen2} -> {copying.Gen2}");
                output.WriteLine(
                    $"pool: {Array2DPool<float>.HitCount} hits, {Array2DPool<float>.MissCount} misses, " +
                    $"{Array2DPool<float>.ReturnCount} returns, {Array2DPool<float>.TotalPooled} retained");

                copying.Failures.ShouldBe(today.Failures);
                todayAgain.Failures.ShouldBe(today.Failures);

                // The wall-clock column above is read against a run-to-run spread it may not
                // exceed, because three passes over the same files ride whatever the OS file cache
                // is doing. The copy is the only term that differs, so price it on its own, off
                // disk: one frame loaded once, then N duplications of it.
                MeasureCopyInIsolation(found[^1], repeats, output, ct);
            }
            finally
            {
                Array2DPool<float>.Enabled = wasEnabled;
            }
        }

        /// <summary>
        /// Prices the one term the two shapes differ by, with the disk taken out of it: load a frame
        /// once, then duplicate it <paramref name="iterations"/> times. Allocation is reported beside
        /// the time because the copy's destination is the frame the caller keeps in BOTH shapes --
        /// today it is the read's destination, under always-copy it is this -- which is why the
        /// allocation delta upstairs is a wash and this is pure addition.
        /// </summary>
        private static void MeasureCopyInIsolation(string path, int iterations, ITestOutputHelper output, CancellationToken ct)
        {
            if (!Image.TryReadFitsFile(path, out var frame, out _))
            {
                output.WriteLine($"copy in isolation: could not read {Path.GetFileName(path)}");
                return;
            }

            var megaPixels = frame.Width / 1024.0 * frame.Height / 1024.0 * frame.ChannelCount;
            var frameMiB = megaPixels * sizeof(float);

            // One outside the timed loop so the first-touch page faults on a fresh LOH block are not
            // billed to the measurement as if they were the copy.
            GC.KeepAlive(frame.SubtractiveChromaticNoise(ScnrMode.None));
            GC.Collect(2, GCCollectionMode.Forced, blocking: true);
            GC.WaitForPendingFinalizers();

            var alloc0 = GC.GetTotalAllocatedBytes(precise: false);
            var sw = Stopwatch.StartNew();
            for (var i = 0; i < iterations; i++)
            {
                ct.ThrowIfCancellationRequested();
                GC.KeepAlive(frame.SubtractiveChromaticNoise(ScnrMode.None));
            }
            sw.Stop();
            var allocated = GC.GetTotalAllocatedBytes(precise: false) - alloc0;

            var perCopyMs = sw.Elapsed.TotalMilliseconds / iterations;
            output.WriteLine(
                $"copy in isolation: {frame.Width}x{frame.Height}x{frame.ChannelCount} ({frameMiB:F0} MiB), " +
                $"{iterations} copies in {sw.Elapsed.TotalSeconds:F2} s = {perCopyMs:F1} ms each " +
                $"({frameMiB / perCopyMs * 1000 / 1024:F1} GiB/s), " +
                $"{allocated / 1024.0 / 1024.0 / iterations:F0} MiB allocated per copy");
            output.WriteLine(
                $"  => always-copy adds {perCopyMs:F1} ms and {frameMiB:F0} MiB of copying per light, " +
                "and removes no allocation, because the destination is the frame the caller keeps either way.");
        }

        /// <summary>
        /// Sequential on purpose. The pool's recycling only shows up when a frame's arrays are back
        /// before the next rent, and eight workers racing one bucket measures the pool's contention
        /// rather than the shape being chosen.
        /// </summary>
        private static async Task<Shape> RunAsync(List<string> files, bool alwaysCopy, CancellationToken ct)
        {
            GC.Collect(2, GCCollectionMode.Forced, blocking: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(2, GCCollectionMode.Forced, blocking: true);

            var (g0, g1, g2) = (GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2));
            var alloc0 = GC.GetTotalAllocatedBytes(precise: false);
            var failures = 0;
            var sw = Stopwatch.StartNew();

            foreach (var path in files)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    if (!Image.TryReadFitsFile(path, out var raw, out _, pooled: alwaysCopy))
                    {
                        failures++;
                        continue;
                    }

                    Image calibrated;
                    if (alwaysCopy)
                    {
                        // What an always-copy Apply does with no masters, and then what it lets
                        // RawLightDecoder do unconditionally: hand the rented arrays straight back.
                        calibrated = raw.SubtractiveChromaticNoise(ScnrMode.None);
                        raw.Release();
                    }
                    else
                    {
                        // Today: Apply returns its own input, so the read had to be unpooled and
                        // there is nothing to release.
                        calibrated = raw;
                    }

                    // Touch a pixel so neither arm can be optimised away, and hold the frame the way
                    // the strategies' FrameCache does -- only until the next iteration, which is
                    // enough to keep the output out of the pool in BOTH arms.
                    GC.KeepAlive(calibrated.GetChannelSpan(0)[0]);
                }
                catch (Exception)
                {
                    failures++;
                }

                await Task.Yield();
            }

            sw.Stop();
            return new Shape(
                alwaysCopy ? "always-copy + pooled" : "today (identity)", files.Count, failures,
                sw.Elapsed.TotalSeconds, GC.GetTotalAllocatedBytes(precise: false) - alloc0,
                GC.CollectionCount(0) - g0, GC.CollectionCount(1) - g1, GC.CollectionCount(2) - g2);
        }
    }
}
