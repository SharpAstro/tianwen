using System;
using System.Collections.Generic;
using System.Globalization;

namespace TianWen.Lib.Imaging.Calibration
{
    /// <summary>
    /// Splits one <see cref="MasterGroupKey"/> group's calibration frames into EPOCHS: runs of
    /// frames whose consecutive capture dates never gap by more than <see cref="MaxEpochGapDays"/>.
    ///
    /// <para><b>The bug this exists to end.</b> <see cref="MasterGroupKey"/> deliberately has no
    /// temporal component, so before this, every header-matching dark in a scan root was averaged
    /// into ONE master -- and the header recorded a single representative DATE-OBS, so a blend
    /// across years was invisible in the output. Defects emerge at a measured ~80 px/year on the
    /// reference sensor (342 px unanimous across all six 2024-2026 APP maps and absent from all
    /// nine 2021-2023 ones, against zero in the reverse direction, since defects cannot heal), and
    /// blending epochs attenuates a recently-emerged hot pixel by roughly (its epoch's frames /
    /// total frames), pushing it under the mask detector's threshold. Baking older archive years
    /// would have folded 2021 and 2026 dark libraries into one master silently.</para>
    ///
    /// <para><b>Why a GAP rule rather than a calendar bucket:</b> a library is however many nights
    /// the operator spent shooting it. Chaining on gaps merges a two-week acquisition run (and a
    /// deliberate monthly cadence) into one epoch while splitting libraries months or years apart,
    /// with no arbitrary bucket boundary to straddle. 30 days is far above any single library's
    /// internal spacing in the reference archive (whose real libraries sit months apart:
    /// 2025-05-03, 2025-05-21, 2025-12-20) and far below the year-scale drift the split exists to
    /// keep apart.</para>
    ///
    /// <para>Frames with no capture date (a header-less library reads
    /// <c>default(DateTimeOffset)</c>) cannot be placed on the timeline, so they form one UNDATED
    /// epoch per group -- kept buildable rather than dropped, matching the lenient-on-unknown
    /// policy of every other calibration comparison.</para>
    /// </summary>
    public static class CalibrationEpochs
    {
        /// <summary>Largest gap, in days, between consecutive frames that still chains them into
        /// one epoch.</summary>
        public const int MaxEpochGapDays = 30;

        /// <summary>One epoch: its frames plus the capture-date span they cover.
        /// <paramref name="Start"/>/<paramref name="End"/> are <c>default</c> for the undated
        /// epoch.</summary>
        public readonly record struct Epoch(DateTimeOffset Start, DateTimeOffset End, List<FrameInfo> Frames);

        /// <summary>
        /// Splits <paramref name="frames"/> (one <see cref="MasterGroupKey"/> group) into epochs,
        /// dated epochs first in chronological order, the undated epoch (if any) last.
        /// </summary>
        public static List<Epoch> Split(IReadOnlyList<FrameInfo> frames)
        {
            var dated = new List<FrameInfo>(frames.Count);
            List<FrameInfo>? undated = null;
            foreach (var frame in frames)
            {
                if (frame.Meta.ExposureStartTime == default)
                {
                    (undated ??= []).Add(frame);
                }
                else
                {
                    dated.Add(frame);
                }
            }
            dated.Sort(static (a, b) => a.Meta.ExposureStartTime.CompareTo(b.Meta.ExposureStartTime));

            var epochs = new List<Epoch>();
            var start = 0;
            for (var i = 1; i <= dated.Count; i++)
            {
                if (i < dated.Count
                    && (dated[i].Meta.ExposureStartTime - dated[i - 1].Meta.ExposureStartTime).TotalDays <= MaxEpochGapDays)
                {
                    continue;
                }
                if (i > start)
                {
                    epochs.Add(new Epoch(
                        dated[start].Meta.ExposureStartTime,
                        dated[i - 1].Meta.ExposureStartTime,
                        dated.GetRange(start, i - start)));
                }
                start = i;
            }
            if (undated is not null)
            {
                epochs.Add(new Epoch(default, default, undated));
            }
            return epochs;
        }

        /// <summary>Largest gap, in degrees C, between consecutive sensor readings (sorted by
        /// temperature) that still chains calibration frames into one run.
        ///
        /// <para>Sized from what it must keep together and what it must keep apart, measured over the
        /// 173 calibration runs filed in the reference archive (one capture run per folder,
        /// 2026-09-24). Together: the largest gap between consecutive readings inside any one run is
        /// 1.00 C (an ASI1600MM flat set), while runs SPAN up to 4.8 C (the 294MC's 2021-12-12 darks,
        /// 26.0 to 30.8 C), which the degree key had cut into one group per degree crossed; a cooled library's settling
        /// frames sit 0.6 C off it (ASI585 2025-08-09: two of 76 at -9.4 C beside -10.0). Apart: the
        /// closest two runs of one configuration inside one epoch that must stay two are 2.9 C apart
        /// (the Uranus-C's 2023-07-29 darks at 13 and 17 C). One pair of runs does join, the
        /// ASI294MM's 2021-12-29 and 2022-01-09 darks, 0.7 C and eleven days apart, which is one
        /// library by the same reasoning <see cref="MaxEpochGapDays"/> gives.</para></summary>
        public const double TemperatureToleranceC = 1.5;

        /// <summary>One calibration SET, the unit a master is built from: the frames of one epoch
        /// that also form one temperature run. <paramref name="Start"/>/<paramref name="End"/> span the
        /// set's own frames (default when undated); <paramref name="TemperatureC"/> is the rounded
        /// median of their readings (null when none has one); <paramref name="EpochSuffix"/> is the
        /// epoch's <see cref="EpochSlug"/> when the group split into several epochs and empty
        /// otherwise, as before.</summary>
        public readonly record struct CalibrationSet(
            DateTimeOffset Start, DateTimeOffset End, int? TemperatureC, List<FrameInfo> Frames, string EpochSuffix);

        /// <summary>
        /// Splits the frames of one group that agree on everything BUT temperature into calibration
        /// sets: epochs first (<see cref="Split"/>), then temperature runs within each epoch
        /// (<see cref="TemperatureClusters.Split"/>). Epochs go first so one library's drift cannot
        /// chain, through another shoot's readings, into a set that spans two setpoints.
        /// </summary>
        /// <param name="temperatureToleranceC">Zero or less keeps the old one-set-per-degree grouping
        /// (a foreign master is one integrated file, served as it is, and stays that way).</param>
        public static List<CalibrationSet> SplitSets(IReadOnlyList<FrameInfo> frames, double temperatureToleranceC = TemperatureToleranceC)
        {
            var epochs = Split(frames);
            var sets = new List<CalibrationSet>();
            foreach (var epoch in epochs)
            {
                var suffix = epochs.Count > 1 ? EpochSlug(epoch.Start) : "";
                foreach (var run in TemperatureClusters.Split(epoch.Frames, temperatureToleranceC))
                {
                    var start = epoch.Start;
                    var end = epoch.End;
                    if (start != default)
                    {
                        start = DateTimeOffset.MaxValue;
                        end = DateTimeOffset.MinValue;
                        foreach (var frame in run.Frames)
                        {
                            var t = frame.Meta.ExposureStartTime;
                            if (t < start) start = t;
                            if (t > end) end = t;
                        }
                    }
                    sets.Add(new CalibrationSet(start, end, run.TemperatureC, run.Frames, suffix));
                }
            }
            return sets;
        }

        /// <summary>
        /// Filename-safe suffix identifying an epoch inside a group that split, e.g.
        /// <c>_e20250521</c>. Empty input (the undated epoch) yields <c>_eundated</c>. Callers
        /// append it ONLY when the group actually split into two or more epochs, so a
        /// single-epoch archive keeps its legacy master filenames (and its existing cache).
        /// </summary>
        public static string EpochSlug(DateTimeOffset epochStart) =>
            epochStart == default
                ? "_eundated"
                : "_e" + epochStart.UtcDateTime.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
    }
}
