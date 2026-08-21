using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using TianWen.Lib.Imaging;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests
{
    /// <summary>
    /// Diagnostic probe: what does stepping to the next frame in a folder actually cost, split into
    /// file read, decode, and the stats pass, with the allocation each stage charges.
    /// </summary>
    /// <remarks>
    /// <para>Answers the question the hot/cold hydration design turns on: whether a step is bound by
    /// the DISK, by FITS.Lib's decode, or by allocating a fresh full-frame <c>float[,]</c> per frame.
    /// Those three want completely different fixes -- a mapping, a faster converter, and the pooled
    /// read that already exists -- and the plan for this file has twice been wrong from reasoning
    /// about it instead of measuring.</para>
    /// <para>Allocation, never working set: run-to-run variance on working set exceeds anything a step
    /// costs (established in M2 of <c>docs/plans/viewer-memory-footprint.md</c>).</para>
    /// <para>Point <c>TIANWEN_FRAME_STEP_PROBE</c> at a FOLDER of frames, not one file: stepping is a
    /// sequence, and the interesting number is the second pass over the same files, when the bytes are
    /// in the OS cache and only the decode and the allocation are left. A single file cannot show that
    /// difference at all.</para>
    /// </remarks>
    [Collection("Imaging")]
    public class FrameStepCostProbe(ITestOutputHelper output)
    {
        private const string EnvVar = "TIANWEN_FRAME_STEP_PROBE";

        [Fact]
        public async Task WhereTheStepTimeGoes()
        {
            var folder = Environment.GetEnvironmentVariable(EnvVar);
            Assert.SkipUnless(folder is { Length: > 0 } && Directory.Exists(folder),
                $"{EnvVar} not set to an existing folder");

            var ct = TestContext.Current.CancellationToken;
            var files = Directory.GetFiles(folder!, "*.fits");
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);
            Assert.SkipUnless(files.Length > 0, "no .fits in the folder");

            output.WriteLine($"{files.Length} frame(s) in {folder}");
            output.WriteLine("");

            // Pass 1 is whatever the OS cache happened to hold; pass 2 is warm by construction. The
            // DIFFERENCE is the disk's share, which is the only honest way to get it without
            // privileged cache-dropping.
            for (var pass = 1; pass <= 2; pass++)
            {
                output.WriteLine($"=== pass {pass} ({(pass == 1 ? "cache as found" : "warm")}) ===");
                foreach (var file in files)
                {
                    await MeasureAsync(file, ct);
                }
                output.WriteLine("");
            }
        }

        private async Task MeasureAsync(string path, System.Threading.CancellationToken ct)
        {
            var name = Path.GetFileName(path);
            var sizeMb = new FileInfo(path).Length / (1024.0 * 1024.0);

            // Unpooled read: what the viewer does today.
            var before = GC.GetAllocatedBytesForCurrentThread();
            var t = Stopwatch.StartNew();
            if (!Image.TryReadFitsFile(path, out var image, out _))
            {
                output.WriteLine($"{name,-56} UNREADABLE");
                return;
            }
            var readMs = t.Elapsed.TotalMilliseconds;
            var readAlloc = GC.GetAllocatedBytesForCurrentThread() - before;

            var (channels, w, h) = image.Shape;
            var mp = w * (double)h / 1e6;

            // The stats pass the document open performs, timed on its own.
            t.Restart();
            for (var c = 0; c < channels; c++) { _ = image.Statistics(c); }
            var statsMs = t.Elapsed.TotalMilliseconds;

            // Pooled read: the shipped path written for exactly this loop, which no production caller
            // uses. Released immediately, as a stepping viewer would when the frame goes cold.
            before = GC.GetAllocatedBytesForCurrentThread();
            t.Restart();
            Image.TryReadFitsFile(path, out var pooledImage, out _, pooled: true);
            var pooledMs = t.Elapsed.TotalMilliseconds;
            var pooledAlloc = GC.GetAllocatedBytesForCurrentThread() - before;
            pooledImage?.Release();

            // The whole thing the viewer actually does per step.
            before = GC.GetAllocatedBytesForCurrentThread();
            t.Restart();
            var document = await AstroImageDocument.OpenAsync(path, cancellationToken: ct);
            var openMs = t.Elapsed.TotalMilliseconds;
            var openAlloc = GC.GetAllocatedBytesForCurrentThread() - before;

            // The same pixels, rewritten by OUR writer, which states DATAMIN/DATAMAX. Reading that
            // back skips the vectorised min/max traversal the gate exists for, so the difference is
            // what those two cards are worth on the read -- and third-party subs carry neither.
            var rewritten = Path.Combine(Path.GetTempPath(), $"tw-rewrite-{Guid.NewGuid():N}.fits");
            double rewrittenMs;
            try
            {
                image.WriteToFitsFile(rewritten);
                Image.TryReadFitsFile(rewritten, out _, out _);   // warm the new file
                t.Restart();
                Image.TryReadFitsFile(rewritten, out _, out _);
                rewrittenMs = t.Elapsed.TotalMilliseconds;
            }
            finally
            {
                if (File.Exists(rewritten)) { File.Delete(rewritten); }
            }

            output.WriteLine($"{name,-56} {sizeMb,7:F1} MB  {w}x{h}x{channels} ({mp:F1} MP) {image.BitDepth}");
            output.WriteLine($"    read (unpooled) {readMs,8:F1} ms   alloc {readAlloc / 1048576.0,8:F1} MB");
            output.WriteLine($"    read (ours, DATAMIN/MAX stated) {rewrittenMs,8:F1} ms");
            output.WriteLine($"    read (pooled)   {pooledMs,8:F1} ms   alloc {pooledAlloc / 1048576.0,8:F1} MB");
            output.WriteLine($"    Statistics x{channels}     {statsMs,8:F1} ms");
            output.WriteLine($"    OpenAsync TOTAL {openMs,8:F1} ms   alloc {openAlloc / 1048576.0,8:F1} MB"
                + $"   (document is what a step must produce)");
            _ = document;
        }
    }
}
