using System;
using System.Collections.Generic;
using System.Linq;
using TianWen.Lib.Devices;
using TianWen.UI.Abstractions;

namespace TianWen.Cli.Plan;

/// <summary>
/// ASCII fallback for altitude charts. Uses Unicode block characters to render
/// a sparkline-style altitude chart per target.
/// </summary>
internal static class AsciiAltitudeChart
{
    // Block characters from lowest to highest
    private static readonly char[] Blocks = [' ', '▁', '▂', '▃', '▄', '▅', '▆', '▇', '█'];

    /// <summary>
    /// Renders altitude sparklines for each target in the planner state.
    /// Each target gets one row showing altitude across the night using block characters.
    /// </summary>
    public static IReadOnlyList<string> Render(PlannerState state, int maxWidth = 80)
    {
        var lines = new List<string>();

        var start = state.CivilSet ?? state.AstroDark - TimeSpan.FromHours(1);
        var end = state.CivilRise ?? state.AstroTwilight + TimeSpan.FromHours(1);
        var totalHours = (end - start).TotalHours;

        if (totalHours <= 0)
        {
            return ["No night window available."];
        }

        // Name column width
        var nameWidth = 16;
        var chartWidth = maxWidth - nameWidth - 3; // 3 for " | "
        if (chartWidth < 10)
        {
            chartWidth = 10;
        }

        // Time header
        var header = new string(' ', nameWidth) + " | ";
        var hourStep = Math.Max(1, (int)Math.Ceiling(totalHours / (chartWidth / 5.0)));
        var firstHour = new DateTimeOffset(start.Year, start.Month, start.Day, start.Hour + 1, 0, 0, start.Offset);
        for (var t = firstHour; t < end; t = t.AddHours(hourStep))
        {
            var col = (int)((t - start).TotalHours / totalHours * chartWidth);
            if (col >= 0 && col < chartWidth)
            {
                var label = t.ToLocalTime().ToString("HH");
                while (header.Length < nameWidth + 3 + col)
                {
                    header += ' ';
                }
                header += label;
            }
        }
        lines.Add(header);

        // Min altitude marker line
        var minAltLine = new string(' ', nameWidth) + " | ";
        var minAltChars = new char[chartWidth];
        Array.Fill(minAltChars, '·');
        minAltLine += new string(minAltChars) + $" {state.MinHeightAboveHorizon}°";
        lines.Add(minAltLine);

        // One sparkline row per target (top 15)
        var targets = state.AltitudeProfiles.Keys.Take(15).ToList();
        foreach (var target in targets)
        {
            if (!state.AltitudeProfiles.TryGetValue(target, out var profile) || profile.Count == 0)
            {
                continue;
            }

            var isProposed = state.Proposals.Any(p => p.Target == target);
            var marker = isProposed ? "*" : " ";

            var name = target.Name;
            if (name.Length > nameWidth - 1)
            {
                name = name[..(nameWidth - 2)] + ".";
            }

            var row = new char[chartWidth];
            Array.Fill(row, ' ');

            foreach (var (time, alt) in profile)
            {
                var col = (int)((time - start).TotalHours / totalHours * chartWidth);
                if (col >= 0 && col < chartWidth && alt >= 0)
                {
                    var blockIdx = (int)(alt / 90.0 * (Blocks.Length - 1));
                    blockIdx = Math.Clamp(blockIdx, 0, Blocks.Length - 1);
                    // Keep the higher block if already set
                    if (Blocks.AsSpan().IndexOf(row[col]) < blockIdx)
                    {
                        row[col] = Blocks[blockIdx];
                    }
                }
            }

            var nameStr = $"{marker}{name}".PadRight(nameWidth);
            lines.Add($"{nameStr} | {new string(row)}");
        }

        return lines;
    }
}
