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

    /// <summary>
    /// Computes the Y scale from guide stats.
    /// </summary>
    public static double ComputeYScale(GuideStats? stats)
    {
        var peakErr = 1.0;
        if (stats is { } gs)
        {
            peakErr = Math.Max(gs.PeakRa, gs.PeakDec);
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
        var sampleSpacing = Math.Max(2f * dpiScale, 3f);
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
