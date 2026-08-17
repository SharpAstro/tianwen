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
