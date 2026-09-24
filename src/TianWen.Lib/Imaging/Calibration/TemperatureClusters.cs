using System;
using System.Collections.Generic;

namespace TianWen.Lib.Imaging.Calibration;

/// <summary>
/// Splits frames that agree on everything but sensor temperature into runs by TEMPERATURE: sorted by
/// <c>CCD-TEMP</c> and cut only where two consecutive readings are further apart than a tolerance.
/// The one rule for this, shared by light grouping (<see cref="LightGroupKey.Assign"/>) and
/// calibration grouping (<see cref="CalibrationEpochs.SplitSets"/>).
///
/// <para><b>Why not <see cref="MasterGroupKey.TemperatureC"/> alone.</b> That key is the reading
/// rounded to the degree, which is right for a cooled camera at its setpoint and wrong for anything
/// that drifts: an uncooled dark run that read 14 to 20 C became seven groups, and the matcher then
/// took whichever degree sat nearest the lights, however few frames it held (two of fifty, measured on
/// the 2026-09-24 coverage run). A drift is one run; two setpoints a few degrees apart stay two, since
/// no reading bridges the gap between them.</para>
/// </summary>
public static class TemperatureClusters
{
    /// <summary>One run: its frames and the rounded MEDIAN of their temperatures, which is what a
    /// match and a slug then see. <paramref name="TemperatureC"/> is null for the frames with no
    /// temperature at all, which form a run of their own.</summary>
    public readonly record struct Cluster(int? TemperatureC, List<FrameInfo> Frames);

    /// <summary>
    /// Splits <paramref name="frames"/> into temperature runs. A tolerance of zero or less reproduces
    /// the degree-rounded grouping exactly (one run per rounded reading), so a caller that has not
    /// opted in is unchanged. A frame with no temperature is never merged with one that has one.
    /// Runs are returned coldest first, the temperature-less run (if any) last.
    /// </summary>
    public static List<Cluster> Split(IReadOnlyList<FrameInfo> frames, double toleranceC)
    {
        var withTemperature = new List<(float Temperature, FrameInfo Frame)>(frames.Count);
        List<FrameInfo>? without = null;
        foreach (var frame in frames)
        {
            var t = frame.Meta.CCDTemperature;
            if (float.IsNaN(t))
            {
                (without ??= []).Add(frame);
            }
            else
            {
                withTemperature.Add((t, frame));
            }
        }

        var clusters = new List<Cluster>();
        if (toleranceC > 0)
        {
            withTemperature.Sort(static (a, b) => a.Temperature.CompareTo(b.Temperature));
            var start = 0;
            for (var i = 1; i <= withTemperature.Count; i++)
            {
                if (i < withTemperature.Count && withTemperature[i].Temperature - withTemperature[i - 1].Temperature <= toleranceC)
                {
                    continue;
                }

                var count = i - start;
                var mid = start + (count / 2);
                var median = count % 2 == 1
                    ? withTemperature[mid].Temperature
                    : 0.5f * (withTemperature[mid - 1].Temperature + withTemperature[mid].Temperature);
                var members = new List<FrameInfo>(count);
                for (var j = start; j < i; j++)
                {
                    members.Add(withTemperature[j].Frame);
                }
                clusters.Add(new Cluster((int)Math.Round(median), members));
                start = i;
            }
        }
        else
        {
            var byDegree = new SortedDictionary<int, List<FrameInfo>>();
            foreach (var (temperature, frame) in withTemperature)
            {
                var degree = (int)Math.Round(temperature);
                if (!byDegree.TryGetValue(degree, out var list))
                {
                    byDegree[degree] = list = [];
                }
                list.Add(frame);
            }
            foreach (var (degree, list) in byDegree)
            {
                clusters.Add(new Cluster(degree, list));
            }
        }

        if (without is not null)
        {
            clusters.Add(new Cluster(null, without));
        }
        return clusters;
    }

    /// <summary>
    /// How a set's readings are described in a log line: <c>"16.3..17.5 C"</c>, one reading
    /// <c>"-10.0 C"</c>, or empty when no frame carries a temperature.
    /// </summary>
    /// <remarks>
    /// <see cref="Split"/> bounds the gap between two consecutive readings, never a run's whole width,
    /// so a run can be wide: a library started while the cooler was still pulling down chains, a few
    /// tenths at a time, into the setpoint's set. It is keyed on its median, and a master is a
    /// per-pixel median, so a minority of warm frames is outvoted. Printing the width beside every
    /// built master is what makes that case visible rather than silent. The widest run in the
    /// archive, measured 2026-09-24, is 4.8 C (the 294MC's 2021-12-12 darks, 26.0 to 30.8 C;
    /// <see cref="CalibrationEpochs.TemperatureToleranceC"/>).
    /// </remarks>
    public static string DescribeRange(IReadOnlyList<FrameInfo> frames)
    {
        var min = float.PositiveInfinity;
        var max = float.NegativeInfinity;
        foreach (var frame in frames)
        {
            var t = frame.Meta.CCDTemperature;
            if (!float.IsNaN(t))
            {
                min = MathF.Min(min, t);
                max = MathF.Max(max, t);
            }
        }
        return min > max
            ? ""
            : min == max
                ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{min:0.0} C")
                : string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{min:0.0}..{max:0.0} C");
    }

    /// <summary>The rounded median sensor temperature of <paramref name="frames"/>, or null when none
    /// carries one. What a SESSION is matched on: its first light alone can sit a degree off the
    /// rest (a cooler still settling), and one frame's reading then decided the dark for the whole
    /// session.</summary>
    public static int? MedianTemperatureC(IReadOnlyList<FrameInfo> frames)
    {
        var temperatures = new List<float>(frames.Count);
        foreach (var frame in frames)
        {
            if (!float.IsNaN(frame.Meta.CCDTemperature))
            {
                temperatures.Add(frame.Meta.CCDTemperature);
            }
        }
        if (temperatures.Count == 0)
        {
            return null;
        }
        temperatures.Sort();
        var mid = temperatures.Count / 2;
        var median = temperatures.Count % 2 == 1
            ? temperatures[mid]
            : 0.5f * (temperatures[mid - 1] + temperatures[mid]);
        return (int)Math.Round(median);
    }
}
