using System;
using System.IO;
using TianWen.Lib.Imaging;

namespace TianWen.Lib.Imaging.Stacking;

/// <summary>
/// Coarse characterisation of the staging drive. Spindle disks need ~80x
/// the seek penalty of NVMe and ~5x the throughput penalty -- skipping that
/// distinction wrecks the ranker on machines that still have an HDD.
/// </summary>
/// <remarks>
/// We don't currently auto-detect this. Windows can read
/// <c>MSFT_PhysicalDisk.MediaType</c> via CIM, Linux has
/// <c>/sys/block/&lt;dev&gt;/queue/rotational</c>, macOS has
/// <c>diskutil info</c> -- all need platform-specific code we haven't
/// written. v1 accepts the orchestrator's best guess (default
/// <see cref="Ssd"/>); a future <c>IDiskProbe</c> service can fill this
/// in transparently.
/// </remarks>
public enum DiskKind
{
    /// <summary>Type unknown; the cost model uses the SSD profile as a
    /// safe-ish default but accuracy is reduced.</summary>
    Unknown,

    /// <summary>NVMe over PCIe. Sequential ~2 GB/s, seek &lt; 0.05 ms.</summary>
    Nvme,

    /// <summary>SATA SSD. Sequential ~500 MB/s, seek ~ 0.1 ms.</summary>
    Ssd,

    /// <summary>Spinning rust. Sequential ~120 MB/s, seek ~ 8 ms -- staging
    /// strategies pay heavily here.</summary>
    Hdd,
}

/// <summary>
/// Snapshot of an integration job + host at strategy-pick time. Carries the
/// information every <see cref="IIntegrationStrategy"/> needs to decide whether
/// it can run and how it estimates the resource bill. One probe -> one
/// <see cref="IntegrationStrategySelector.Pick"/> call -> one chosen strategy.
/// </summary>
/// <param name="FrameCount">Number of registered light frames feeding the
/// integration.</param>
/// <param name="FrameWidth">Pixel width of each raw light frame.</param>
/// <param name="FrameHeight">Pixel height of each raw light frame.</param>
/// <param name="ChannelCount">Channels per frame (1 mono, 3 RGB).</param>
/// <param name="CanvasWidth">Pixel width of the union-BB output canvas.</param>
/// <param name="CanvasHeight">Pixel height of the union-BB output canvas.</param>
/// <param name="AvailableRamBytes">Total RAM budget the process can claim
/// (bytes). Use the GC's <c>TotalAvailableMemoryBytes</c> directly -- on
/// .NET this defaults to the physical RAM the process is allowed to grow
/// into, not the "currently free" number. The <see cref="ResourceBudget"/>
/// safety factor (default 75%) brings it down to a working budget, so the
/// final cap is "physical RAM the strategy is permitted to use" rather
/// than "free RAM right now, after the GC's transient working set."
/// The "free right now" number was too pessimistic -- a fresh dotnet test
/// host reported 1.2 GB on a 64 GB machine just because the GC heap hadn't
/// grown yet, and that gated out InRam for stacks the box could trivially
/// fit.</param>
/// <param name="FreeRamBytes">Currently-free RAM (bytes) -- the OS's
/// "available" number after accounting for active working sets. Used as a
/// soft signal in the ranker: a RAM-heavy strategy still gets a CanRun=true
/// gate so long as its estimate fits in <see cref="AvailableRamBytes"/>,
/// but if its estimate also exceeds <see cref="FreeRamBytes"/> the ranker
/// applies a memory-pressure penalty so a lighter staged strategy can take
/// the win. This means InRam wins on dedicated stack runs (lots of free RAM)
/// and Footprint/Float16-staged take over when the user is mid-other-work
/// (RAM is committed elsewhere).</param>
/// <param name="AvailableDiskBytes">Free space on <paramref name="StagingDir"/>'s drive (bytes).</param>
/// <param name="StagingDir">Where staged frames would be written. Drive
/// identity drives the disk-throughput model.</param>
/// <param name="StagingDiskKind">Coarse type of the staging drive (NVMe / SSD /
/// HDD). Cost model multiplies seek + throughput accordingly.</param>
/// <param name="EmitRejectionMap">Whether the output buffer includes a
/// rejection map (doubles peak output RAM).</param>
/// <param name="LiveStacking">True when the caller wants frame-at-a-time
/// incremental accumulation (Welford online; PLAN-stacking Phase 14).
/// Filters the strategy set down to live-capable implementations.</param>
public sealed record IntegrationProbe(
    int FrameCount,
    int FrameWidth,
    int FrameHeight,
    int ChannelCount,
    int CanvasWidth,
    int CanvasHeight,
    long AvailableRamBytes,
    long AvailableDiskBytes,
    string StagingDir,
    // Sensor type of the reference frame, authoritatively the group's
    // <see cref="LightGroupKey.CalibrationKey"/>.SensorType. The scanner
    // pins this at scan-time from per-frame FITS headers; it's invariant
    // within a group (frames with different SensorType end up in
    // different groups by construction). Required (no default) so a
    // caller that doesn't know the sensor type has to make an explicit
    // choice -- <see cref="Imaging.SensorType.Monochrome"/> for the safe
    // "no Bayer matrix" assumption, matching the Image / ImageMeta
    // convention. Drizzle strategies key CanRun off this; only
    // <see cref="Imaging.SensorType.RGGB"/> exposes the Bayer-encoded
    // source plane the forward-projection kernel dispatches from.
    SensorType SensorType,
    DiskKind StagingDiskKind = DiskKind.Unknown,
    bool EmitRejectionMap = true,
    bool LiveStacking = false,
    long FreeRamBytes = 0)
{
    /// <summary>Bytes one frame occupies as float32 in RAM.</summary>
    public long FrameBytes => (long)FrameWidth * FrameHeight * ChannelCount * sizeof(float);

    /// <summary>Bytes the output canvas occupies as float32 in RAM (single image, no rejection map).</summary>
    public long CanvasBytes => (long)CanvasWidth * CanvasHeight * ChannelCount * sizeof(float);

    /// <summary>Peak output RAM: master + (optional) rejection map.</summary>
    public long OutputRamBytes => CanvasBytes * (EmitRejectionMap ? 2 : 1);

    /// <summary>Bytes for all frames in RAM simultaneously (InRamAllFrames).</summary>
    public long AllFramesRamBytes => FrameBytes * FrameCount;

    /// <summary>
    /// Snapshot the live host. Reads <see cref="GC.GetGCMemoryInfo()"/>'s
    /// <c>TotalAvailableMemoryBytes</c> for RAM ("how big can the process
    /// grow", which is physical RAM minus OS / container reserves) and
    /// <see cref="DriveInfo"/> for free disk on the staging drive. The old
    /// snapshot subtracted <c>MemoryLoadBytes</c> ("what the GC is already
    /// holding") which made the budget swing wildly with transient working
    /// set -- a fresh dotnet host with 64 GB physical reported a 1.2 GB
    /// "free" gate and rejected InRam stacks the machine could trivially
    /// fit. The safety factor in <see cref="ResourceBudget"/> (default 75%)
    /// is the sole mechanism that takes the budget below physical.
    /// <paramref name="stagingDiskKind"/> defaults to
    /// <see cref="DiskKind.Unknown"/> -- pass the orchestrator's best guess
    /// (typically <see cref="DiskKind.Ssd"/>) until auto-detection is wired.
    /// </summary>
    public static IntegrationProbe Snapshot(
        int frameCount,
        int frameWidth,
        int frameHeight,
        int channelCount,
        int canvasWidth,
        int canvasHeight,
        string stagingDir,
        SensorType sensorType,
        DiskKind stagingDiskKind = DiskKind.Unknown,
        bool emitRejectionMap = true,
        bool liveStacking = false)
    {
        var info = GC.GetGCMemoryInfo();
        var physicalRam = info.TotalAvailableMemoryBytes;
        var currentlyFree = Math.Max(0, info.TotalAvailableMemoryBytes - info.MemoryLoadBytes);
        var root = Path.GetPathRoot(Path.GetFullPath(stagingDir));
        var freeDisk = string.IsNullOrEmpty(root)
            ? 0L
            : new DriveInfo(root).AvailableFreeSpace;
        return new IntegrationProbe(
            FrameCount: frameCount,
            FrameWidth: frameWidth,
            FrameHeight: frameHeight,
            ChannelCount: channelCount,
            CanvasWidth: canvasWidth,
            CanvasHeight: canvasHeight,
            AvailableRamBytes: physicalRam,
            AvailableDiskBytes: freeDisk,
            StagingDir: stagingDir,
            StagingDiskKind: stagingDiskKind,
            EmitRejectionMap: emitRejectionMap,
            LiveStacking: liveStacking,
            FreeRamBytes: currentlyFree,
            SensorType: sensorType);
    }
}

/// <summary>
/// Safety factors on the raw <see cref="IntegrationProbe.AvailableRamBytes"/> /
/// <see cref="IntegrationProbe.AvailableDiskBytes"/> budgets. Leaves headroom
/// for the framework, masters, OS page cache, and other in-process state.
/// Defaults to 75% / 80%: the RAM number is "physical RAM the process is
/// permitted to grow into" so the safety factor takes us down to a working
/// budget. Disk is "free space the OS just reported", same idea. Both can be
/// cranked higher when the caller knows the host is dedicated to stacking
/// and nothing else (CI / batch reprocess), or lower for interactive use
/// where the user wants the GUI to stay responsive during a stack.
/// </summary>
public sealed record ResourceBudget(double RamSafetyFactor = 0.75, double DiskSafetyFactor = 0.80)
{
    public long AllowedRam(IntegrationProbe p) => (long)(p.AvailableRamBytes * RamSafetyFactor);
    public long AllowedDisk(IntegrationProbe p) => (long)(p.AvailableDiskBytes * DiskSafetyFactor);
}

/// <summary>
/// First-order cost coefficients each <see cref="IIntegrationStrategy.Evaluate"/>
/// uses to convert (frame count, canvas size, disk bytes, seek count) into a
/// wall-clock estimate. The defaults are guesstimates calibrated against the
/// Liberty 120s run (13 frames, 3024^2 RGB, ~98 s end-to-end). They will be
/// wrong; the ranking they produce is still useful as a tiebreaker, and a
/// future calibration pass (warp one tile, time it, persist to disk) can
/// replace the constants per host.
/// </summary>
public sealed record IntegrationCostModel
{
    /// <summary>Per-pixel CPU cost of the warp + bilinear sample (ns).</summary>
    // TODO: calibrate against measured runs; this is a Liberty-120s extrapolation.
    public double CpuNsPerWarpPixel { get; init; } = 80.0;

    /// <summary>Per-output-pixel-per-frame CPU cost of reject + combine (ns).
    /// Recalibrated 2026-05-17 from the Liberty SoL 60s spill run:
    /// StreamingIntegrator pass-2 (sigma-clip + mean over 242 frames) took ~309 s
    /// for a 3179x3159x3 canvas, i.e. 309e9 ns / (3179*3159*3*242) ≈ 42 ns/pixel.
    /// The previous 8 ns/px figure was measured on a mean-only inline integrator
    /// without rejection -- a 5x under-count for the real reject+combine work.</summary>
    public double CpuNsPerStackPixelPerFrame { get; init; } = 40.0;

    /// <summary>Per-source-pixel CPU cost of pre-debayer per-frame load + calibrate
    /// (read FITS, apply bias/dark/flat, copy into channel buffer) in ns.
    /// Calibrated 2026-05-17 from the Liberty SoL 60s run: Load 438 ms + Calibrate
    /// 322 ms = 760 ms per 3008^2 source pixel frame ≈ 84 ns/px under contended
    /// load. The clean-room figure is closer to 30 ns/px; default sits in between
    /// as a typical-case estimate. Missing this stage was the single biggest
    /// observed gap (~200 s under-prediction on a 242-frame stack).</summary>
    public double CpuNsPerLoadCalibratePixel { get; init; } = 50.0;

    /// <summary>Per-source-pixel CPU cost of debayer (ns). Calibrated 2026-05-16
    /// from the Liberty SoL 60s run: 547 ms / frame / (3008*3008) ≈ 60 ns/pixel
    /// for AHD on the 12-core Snapdragon X. Dominates every other phase by a
    /// factor of ~10 -- this constant is the single biggest determinant of
    /// pipeline wall time on raw FITS lights.</summary>
    public double CpuNsPerDebayerPixel { get; init; } = 60.0;

    /// <summary>Per-source-pixel CPU cost of drizzle forward-projection (ns).
    /// Each Bayer sample touches up to 4 output cells at pixfrac=1; the inner
    /// loop is 4 multiplies + 4 adds + 4 stores per cell, ~30 ns per source
    /// pixel measured empirically from earlier SoL drizzle runs. This is
    /// what the drizzle family pays instead of <see cref="CpuNsPerDebayerPixel"/>
    /// + <see cref="CpuNsPerWarpPixel"/> + <see cref="CpuNsPerStackPixelPerFrame"/>;
    /// the net is ~3-5x speedup vs the standard path on RGGB inputs, which
    /// is what makes drizzle competitive enough to auto-select on big-N
    /// sessions despite its 0.92 vs 0.98 fidelity discount.</summary>
    public double CpuNsPerDrizzleProjectPixel { get; init; } = 30.0;

    /// <summary>Float16 unpack overhead per pixel on read (ns). Cheap thanks to
    /// hardware f16->f32 paths, but not zero.</summary>
    public double CpuNsPerFloat16Unpack { get; init; } = 1.0;

    /// <summary>Sequential throughput (MB/s) for the given drive class.
    /// Overridable so a future <c>IDiskProbe</c> or per-host calibration
    /// can feed measured numbers.</summary>
    public double DiskMBPerSec(DiskKind kind) => kind switch
    {
        DiskKind.Nvme    => 2000.0,
        DiskKind.Ssd     =>  500.0,
        DiskKind.Hdd     =>  120.0,
        DiskKind.Unknown =>  500.0, // SSD-ish guess; flag it in the rationale.
        _ => 500.0,
    };

    /// <summary>Per-seek penalty (ms) for the given drive class.</summary>
    public double DiskSeekMs(DiskKind kind) => kind switch
    {
        DiskKind.Nvme    => 0.05,
        DiskKind.Ssd     => 0.10,
        DiskKind.Hdd     => 8.00,
        DiskKind.Unknown => 0.10,
        _ => 0.10,
    };

    /// <summary>Total CPU time to load + calibrate all N source frames once
    /// (read FITS, apply bias/dark/flat, write to channel buffer). Counts as
    /// per-source-pixel × frameCount; the work happens before debayer and is
    /// part of the producer flow every strategy iterates.</summary>
    public TimeSpan LoadAndCalibrateAllFrames(IntegrationProbe p)
    {
        var pixels = (double)p.FrameWidth * p.FrameHeight * p.FrameCount;
        return TimeSpan.FromMilliseconds(pixels * CpuNsPerLoadCalibratePixel / 1e6);
    }

    /// <summary>Total CPU time to debayer all N source frames once (ns ×
    /// source-pixels × N / 1e6 ms). Mosaic-CFA frames only -- mono frames
    /// pay zero debayer.</summary>
    public TimeSpan DebayerAllFrames(IntegrationProbe p)
    {
        // ChannelCount=1 is mono (no debayer); only CFA frames pay.
        if (p.ChannelCount == 1) return TimeSpan.Zero;
        var pixels = (double)p.FrameWidth * p.FrameHeight * p.FrameCount;
        return TimeSpan.FromMilliseconds(pixels * CpuNsPerDebayerPixel / 1e6);
    }

    /// <summary>Total CPU time to warp all N frames into the canvas (one pass).</summary>
    public TimeSpan WarpAllFrames(IntegrationProbe p)
    {
        var pixels = (double)p.FrameWidth * p.FrameHeight * p.ChannelCount * p.FrameCount;
        return TimeSpan.FromMilliseconds(pixels * CpuNsPerWarpPixel / 1e6);
    }

    /// <summary>Total CPU time to stack N frames into the canvas (one pass).</summary>
    public TimeSpan StackAllFrames(IntegrationProbe p)
    {
        var pixels = (double)p.CanvasWidth * p.CanvasHeight * p.ChannelCount * p.FrameCount;
        return TimeSpan.FromMilliseconds(pixels * CpuNsPerStackPixelPerFrame / 1e6);
    }

    /// <summary>
    /// Approximate per-frame cache-miss rate for a strategy that holds at most
    /// <paramref name="cacheRamBudget"/> bytes of debayered frames in RAM. When
    /// the working set of all frames fits the budget, hit rate is 1.0 and miss
    /// rate is 0.0; when it overflows, miss rate is the fraction that doesn't
    /// fit. Used by tile-pipelined strategies whose pass-2 strip integration
    /// pays a full re-decode + re-debayer on every cache miss.
    /// </summary>
    public double EstimateCacheMissRate(IntegrationProbe p, long cacheRamBudget)
    {
        var debayeredBytesPerFrame = (long)p.FrameWidth * p.FrameHeight * p.ChannelCount * sizeof(float);
        var workingSetBytes = debayeredBytesPerFrame * p.FrameCount;
        if (workingSetBytes <= cacheRamBudget) return 0.0;
        return 1.0 - (double)cacheRamBudget / workingSetBytes;
    }

    /// <summary>Wall-clock for moving <paramref name="bytes"/> through the
    /// staging drive with <paramref name="seeks"/> head moves on the given
    /// <paramref name="kind"/>.</summary>
    public TimeSpan DiskIo(long bytes, int seeks, DiskKind kind)
    {
        var bw = bytes / (DiskMBPerSec(kind) * 1024 * 1024) * 1000;
        var seekMs = seeks * DiskSeekMs(kind);
        return TimeSpan.FromMilliseconds(bw + seekMs);
    }
}

/// <summary>
/// How the selector blends fidelity vs estimated speed when ranking the
/// strategies that pass the gate. <see cref="FidelityFirst"/> matches the
/// pre-ranking-pass behaviour (quality only). <see cref="Balanced"/> is the
/// reasonable default. <see cref="SpeedFirst"/> is for batch reprocessing
/// where wall-clock dominates.
/// </summary>
public sealed record RankingPolicy(double FidelityWeight = 0.7, double SpeedWeight = 0.3)
{
    public static readonly RankingPolicy FidelityFirst = new(FidelityWeight: 1.0, SpeedWeight: 0.0);
    public static readonly RankingPolicy Balanced = new(FidelityWeight: 0.5, SpeedWeight: 0.5);
    public static readonly RankingPolicy SpeedFirst = new(FidelityWeight: 0.2, SpeedWeight: 0.8);

    /// <summary>
    /// Score = fidelity * w_f + normalized_speed * w_s. Higher is better.
    /// <paramref name="normalizedSpeed"/> must be in [0, 1] where 1 = fastest
    /// survivor and 0 = slowest survivor; caller does the normalisation.
    /// </summary>
    public double Score(double fidelity, double normalizedSpeed)
        => fidelity * FidelityWeight + normalizedSpeed * SpeedWeight;
}

/// <summary>
/// One strategy's verdict on a probe. <see cref="CanRun"/> gates inclusion;
/// the bytes + duration estimates feed the ranker; <see cref="Rationale"/>
/// is a single-line human-readable explanation for the log.
/// </summary>
public sealed record StrategyFit(
    bool CanRun,
    long EstimatedRamBytes,
    long EstimatedDiskBytes,
    TimeSpan EstimatedDuration,
    string Rationale)
{
    // Backing field as nullable so the property getter can fall back to
    // EstimatedRamBytes -- a positional record's primary constructor params
    // aren't visible to property initialisers, so we resolve at read time.
    private readonly long? _floorRamBytes;

    /// <summary>
    /// Minimum RAM the strategy needs to run at all -- separate from
    /// <see cref="EstimatedRamBytes"/> which reports the optimistic target.
    /// For non-adaptive strategies (the default), reading this returns
    /// <see cref="EstimatedRamBytes"/>: floor == target. Adaptive strategies
    /// (e.g. <see cref="TilePipelinedStrategy"/>, whose cache can shrink from
    /// "N x debayered frames" down to "1 in-flight + strip + output") set this
    /// explicitly so the selector's memory-pressure penalty doesn't over-
    /// penalise a target that the strategy can scale below at runtime.
    /// </summary>
    public long FloorRamBytes
    {
        get => _floorRamBytes ?? EstimatedRamBytes;
        init => _floorRamBytes = value;
    }
}
