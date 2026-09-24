using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Lib.Imaging.Calibration;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The header index on REAL capture software's headers: each folder in
/// <c>TIANWEN_HEADER_INDEX_PROBE</c> (separated by ';') is scanned twice, the second time from the
/// index, and every frame must come back identical. Reports how many headers replayed (were stored) and
/// what each scan cost, since a header that does not replay is read from its file every time.
/// </summary>
[Collection("Imaging")]
public sealed class FitsHeaderIndexProbe(ITestOutputHelper output)
{
    [Fact]
    public async Task RealHeadersReplayFromTheIndex()
    {
        var folders = Environment.GetEnvironmentVariable("TIANWEN_HEADER_INDEX_PROBE");
        Assert.SkipWhen(string.IsNullOrWhiteSpace(folders), "TIANWEN_HEADER_INDEX_PROBE not set");
        var ct = TestContext.Current.CancellationToken;
        var indexDir = Path.Combine(Path.GetTempPath(), "headerindex-probe-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            foreach (var folder in folders.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var file = FitsHeaderIndex.PathFor(indexDir, folder);
                var first = FitsHeaderIndex.Load(file);
                var read = Stopwatch.StartNew();
                var fromFiles = await new FitsFolderFrameSource(folder, recursive: true) { HeaderIndex = first }.EnumerateAsync(ct).ToListAsync(ct);
                read.Stop();
                first.Save();

                var second = FitsHeaderIndex.Load(file);
                var replay = Stopwatch.StartNew();
                var fromIndex = await new FitsFolderFrameSource(folder, recursive: true) { HeaderIndex = second }.EnumerateAsync(ct).ToListAsync(ct);
                replay.Stop();

                fromIndex.Count.ShouldBe(fromFiles.Count);
                for (var i = 0; i < fromFiles.Count; i++)
                {
                    FitsHeaderIndex.SameFrame(fromIndex[i], fromFiles[i]).ShouldBeTrue($"{fromFiles[i].Path} replayed differently");
                }

                var software = fromFiles.Select(f => f.Meta.Instrument).Distinct().Take(3);
                output.WriteLine(
                    $"{folder}: {fromFiles.Count} frames ({string.Join(", ", software)}); stored {second.Count}, " +
                    $"second scan {second.Hits} hits / {second.Misses} reads; {read.Elapsed.TotalMilliseconds:F0} ms read, " +
                    $"{replay.Elapsed.TotalMilliseconds:F0} ms from the index");
            }
        }
        finally
        {
            try { Directory.Delete(indexDir, recursive: true); } catch { /* best effort */ }
        }
    }
}
