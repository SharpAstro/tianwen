using System;
using System.Collections.Immutable;
using DIR.Lib;
using TianWen.Lib.Devices.Guider;
using TianWen.Lib.Sequencing;

namespace TianWen.UI.Abstractions;

/// <summary>
/// Shared guide error graph data. PHD2-style connected lines for RA (blue) and Dec (orange),
/// dynamic Y scale, scrolling window, grid lines at integer arcsec.
/// Used by both <see cref="LiveSessionTab{TSurface}"/> (compact) and <see cref="GuiderTab{TSurface}"/> (full).
/// Call from within a <see cref="PixelWidgetBase{TSurface}"/> subclass which has access to FillRect/DrawText.
/// </summary>
public static class GuideGraphRenderer
{
    public static readonly RGBAColor32 GraphBg = new RGBAColor32(0x12, 0x12, 0x1a, 0xff);
    public static readonly RGBAColor32 GridColor = new RGBAColor32(0x33, 0x33, 0x44, 0xff);
    public static readonly RGBAColor32 ZeroLineColor = new RGBAColor32(0x55, 0x55, 0x66, 0xff);
    public static readonly RGBAColor32 RaColor = new RGBAColor32(0x44, 0x88, 0xff, 0xff);
    public static readonly RGBAColor32 DecColor = new RGBAColor32(0xff, 0x88, 0x44, 0xff);
    public static readonly RGBAColor32 DitherMarkerColor = new RGBAColor32(0xff, 0xff, 0x44, 0x88);
    public static readonly RGBAColor32 SettlingShadeColor = new RGBAColor32(0x44, 0x44, 0x00, 0x30);
    public static readonly RGBAColor32 RaCorrectionColor = new RGBAColor32(0x44, 0x88, 0xff, 0x55);
    public static readonly RGBAColor32 DecCorrectionColor = new RGBAColor32(0xff, 0x88, 0x44, 0x55);

    /// <summary>
    /// Computes the bar height fraction [0, 1] for a correction pulse using log scale.
    /// A 1ms correction → ~0.15, 10ms → ~0.45, 100ms → ~0.75, 1000ms → ~1.0.
    /// Log scale ensures even small corrections (2-5ms) are visible.
    /// </summary>
    public static float CorrectionBarFraction(double pulseMs)
    {
        if (pulseMs == 0) return 0;
        // log10(1)=0, log10(1000)=3 → normalize to [0, 1] over range [1ms, 1000ms]
        var logVal = Math.Log10(Math.Max(Math.Abs(pulseMs), 1.0));
        return (float)Math.Clamp(logVal / 3.0, 0.05, 1.0); // minimum 5% so tiny corrections are visible
    }

    /// <summary>
    /// Computes the Y scale from the visible window samples, ignoring settling spikes.
    /// Falls back to guide stats if no samples are visible.
    /// </summary>
    public static double ComputeYScale(GuideStats? stats, ImmutableArray<GuideErrorSample> samples, int startIdx, int visibleCount)
    {
        var peakErr = 0.5;

        // Compute peak from visible window, skipping settling samples (dither spikes)
        for (var i = startIdx; i < startIdx + visibleCount; i++)
        {
            var s = samples[i];
            if (s.IsSettling) continue; // don't let settling spikes blow out the scale
            peakErr = Math.Max(peakErr, Math.Max(Math.Abs(s.RaError), Math.Abs(s.DecError)));
        }

        // If all visible samples were settling, fall back to stats
        if (peakErr <= 0.5 && stats is { } gs)
        {
            // Use RMS * 4 as a reasonable peak estimate (not the all-time peak which includes dither spikes)
            peakErr = Math.Max(gs.RaRMS, gs.DecRMS) * 4;
        }

        return peakErr < 0.3 ? 0.5
            : peakErr < 0.7 ? 1.0
            : peakErr < 1.5 ? 2.0
            : peakErr < 3.0 ? 4.0
            : peakErr < 6.0 ? 8.0
            : 12.0;
    }

    /// <summary>
    /// Computes the Y scale from guide stats (legacy overload for compact graphs without sample access).
    /// </summary>
    public static double ComputeYScale(GuideStats? stats)
    {
        var peakErr = 1.0;
        if (stats is { } gs)
        {
            // Use RMS * 4 as a reasonable peak — avoids dither spikes locking the scale
            peakErr = Math.Max(gs.RaRMS, gs.DecRMS) * 4;
            peakErr = Math.Max(peakErr, 0.5);
        }
        return peakErr < 0.3 ? 0.5
            : peakErr < 0.7 ? 1.0
            : peakErr < 1.5 ? 2.0
            : peakErr < 3.0 ? 4.0
            : peakErr < 6.0 ? 8.0
            : 12.0;
    }

    /// <summary>
    /// Computes the scrolling window parameters.
    /// </summary>
    public static (int StartIdx, int VisibleCount, float SampleSpacing) ComputeWindow(
        int sampleCount, float width, float dpiScale)
    {
        var sampleSpacing = Math.Max(5f * dpiScale, 6f);
        var maxVisible = (int)(width / sampleSpacing);
        var startIdx = Math.Max(0, sampleCount - maxVisible);
        var visibleCount = sampleCount - startIdx;
        return (startIdx, visibleCount, sampleSpacing);
    }

    /// <summary>
    /// Converts an error value to a Y pixel coordinate.
    /// </summary>
    public static float ErrorToY(double error, double yScale, float zeroY, float halfH)
    {
        var clamped = Math.Clamp(error, -yScale, yScale);
        return zeroY - (float)(clamped / yScale * halfH);
    }
}
