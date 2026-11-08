using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;

namespace TianWen.Lib.Imaging.Stacking;

/// <summary>
/// The run's one line about colour alignment (#1993): how many frames had their red and blue offsets from green measured, how
/// many had too few stars for a reading and so align with zero offsets, and what the measured ones read (the median over frames,
/// and the range, since the split changes with altitude and a range is how a session shows it).
/// </summary>
internal static class ColourAlignmentReport
{
    public static void Log(ILogger logger, IReadOnlyList<ChannelOffsetsResult> measured, int unmeasured)
    {
        if (measured.Count == 0)
        {
            logger.LogWarning(
                "  colour alignment: no frame had enough stars for a reading ({Unmeasured} frames aligned with zero offsets)", unmeasured);
            return;
        }

        var (redX, redY, blueX, blueY) = (new double[measured.Count], new double[measured.Count], new double[measured.Count], new double[measured.Count]);
        for (var i = 0; i < measured.Count; i++)
        {
            redX[i] = measured[i].Red.Dx;
            redY[i] = measured[i].Red.Dy;
            blueX[i] = measured[i].Blue.Dx;
            blueY[i] = measured[i].Blue.Dy;
        }

        var message = "  colour alignment: {Measured} of {Total} frames measured, {Unmeasured} aligned with zero offsets (too few stars); "
            + "median offset from green: red ({RedX:+0.000;-0.000},{RedY:+0.000;-0.000}) px, blue ({BlueX:+0.000;-0.000},{BlueY:+0.000;-0.000}) px, "
            + "longest red {MaxRed:F3} px, longest blue {MaxBlue:F3} px";
        var total = measured.Count + unmeasured;
        var level = unmeasured > 0 ? LogLevel.Warning : LogLevel.Information;
        logger.Log(level, message,
            measured.Count, total, unmeasured,
            Median(redX), Median(redY), Median(blueX), Median(blueY),
            Longest(measured, static r => r.Red), Longest(measured, static r => r.Blue));
    }

    private static double Median(double[] values)
    {
        Array.Sort(values);
        var mid = values.Length / 2;
        return values.Length % 2 == 1 ? values[mid] : 0.5 * (values[mid - 1] + values[mid]);
    }

    private static double Longest(IReadOnlyList<ChannelOffsetsResult> measured, Func<ChannelOffsetsResult, ChannelOffset> pick)
    {
        var longest = 0.0;
        foreach (var reading in measured)
        {
            longest = Math.Max(longest, pick(reading).Length);
        }
        return longest;
    }
}
