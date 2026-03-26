using System;
using System.Collections.Immutable;
using TianWen.Lib.Devices.Guider;
using TianWen.Lib.Sequencing;

namespace TianWen.UI.Abstractions;

/// <summary>
/// Static helpers for the guider tab — placeholder text, formatting, sparklines.
/// </summary>
public static class GuiderActions
{
    private const string SparkChars = "▁▂▃▄▅▆▇█";

    /// <summary>Banner text for each placeholder state.</summary>
    public static string PlaceholderText(GuiderPlaceholder reason) => reason switch
    {
        GuiderPlaceholder.NoSession => "No session running",
        GuiderPlaceholder.Connecting => "Connecting devices\u2026",
        GuiderPlaceholder.Calibrating => "Calibrating guider\u2026",
        GuiderPlaceholder.NotGuiding => "Guider not active in this phase",
        GuiderPlaceholder.WaitingForGuider => "Waiting for guider to start\u2026",
        _ => ""
    };

    /// <summary>One-line RMS summary.</summary>
    public static string FormatRmsSummary(GuideStats? stats)
    {
        if (stats is null)
        {
            return "RMS: --";
        }
        return $"Total: {stats.TotalRMS:F2}\"  Ra: {stats.RaRMS:F2}\"  Dec: {stats.DecRMS:F2}\"";
    }

    /// <summary>Last error line: "Ra: +0.42"  Dec: -0.18"".</summary>
    public static string FormatLastError(GuideStats? stats)
    {
        if (stats is null || !stats.LastRaErr.HasValue)
        {
            return "Last: --";
        }
        return $"Last Ra: {stats.LastRaErr.Value:+0.00;-0.00}\"  Dec: {stats.LastDecErr ?? 0:+0.00;-0.00}\"";
    }

    /// <summary>Multi-line stats block for TUI markdown or GPU text panel.</summary>
    public static string FormatStatsBlock(GuiderTabState state)
    {
        var stats = state.LastGuideStats;
        if (stats is null)
        {
            return "No guide data yet";
        }

        var settle = state.GuiderSettleProgress;
        var settleStr = settle is { Done: false }
            ? $"Settle: {settle.Distance:F2}\"/{ settle.SettlePx:F2}\""
            : "";
        var expStr = state.GuideExposure > TimeSpan.Zero
            ? $"Exp: {state.GuideExposure.TotalSeconds:F1}s"
            : "";

        return $"""
            Total RMS: {stats.TotalRMS:F2}"

            Ra RMS:    {stats.RaRMS:F2}"

            Dec RMS:   {stats.DecRMS:F2}"

            Peak Ra:   {stats.PeakRa:F2}"

            Peak Dec:  {stats.PeakDec:F2}"

            {FormatLastError(stats)}

            {expStr}

            {settleStr}
            """;
    }

    /// <summary>
    /// Computes the Y-axis range for the guide error graph.
    /// Returns symmetric range around zero with a minimum extent.
    /// </summary>
    public static (double Min, double Max) ComputeGraphRange(
        ImmutableArray<GuideErrorSample> samples, double minRange = 2.0)
    {
        var maxAbs = minRange / 2;
        for (var i = 0; i < samples.Length; i++)
        {
            var ra = Math.Abs(samples[i].RaError);
            var dec = Math.Abs(samples[i].DecError);
            if (ra > maxAbs) maxAbs = ra;
            if (dec > maxAbs) maxAbs = dec;
        }
        // Round up to nice value
        maxAbs = Math.Ceiling(maxAbs * 2) / 2; // round to 0.5
        return (-maxAbs, maxAbs);
    }

    /// <summary>
    /// Builds Unicode sparklines for RA and Dec guide errors, independently scaled per axis.
    /// Each axis gets two rows: positive errors go up (row 1), negative go down inverted (row 2).
    /// Like PHD2's dual-axis graph with a zero line between them.
    /// </summary>
    public static (string Ra, string Dec, string RaRange, string DecRange) BuildGuideSparklines(
        ImmutableArray<GuideErrorSample> samples, int width)
    {
        if (samples.Length < 2)
        {
            return ("--", "--", "", "");
        }

        // Take last 'width' samples
        var start = Math.Max(0, samples.Length - width);
        var count = Math.Min(width, samples.Length - start);

        // Compute per-axis max absolute error
        var raMax = 0.5;
        var decMax = 0.5;
        for (var i = 0; i < count; i++)
        {
            var s = samples[start + i];
            var ra = Math.Abs(s.RaError);
            var dec = Math.Abs(s.DecError);
            if (ra > raMax) raMax = ra;
            if (dec > decMax) decMax = dec;
        }
        raMax = Math.Ceiling(raMax * 2) / 2; // round to 0.5
        decMax = Math.Ceiling(decMax * 2) / 2;

        var raPosNeg = BuildDualRowSparkline(samples, start, count, raMax, static s => s.RaError);
        var decPosNeg = BuildDualRowSparkline(samples, start, count, decMax, static s => s.DecError);

        return (raPosNeg, decPosNeg, $"\u00b1{raMax:F1}\"", $"\u00b1{decMax:F1}\"");
    }

    /// <summary>
    /// Builds a two-row sparkline mirrored around zero like PHD2's guide graph:
    /// top row = positive errors (bars grow up), bottom row = negative errors (bars hang down
    /// using reverse video to flip the block characters).
    /// </summary>
    private static string BuildDualRowSparkline(
        ImmutableArray<GuideErrorSample> samples, int start, int count,
        double maxAbs, Func<GuideErrorSample, double> selector)
    {
        var posRow = new char[count];
        var negRow = new char[count];

        for (var i = 0; i < count; i++)
        {
            var value = selector(samples[start + i]);
            var norm = maxAbs > 0 ? Math.Abs(value) / maxAbs : 0;
            var idx = (int)(norm * (SparkChars.Length - 1));
            var ch = SparkChars[Math.Clamp(idx, 0, SparkChars.Length - 1)];

            if (value >= 0)
            {
                posRow[i] = ch;
                negRow[i] = ' ';
            }
            else
            {
                posRow[i] = ' ';
                negRow[i] = ch;
            }
        }

        // Negative row uses reverse video (\e[7m) so block chars hang from top
        return $"{new string(posRow)}\n\n\x1b[7m{new string(negRow)}\x1b[27m";
    }
}
