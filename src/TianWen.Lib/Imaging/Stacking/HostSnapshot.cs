using System;
using System.Diagnostics;
using System.IO;
using Microsoft.Extensions.Logging;

namespace TianWen.Lib.Imaging.Stacking;

/// <summary>
/// Point-in-time host resource snapshot: process working-set + managed
/// heap size, free RAM the GC believes the process can still grow into,
/// and free disk on a caller-supplied drive. Cheap to take -- one
/// <see cref="Process.GetCurrentProcess"/>, one
/// <see cref="GC.GetGCMemoryInfo()"/>, one <see cref="DriveInfo"/>.
/// </summary>
internal readonly record struct HostSnapshot(
    long ProcessWorkingSetBytes,
    long ManagedHeapBytes,
    long FreeRamBytes,
    long FreeDiskBytes,
    long ElapsedMs)
{
    public static HostSnapshot Take(string diskPath, Stopwatch sw)
    {
        using var p = Process.GetCurrentProcess();
        var gc = GC.GetGCMemoryInfo();
        long freeDisk = 0;
        if (!string.IsNullOrEmpty(diskPath))
        {
            // DriveInfo throws on a vanished drive / share permission denial;
            // a missing free-disk reading should not abort the run.
            try
            {
                var root = Path.GetPathRoot(Path.GetFullPath(diskPath));
                if (!string.IsNullOrEmpty(root)) freeDisk = new DriveInfo(root).AvailableFreeSpace;
            }
            catch { /* best-effort: log 0 rather than crashing the run */ }
        }
        return new HostSnapshot(
            ProcessWorkingSetBytes: p.WorkingSet64,
            ManagedHeapBytes: GC.GetTotalMemory(forceFullCollection: false),
            // TotalAvailableMemoryBytes - MemoryLoadBytes: "what the process
            // can still grow into without paging". MemoryLoadBytes includes
            // the process itself plus everything else on the host.
            FreeRamBytes: Math.Max(0, gc.TotalAvailableMemoryBytes - gc.MemoryLoadBytes),
            FreeDiskBytes: freeDisk,
            ElapsedMs: sw.ElapsedMilliseconds);
    }
}

/// <summary>
/// Holds the run's baseline <see cref="HostSnapshot"/> + a single
/// <see cref="Stopwatch"/> and emits structured <c>[host]</c> log lines
/// with deltas vs. baseline at each milestone. One tracker per
/// <c>RunAsync</c> invocation; threaded through the per-group helpers so
/// every stage's line uses the same baseline + clock.
/// </summary>
internal sealed class HostSnapshotTracker
{
    private readonly Stopwatch _sw;
    private readonly HostSnapshot _baseline;
    private readonly string _diskPath;

    public HostSnapshotTracker(string diskPath)
    {
        _diskPath = diskPath;
        _sw = Stopwatch.StartNew();
        _baseline = HostSnapshot.Take(diskPath, _sw);
    }

    /// <summary>
    /// Emit one <c>[host]</c> line tagged with <paramref name="stage"/>.
    /// Format: <c>[host/stage] rss=1.2GB(+340MB) heap=820MB(+220MB)
    /// freeRam=8.4GB(-2.1GB) freeDisk=412GB(-1.2GB) @ +245s</c>.
    /// Deltas are computed against the tracker's construction-time
    /// baseline, not the previous log call, so the +/- numbers always
    /// answer "how much has the run consumed so far?".
    /// </summary>
    public void Log(ILogger logger, string stage)
    {
        var current = HostSnapshot.Take(_diskPath, _sw);
        logger.LogInformation(
            "[host/{Stage}] rss={Rss}({RssD}) heap={Heap}({HeapD}) freeRam={FreeRam}({FreeRamD}) freeDisk={FreeDisk}({FreeDiskD}) @ +{Seconds}s",
            stage,
            FormatBytes(current.ProcessWorkingSetBytes),
            FormatDelta(current.ProcessWorkingSetBytes - _baseline.ProcessWorkingSetBytes),
            FormatBytes(current.ManagedHeapBytes),
            FormatDelta(current.ManagedHeapBytes - _baseline.ManagedHeapBytes),
            FormatBytes(current.FreeRamBytes),
            FormatDelta(current.FreeRamBytes - _baseline.FreeRamBytes),
            FormatBytes(current.FreeDiskBytes),
            FormatDelta(current.FreeDiskBytes - _baseline.FreeDiskBytes),
            current.ElapsedMs / 1000);
    }

    /// <summary>
    /// Format an absolute byte count as a short human-readable string:
    /// <c>1.2GB</c>, <c>340MB</c>, <c>820KB</c>, <c>42B</c>. Picks the
    /// biggest unit where the magnitude is >= 1.
    /// </summary>
    internal static string FormatBytes(long bytes)
    {
        var abs = Math.Abs(bytes);
        if (abs >= 1L << 30) return $"{bytes / (double)(1L << 30):F1}GB";
        if (abs >= 1L << 20) return $"{bytes / (1L << 20)}MB";
        if (abs >= 1L << 10) return $"{bytes / (1L << 10)}KB";
        return $"{bytes}B";
    }

    /// <summary>Same as <see cref="FormatBytes"/> but always carries an explicit
    /// sign (<c>+</c> for non-negative, <c>-</c> for negative) so a delta is
    /// instantly distinguishable from an absolute number in the log line.</summary>
    internal static string FormatDelta(long bytes) => bytes >= 0
        ? "+" + FormatBytes(bytes)
        : "-" + FormatBytes(-bytes);
}
