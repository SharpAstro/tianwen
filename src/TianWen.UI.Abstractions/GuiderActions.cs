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

    // Braille dot offsets within each 2×4 cell:
    //  col 0    col 1
    //  bit 0    bit 3    row 0
    //  bit 1    bit 4    row 1
    //  bit 2    bit 5    row 2
    //  bit 6    bit 7    row 3
    private static readonly int[] BrailleBits = [0x01, 0x02, 0x04, 0x40, 0x08, 0x10, 0x20, 0x80];

    /// <summary>
    /// Builds a Braille-resolution target view (2D scatter of RA vs Dec error) for TUI display.
    /// Each character cell is 2×4 sub-pixels, giving high-resolution scatter for the terminal.
    /// PHD2-style bull's-eye with crosshair, ring, and recent guide errors.
    /// </summary>
    /// <param name="samples">Guide error samples.</param>
    /// <param name="cellsWide">Width in character cells (sub-pixel width = cellsWide * 2).</param>
    public static string BuildTargetView(ImmutableArray<GuideErrorSample> samples, int cellsWide = 21)
    {
        if (samples.Length < 2)
        {
            return "No guide data";
        }

        // Make it square in sub-pixels: width = cellsWide*2, height = cellsHigh*4
        // For square aspect: cellsHigh = cellsWide * 2 / 4 = cellsWide / 2
        var cellsHigh = Math.Max(cellsWide / 2, 5);
        var subW = cellsWide * 2;
        var subH = cellsHigh * 4;
        var halfW = subW / 2;
        var halfH = subH / 2;
        var half = Math.Min(halfW, halfH); // square plotting radius

        // Braille bitmap: one byte per character cell
        var cells = new int[cellsHigh, cellsWide];

        void SetSubPixel(int sx, int sy)
        {
            if (sx < 0 || sx >= subW || sy < 0 || sy >= subH) return;
            var cx = sx / 2;
            var cy = sy / 4;
            var dx = sx % 2;
            var dy = sy % 4;
            cells[cy, cx] |= BrailleBits[dy + dx * 4];
        }

        // Draw crosshair
        for (var i = 0; i < subW; i++) SetSubPixel(i, halfH);
        for (var i = 0; i < subH; i++) SetSubPixel(halfW, i);

        // Draw circle at ~75% radius
        var ringR = half * 0.75;
        var ringSteps = (int)(ringR * 4);
        for (var i = 0; i < ringSteps; i++)
        {
            var angle = 2.0 * Math.PI * i / ringSteps;
            var rx = halfW + (int)Math.Round(ringR * Math.Cos(angle));
            var ry = halfH - (int)Math.Round(ringR * Math.Sin(angle));
            SetSubPixel(rx, ry);
        }

        // Compute scale from recent peak errors
        var maxAbs = 0.5;
        var recentStart = Math.Max(0, samples.Length - 100);
        for (var i = recentStart; i < samples.Length; i++)
        {
            maxAbs = Math.Max(maxAbs, Math.Max(Math.Abs(samples[i].RaError), Math.Abs(samples[i].DecError)));
        }
        maxAbs = Math.Ceiling(maxAbs * 2) / 2;

        // Plot recent samples
        var plotStart = Math.Max(0, samples.Length - 80);
        for (var i = plotStart; i < samples.Length; i++)
        {
            var s = samples[i];
            var sx = halfW + (int)Math.Round(Math.Clamp(s.RaError / maxAbs, -1, 1) * half);
            var sy = halfH - (int)Math.Round(Math.Clamp(s.DecError / maxAbs, -1, 1) * half);
            SetSubPixel(sx, sy);
            // Make recent points thicker (2×2)
            var age = (float)(i - plotStart) / (samples.Length - plotStart);
            if (age > 0.7f)
            {
                SetSubPixel(sx + 1, sy);
                SetSubPixel(sx, sy + 1);
                SetSubPixel(sx + 1, sy + 1);
            }
        }

        // Render to string
        var lines = new string[cellsHigh + 2];
        lines[0] = $"  Target \u00b1{maxAbs:F1}\"  RA\u2192  Dec\u2191";
        for (var y = 0; y < cellsHigh; y++)
        {
            var row = new char[cellsWide];
            for (var x = 0; x < cellsWide; x++)
            {
                row[x] = (char)(0x2800 + cells[y, x]);
            }
            lines[y + 1] = new string(row);
        }
        lines[cellsHigh + 1] = "";
        return string.Join('\n', lines);
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
