using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DIR.Lib;
using SharpAstro.Png;
using TianWen.Lib.Imaging.Planetary;

namespace TianWen.UI.Abstractions;

/// <summary>
/// Where <see cref="PlanetaryQualityReport.Render{TSurface}"/> drew: each panel's plot, the quality both plots' axes start at (their top is
/// the best frame's 100), and the keep's mark on the sorted curve.
/// </summary>
public readonly record struct PlanetaryQualityReportLayout(RectInt SortedPlot, RectInt RunPlot, float QualityFloor, float KeepX, float KeepY);

/// <summary>
/// A run's frame-quality curve as a picture (#1364, AutoStakkert's graph), read off <see cref="PlanetaryQualityCurve"/>: the curve sorted
/// best first, with the keep's cut marked and the reference cuts beside it, above the run's quality in time order (the 10th to 90th
/// percentile as a band, the median as a line, a segment a file, the keep's cut as a level). Frames the stack leaves out are in neither
/// and counted in the caption. Drawn with the renderer's primitives as <see cref="AltitudeChartRenderer"/> is, so it renders wherever
/// <c>tianwen</c> runs, and written as PNG, which the Claude web interface shows where it does not show SVG.
/// </summary>
public static class PlanetaryQualityReport
{
    /// <summary>The picture's width, px.</summary>
    public const uint Width = 1200;

    /// <summary>The picture's height, px.</summary>
    public const uint Height = 960;

    private static readonly RGBAColor32 Background = new RGBAColor32(0x14, 0x18, 0x22, 0xff);
    private static readonly RGBAColor32 PlotBackground = new RGBAColor32(0x1b, 0x20, 0x2c, 0xff);
    private static readonly RGBAColor32 Grid = new RGBAColor32(0x33, 0x3a, 0x48, 0xff);
    private static readonly RGBAColor32 Ink = new RGBAColor32(0xd8, 0xdc, 0xe4, 0xff);
    private static readonly RGBAColor32 DimInk = new RGBAColor32(0x9a, 0xa2, 0xb0, 0xff);
    private static readonly RGBAColor32 Curve = new RGBAColor32(0x7f, 0xc8, 0xff, 0xff);
    private static readonly RGBAColor32 Band = new RGBAColor32(0x7f, 0xc8, 0xff, 0x55);
    private static readonly RGBAColor32 Median = new RGBAColor32(0xec, 0xf2, 0xff, 0xff);
    private static readonly RGBAColor32 Keep = new RGBAColor32(0xff, 0xb0, 0x40, 0xff);
    private static readonly RGBAColor32 Reference = new RGBAColor32(0xb0, 0xb8, 0xc8, 0xff);
    private static readonly RGBAColor32 FileEdge = new RGBAColor32(0x60, 0x6a, 0x7c, 0xff);

    /// <summary>
    /// Draws <paramref name="curve"/> with its <paramref name="title"/> over the whole of <paramref name="renderer"/>'s surface;
    /// <paramref name="fontFamily"/> a face's path, or empty to draw no words.
    /// </summary>
    public static PlanetaryQualityReportLayout Render<TSurface>(Renderer<TSurface> renderer, PlanetaryQualityCurve curve, string title, string fontFamily)
    {
        var (w, h) = ((int)renderer.Width, (int)renderer.Height);
        FillRect(renderer, 0, 0, w, h, Background);

        var label = Math.Max(10f, h / 64f);
        var heading = label * 1.35f;
        var left = (int)(label * 4.2f);
        var right = (int)(label * 3.2f);
        var titleH = (int)(heading * 2.2f);
        var captionH = (int)(label * 4.4f);
        var gap = (int)(label * 5f);
        var panelH = (h - titleH - captionH - (2 * gap)) / 2;
        var plotW = w - left - right;

        Text(renderer, title, fontFamily, heading, Ink, left, (int)(heading * 0.5f), plotW, (int)(heading * 1.4f), TextAlign.Near);

        // Both plots' quality axis starts a little below the lowest quality either shows: a run's frames often all lie within a few tens
        // of the best, which an axis from zero squashes into its top strip (AutoStakkert's graph zooms the same way).
        var floor = QualityFloor(curve);

        // Panel 1: sorted, best first, against the share of the run's frames kept.
        var sortedPlot = MakeRect(left, titleH + gap, plotW, panelH);
        Axes(renderer, sortedPlot, floor, fontFamily, label, "quality (best frame 100)",
            share => string.Create(CultureInfo.InvariantCulture, $"{share * 100:0}%"), [0.0, 0.1, 0.2, 0.3, 0.4, 0.5, 0.6, 0.7, 0.8, 0.9, 1.0]);
        Text(renderer, "Sorted, best first: each frame's quality against the share of the run's frames kept", fontFamily, label, DimInk,
            left, sortedPlot.UpperLeft.Y - (int)(label * 2.2f), plotW, (int)(label * 1.6f), TextAlign.Near);

        float ShareX(double share) => sortedPlot.UpperLeft.X + (float)(share * plotW);
        float QualityY(RectInt plot, float quality) => plot.LowerRight.Y - ((quality - floor) / (100f - floor) * (plot.LowerRight.Y - plot.UpperLeft.Y));

        if (curve.Graded > 0)
        {
            // One point a pixel column at most: the curve has as many points as graded frames, tens of thousands on a session.
            var points = Math.Min(curve.Graded, Math.Max(2, plotW));
            var line = new (float X, float Y)[points];
            for (var p = 0; p < points; p++)
            {
                var index = points == 1 ? 0 : (int)((long)p * (curve.Graded - 1) / (points - 1));
                line[p] = (ShareX((index + 1.0) / curve.Frames), QualityY(sortedPlot, curve.Sorted[index]));
            }
            renderer.DrawPolyline(line, Curve, 2);
        }

        foreach (var cut in curve.ReferenceCuts)
        {
            var (x, y) = (ShareX(cut.Share), QualityY(sortedPlot, cut.Quality));
            renderer.DrawLineDashed(x, y, x, sortedPlot.LowerRight.Y, Reference, dashLength: 3f, gapLength: 3f);
            FillRect(renderer, (int)x - 3, (int)y - 3, 7, 7, Reference);
            Text(renderer, string.Create(CultureInfo.InvariantCulture, $"{cut.Quality:0}"), fontFamily, label * 0.9f, Reference,
                (int)x - 30, (int)(y - (label * 2f)), 60, (int)(label * 1.4f), TextAlign.Center);
        }

        var keepShare = (double)curve.KeptFrames / curve.Frames;
        var keepX = ShareX(keepShare);
        var keepY = float.IsNaN(curve.KeepQuality) ? sortedPlot.LowerRight.Y : QualityY(sortedPlot, curve.KeepQuality);
        renderer.DrawLine(keepX, sortedPlot.UpperLeft.Y, keepX, sortedPlot.LowerRight.Y, Keep, 2);
        renderer.DrawLine(sortedPlot.UpperLeft.X, keepY, keepX, keepY, Keep, 1);
        FillRect(renderer, (int)keepX - 5, (int)keepY - 5, 11, 11, Keep);
        var keepWords = string.Create(CultureInfo.InvariantCulture, $"keep {curve.Keep:0.##%}: {curve.KeptFrames:N0} frames, quality {curve.KeepQuality:0} at the cut");
        var labelLeft = keepX + (label * 22f) < sortedPlot.LowerRight.X ? (int)keepX + 8 : (int)keepX - (int)(label * 22f) - 8;
        Text(renderer, keepWords, fontFamily, label, Keep, labelLeft, sortedPlot.UpperLeft.Y + 6, (int)(label * 22f), (int)(label * 1.5f),
            labelLeft > keepX ? TextAlign.Near : TextAlign.Far);

        // Panel 2: through the run, in the order the frames were taken.
        var runPlot = MakeRect(left, sortedPlot.LowerRight.Y + gap, plotW, panelH);
        Axes(renderer, runPlot, floor, fontFamily, label, "quality (best frame 100)",
            share => string.Create(CultureInfo.InvariantCulture, $"{share * curve.Frames:N0}"), [0.0, 0.25, 0.5, 0.75, 1.0]);
        Text(renderer, string.Create(CultureInfo.InvariantCulture,
                $"Through the run: the 10th to 90th percentile and the median per {PlanetaryQualityCurve.BinFrames} frames, the frame number along, a segment a file"),
            fontFamily, label, DimInk, left, runPlot.UpperLeft.Y - (int)(label * 2.2f), plotW, (int)(label * 1.6f), TextAlign.Near);

        float FrameX(double frame) => runPlot.UpperLeft.X + (float)(frame / curve.Frames * plotW);
        for (var file = 1; file < curve.FileStarts.Length; file++)
        {
            var x = FrameX(curve.FileStarts[file]);
            renderer.DrawLine(x, runPlot.UpperLeft.Y, x, runPlot.LowerRight.Y, FileEdge, 1);
        }
        DrawRunBins(renderer, curve, runPlot, FrameX, QualityY);
        if (!float.IsNaN(curve.KeepQuality))
        {
            var levelY = QualityY(runPlot, curve.KeepQuality);
            renderer.DrawLineDashed(runPlot.UpperLeft.X, levelY, runPlot.LowerRight.X, levelY, Keep, dashLength: 8f, gapLength: 5f, thickness: 2);
        }

        Text(renderer, Caption(curve), fontFamily, label, DimInk, left, runPlot.LowerRight.Y + (int)(label * 2.6f), plotW, (int)(label * 1.6f), TextAlign.Near);
        return new PlanetaryQualityReportLayout(sortedPlot, runPlot, floor, keepX, keepY);
    }

    /// <summary>Renders <paramref name="curve"/> as a PNG at <paramref name="path"/>, in the app's own text face.</summary>
    public static async Task WritePngAsync(PlanetaryQualityCurve curve, string title, string path, CancellationToken cancellationToken = default)
    {
        using var renderer = new RgbaImageRenderer(Width, Height);
        Render(renderer, curve, title, BundledFonts.Resolve().Text);
        var png = PngWriter.Encode(renderer.Surface.Pixels, (int)Width, (int)Height, new PngWriteOptions { Cicp = CicpChunk.Srgb });
        if (Path.GetDirectoryName(Path.GetFullPath(path)) is { } folder)
        {
            Directory.CreateDirectory(folder);
        }
        await File.WriteAllBytesAsync(path, png, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The caption: how many frames, how many were left out and why, and where the curve's head ends.</summary>
    public static string Caption(PlanetaryQualityCurve curve)
    {
        var leftOut = curve.LeftOut == 0
            ? "none left out"
            : string.Create(CultureInfo.InvariantCulture,
                $"{curve.LeftOut:N0} left out and in neither panel ({curve.LeftOutCut:N0} cut, {curve.LeftOutSmeared:N0} smeared, {curve.LeftOutDim:N0} dim, {curve.LeftOutUnscored:N0} unreadable)");
        var head = curve.HeadEndsAt is { } end
            ? string.Create(CultureInfo.InvariantCulture, $"; the head ends near {end:0.#%}")
            : "";
        return string.Create(CultureInfo.InvariantCulture, $"{curve.Frames:N0} frames, {curve.Graded:N0} graded; {leftOut}{head}");
    }

    /// <summary>
    /// Where both plots' quality axis starts: the lowest quality either shows (the sorted curve's last graded frame, the run's lowest
    /// 10th percentile) less 5, down to a multiple of 10, and never below zero.
    /// </summary>
    public static float QualityFloor(PlanetaryQualityCurve curve)
    {
        var lowest = curve.Graded > 0 ? curve.Sorted[^1] : 0f;
        foreach (var bin in curve.Bins)
        {
            if (bin.Graded > 0)
            {
                lowest = MathF.Min(lowest, bin.P10);
            }
        }
        return MathF.Max(0f, MathF.Floor((lowest - 5f) / 10f) * 10f);
    }

    private static void DrawRunBins<TSurface>(Renderer<TSurface> renderer, PlanetaryQualityCurve curve, RectInt plot,
        Func<double, float> frameX, Func<RectInt, float, float> qualityY)
    {
        var median = new (float X, float Y)[curve.Bins.Length];
        var drawn = 0;
        var fileEnd = 0;
        var file = 0;
        foreach (var bin in curve.Bins)
        {
            // A new file starts a new median line: the run's curve is a segment a file.
            while (file < curve.FileStarts.Length && bin.First >= curve.FileStarts[file])
            {
                fileEnd = file + 1 < curve.FileStarts.Length ? curve.FileStarts[file + 1] : curve.Frames;
                file++;
                if (drawn > 1)
                {
                    renderer.DrawPolyline(median.AsSpan(0, drawn), Median, 2);
                }
                drawn = 0;
            }
            if (bin.Graded == 0)
            {
                continue;
            }
            var (x0, x1) = (frameX(bin.First), frameX(Math.Min(fileEnd, bin.First + bin.Count)));
            var (top, bottom) = (qualityY(plot, bin.P90), qualityY(plot, bin.P10));
            FillRect(renderer, (int)x0, (int)top, Math.Max(1, (int)MathF.Ceiling(x1 - x0)), Math.Max(1, (int)(bottom - top)), Band);
            median[drawn++] = ((x0 + x1) / 2f, qualityY(plot, bin.Median));
        }
        if (drawn > 1)
        {
            renderer.DrawPolyline(median.AsSpan(0, drawn), Median, 2);
        }
    }

    // A plot's frame, its quality grid from 100 down to the floor (every 10, or 20 over a wide range) and the x ticks, labelled.
    private static void Axes<TSurface>(Renderer<TSurface> renderer, RectInt plot, float floor, string fontFamily, float label, string yTitle,
        Func<double, string> xLabel, double[] xTicks)
    {
        var (x, y) = (plot.UpperLeft.X, plot.UpperLeft.Y);
        var (pw, ph) = (plot.LowerRight.X - x, plot.LowerRight.Y - y);
        FillRect(renderer, x, y, pw, ph, PlotBackground);
        // Down from the best frame's 100, so the top line is always labelled.
        var step = 100f - floor > 50f ? 20 : 10;
        for (var q = 100; q >= floor; q -= step)
        {
            var gy = plot.LowerRight.Y - ((q - floor) / (100f - floor) * ph);
            renderer.DrawLine(x, gy, plot.LowerRight.X, gy, Grid, 1);
            Text(renderer, q.ToString(CultureInfo.InvariantCulture), fontFamily, label, DimInk,
                x - (int)(label * 3.6f), (int)(gy - (label * 0.75f)), (int)(label * 3.2f), (int)(label * 1.5f), TextAlign.Far);
        }
        foreach (var tick in xTicks)
        {
            var tx = x + (float)(tick * pw);
            renderer.DrawLine(tx, plot.LowerRight.Y, tx, plot.LowerRight.Y + 5, DimInk, 1);
            Text(renderer, xLabel(tick), fontFamily, label, DimInk, (int)tx - (int)(label * 4f), plot.LowerRight.Y + 7, (int)(label * 8f),
                (int)(label * 1.5f), TextAlign.Center);
        }
        Text(renderer, yTitle, fontFamily, label * 0.9f, DimInk, x + 6, y + 4, (int)(label * 16f), (int)(label * 1.4f), TextAlign.Near);
    }

    private static void Text<TSurface>(Renderer<TSurface> renderer, string text, string fontFamily, float size, RGBAColor32 colour,
        int x, int y, int w, int h, TextAlign horizontal)
    {
        if (string.IsNullOrEmpty(fontFamily) || w <= 0 || h <= 0)
        {
            return;
        }
        renderer.DrawText(text, fontFamily, size, colour, MakeRect(x, y, w, h), horizontal, TextAlign.Center);
    }

    private static void FillRect<TSurface>(Renderer<TSurface> renderer, int x, int y, int w, int h, RGBAColor32 colour)
    {
        if (w > 0 && h > 0)
        {
            renderer.FillRectangle(MakeRect(x, y, w, h), colour);
        }
    }

    // RectInt is (LowerRight, UpperLeft).
    private static RectInt MakeRect(int x, int y, int w, int h) => new RectInt((x + w, y + h), (x, y));
}
