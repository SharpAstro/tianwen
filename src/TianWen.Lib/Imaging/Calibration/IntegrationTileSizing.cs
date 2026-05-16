using System;

namespace TianWen.Lib.Imaging.Calibration;

/// <summary>
/// Shared tile-sizing math for the tile-pipelined / staged strategies.
/// PLAN-stacking.md:107-124 sketches the formula; this is its single source
/// of truth so all three tile-based strategies pick the same tile size for a
/// given probe and the log lines stay consistent.
/// </summary>
internal static class IntegrationTileSizing
{
    /// <summary>Floor on the tile side. Below this, per-tile overhead
    /// (boundary halos, dispatch costs) dominates compute time and the
    /// strategy is effectively non-viable -- caller should reject.</summary>
    public const int MinTileSide = 64;

    /// <summary>Ceiling on the tile side. Larger tiles push the rejection
    /// inner loop's masked-stats span out of L2 cache; SetiAstro's
    /// <c>compute_safe_chunk</c> uses the same 2048 cap.</summary>
    public const int MaxTileSide = 2048;

    /// <summary>Safety multiplier on the predicted tile column footprint to
    /// leave room for source halos, calibration masters in memory, and the
    /// rejector's scratch buffers.</summary>
    public const int SafetyMargin = 4;

    /// <summary>
    /// Largest square tile side (pixels) whose <c>tile_side^2 * N * C * 4</c>
    /// column fits in <paramref name="ramBudget"/>, with the standard safety
    /// margin applied. Returns <c>-1</c> if the <see cref="MinTileSide"/>
    /// floor doesn't fit -- caller should treat the strategy as un-runnable.
    /// </summary>
    public static int Side(IntegrationProbe probe, long ramBudget)
    {
        var perPixelBytes = (long)probe.FrameCount * probe.ChannelCount * sizeof(float);
        if (perPixelBytes <= 0 || ramBudget <= 0) return -1;
        var pixelsPerTile = ramBudget / (perPixelBytes * SafetyMargin);
        if (pixelsPerTile <= 0) return -1;
        var side = (int)Math.Sqrt(pixelsPerTile);
        if (side < MinTileSide) return -1;
        return Math.Min(side, MaxTileSide);
    }

    /// <summary>RAM bytes a tile column actually consumes once stack +
    /// masters + halos are counted in.</summary>
    public static long TileRamBytes(int side, IntegrationProbe probe)
        => (long)side * side * probe.FrameCount * probe.ChannelCount * sizeof(float) * SafetyMargin;
}
