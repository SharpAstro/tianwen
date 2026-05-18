using System;
using System.IO;

namespace TianWen.Lib.Imaging.Calibration;

/// <summary>
/// Backing choice for an integrator's master canvas. Surfaced on
/// <see cref="IntegrationStrategySelector.Selection"/> so callers can
/// see what the selector picked and react (force-overrides for tests,
/// log lines for ops, MEF mode switching when Phase 10's full sink
/// matures).
/// </summary>
public enum SinkKind
{
    /// <summary>Managed <c>float[][,]</c> backing (<see cref="ArraySink"/>).
    /// Today's default. Suitable when the master canvas is small enough
    /// that the integrator + scratch + per-strategy working set all fit
    /// comfortably in available RAM.</summary>
    InRamArray = 0,

    /// <summary>Memory-mapped scratch file backing
    /// (<see cref="MemoryMappedFitsSink"/>). The canvas lives in the
    /// page cache rather than the GC heap, so the integrator's resident
    /// set doesn't grow with canvas size. Picked automatically when the
    /// canvas alone would consume a large fraction of available RAM
    /// (the threshold lives in <see cref="IntegrationStrategySelector"/>),
    /// or via user override for the byte-equivalence verification path.</summary>
    MemoryMappedFits = 1,
}

/// <summary>
/// Builds a sink-construction delegate from a <see cref="SinkKind"/> the
/// selector decided. Lives separately from the enum so the
/// <see cref="MemoryMappedFitsSink"/> scratch-file path (which the enum
/// itself shouldn't know about) stays in caller-side land.
/// </summary>
public static class SinkFactories
{
    /// <summary>Returns a <c>(channels, width, height) -&gt; IIntegrationSink</c>
    /// delegate suitable for assigning to <see cref="IntegrationJob.MasterSinkFactory"/>.
    /// </summary>
    /// <param name="kind">Sink choice (typically from
    /// <see cref="IntegrationStrategySelector.Selection.Sink"/>).</param>
    /// <param name="scratchDir">Directory the MMF sink will create scratch
    /// files in. Ignored for <see cref="SinkKind.InRamArray"/>. The sink
    /// owns the scratch file lifecycle (creates + deletes on dispose).</param>
    public static Func<int, int, int, IIntegrationSink> Create(SinkKind kind, string scratchDir) => kind switch
    {
        SinkKind.InRamArray => (c, w, h) => new ArraySink(c, w, h),
        SinkKind.MemoryMappedFits => (c, w, h) =>
        {
            Directory.CreateDirectory(scratchDir);
            var path = Path.Combine(scratchDir, $"mmf-canvas-{Guid.NewGuid():N}.bin");
            return new MemoryMappedFitsSink(path, c, w, h);
        },
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, $"Unknown sink kind"),
    };
}
