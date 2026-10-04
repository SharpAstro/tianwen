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

        /// <summary>
        /// The key a calibration frame is grouped under before <see cref="SplitSets"/> divides the
        /// group into sets: its own key with the temperature left out (the set decides that, by run),
        /// and for a FLAT with the exposure to three significant figures. The one grouping rule for
        /// the dataset resolver and <c>tianwen stack</c> alike.
        ///
        /// <para><b>Why a flat's exposure is rounded.</b> A flat wizard steps its exposure by a
        /// fraction of a millisecond while it settles, and the key compares exposure exactly, so one
        /// flat run became two groups: the SV605CC's 2025-12-20 L-Quad set held 46 frames at 0.47768 s
        /// and 3 at 0.47808 s, both slugged <c>flat_0.48s_...</c>, and two sessions got the 3-frame
        /// one. A flat's exposure says nothing about its dust, and the pedestal match that does read
        /// it tolerates a factor of four. Measured over the 96 filed flat and dark-flat runs
        /// (2026-09-24): two hold more than one exposure, that set (0.084% apart) and an ASI533
        /// L-Quad set with one frame each at 0.205 s and 16.4 s beside fifty at 6.68 s. Those two
        /// were taken for a wizard's probes and kept apart; measured on 2026-10-04 they sit at the
        /// set's own level (1.000 and 0.933 of its median mean, the fifty 0.996 to 1.017), and
        /// <see cref="JoinFlatRuns"/> now joins them. Darks keep their exact exposure, which their
        /// scaling reads.</para>
        /// </summary>
        public static MasterGroupKey SetGroupKey(FrameInfo frame)
        {
            var key = MasterGroupKey.FromFrame(frame) with { TemperatureC = null };
            return key.Type is FrameType.Flat ? key with { Exposure = ThreeSignificantFigures(key.Exposure) } : key;
        }

        private static TimeSpan ThreeSignificantFigures(TimeSpan exposure)
        {
            var seconds = exposure.TotalSeconds;
            if (!(seconds > 0))
            {
                return exposure;
            }
            var digits = Math.Clamp(2 - (int)Math.Floor(Math.Log10(seconds)), 0, 15);
            return TimeSpan.FromSeconds(Math.Round(seconds, digits));
        }

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
                    // Narrowed to the set's own frames only in a DATED epoch. That is safe because
                    // Split puts every undated frame in the undated epoch, so a dated epoch holds no
                    // frame without a date to leave the span at MaxValue / MinValue.
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
        /// Joins the flat sets of ONE capture run whose exposure moved: twilight sky flats, whose
        /// exposure follows the sky to hold the level, and any flat run shot at more than one
        /// exposure. Two raw flat sets join when they agree on everything but exposure and temperature
        /// (the same scope and the same key otherwise), their temperatures are within
        /// <see cref="TemperatureToleranceC"/>, and some frame of each lies in the same FOLDER, which is
        /// what a capture run is filed in (<c>CalibrationResolver.IsCopyOfAFrameSeen</c> reads it the
        /// same way). Everything else passes through as it came, in order, with a joined set at its
        /// first member's place.
        ///
        /// <para><b>Why not drop the flat's exposure from the key.</b> Measured over the 32 flat
        /// families of the bake roots (2026-10-04), that changes six of them, each a run of separate
        /// NIGHTS within <see cref="MaxEpochGapDays"/> that today keeps one master per night only
        /// because each night's exposure differs (four ASI533 L-Ultimate nights of 40 to 50 frames
        /// would become one blend of 190), and the flat choice prefers the lights' own night. A folder
        /// holding several exposures is the run itself: in the bake roots there is one, the ASI533
        /// L-Quad 2026-04-22 set (see <see cref="SetGroupKey"/>), beside the four LDN 1622 sky-flat
        /// folders (QSI 683ws, 2015) it was written for, twenty frames at twenty exposures each, all at
        /// flat level, which split into singletons nothing could build.</para>
        ///
        /// <para>A joined set's key carries the MEDIAN exposure of its frames to three significant
        /// figures, so a run dominated by one exposure keeps the name it had, and the median
        /// temperature of its frames. No level guard is applied: every frame of every multi-exposure
        /// folder measured sits at its run's level, and a flat master is a median of normalised
        /// frames.</para>
        /// </summary>
        /// <typeparam name="TScope">What else a set's identity carries beyond its key (the resolver's
        /// optical train and master flag); sets of different scopes never join.</typeparam>
        public static List<(TScope Scope, MasterGroupKey Key, CalibrationSet Set)> JoinFlatRuns<TScope>(
            IReadOnlyList<(TScope Scope, MasterGroupKey Key, CalibrationSet Set)> sets)
            where TScope : IEquatable<TScope>
        {
            var parent = new int[sets.Count];
            for (var i = 0; i < parent.Length; i++)
            {
                parent[i] = i;
            }

            // (scope, key with neither exposure nor temperature, folder) -> the sets with a frame there.
            var byRun = new Dictionary<(TScope Scope, MasterGroupKey Family, string Folder), List<int>>();
            for (var i = 0; i < sets.Count; i++)
            {
                var (scope, key, set) = sets[i];
                if (key.Type is not FrameType.Flat || set.Frames.Exists(static f => f.IsMaster))
                {
                    continue;
                }
                var family = key with { Exposure = TimeSpan.Zero, TemperatureC = null };
                var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var frame in set.Frames)
                {
                    folders.Add(System.IO.Path.GetDirectoryName(frame.Path) ?? "");
                }
                foreach (var folder in folders)
                {
                    var run = (scope, family, folder.ToUpperInvariant());
                    if (!byRun.TryGetValue(run, out var members))
                    {
                        byRun[run] = members = [];
                    }
                    foreach (var other in members)
                    {
                        if (TemperaturesAgree(sets[other].Key.TemperatureC, key.TemperatureC))
                        {
                            Union(other, i);
                        }
                    }
                    members.Add(i);
                }
            }

            var components = new Dictionary<int, List<int>>();
            for (var i = 0; i < sets.Count; i++)
            {
                var root = Find(i);
                if (!components.TryGetValue(root, out var members))
                {
                    components[root] = members = [];
                }
                members.Add(i);
            }

            var joined = new List<(TScope Scope, MasterGroupKey Key, CalibrationSet Set)>(sets.Count);
            var emitted = new HashSet<(TScope Scope, MasterGroupKey Key, string Suffix)>();
            for (var i = 0; i < sets.Count; i++)
            {
                var members = components[Find(i)];
                if (members[0] != i)
                {
                    continue; // absorbed into the set emitted at its first member's place
                }
                if (members.Count == 1)
                {
                    joined.Add(sets[i]);
                    emitted.Add((sets[i].Scope, sets[i].Key, sets[i].Set.EpochSuffix));
                    continue;
                }

                var frames = new List<FrameInfo>();
                var start = DateTimeOffset.MaxValue;
                var end = DateTimeOffset.MinValue;
                var undated = false;
                var suffix = sets[members[0]].Set.EpochSuffix;
                foreach (var m in members)
                {
                    var set = sets[m].Set;
                    frames.AddRange(set.Frames);
                    if (set.Start == default)
                    {
                        undated = true;
                    }
                    else
                    {
                        if (set.Start < start) start = set.Start;
                        if (set.End > end) end = set.End;
                    }
                    if (set.EpochSuffix != suffix)
                    {
                        suffix = "";
                    }
                }
                if (undated || start > end)
                {
                    start = end = default;
                }
                var temperature = TemperatureClusters.MedianTemperatureC(frames);
                var key = sets[i].Key with { Exposure = ThreeSignificantFigures(MedianExposure(frames)), TemperatureC = temperature };
                // A joined set is new, so its name may meet a set's that already exists; the epoch
                // date then tells them apart.
                if (!emitted.Add((sets[i].Scope, key, suffix)))
                {
                    suffix = EpochSlug(start);
                    emitted.Add((sets[i].Scope, key, suffix));
                }
                joined.Add((sets[i].Scope, key, new CalibrationSet(start, end, temperature, frames, suffix)));
            }
            return joined;

            int Find(int x)
            {
                while (parent[x] != x)
                {
                    parent[x] = parent[parent[x]];
                    x = parent[x];
                }
                return x;
            }

            void Union(int a, int b)
            {
                var ra = Find(a);
                var rb = Find(b);
                // The smaller index stays the root, so a joined set keeps its first member's place.
                if (ra < rb) parent[rb] = ra;
                else if (rb < ra) parent[ra] = rb;
            }
        }

        private static bool TemperaturesAgree(int? a, int? b)
            => a is not { } x || b is not { } y || Math.Abs(x - y) <= TemperatureToleranceC;

        private static TimeSpan MedianExposure(List<FrameInfo> frames)
        {
            var seconds = new double[frames.Count];
            for (var i = 0; i < seconds.Length; i++)
            {
                seconds[i] = frames[i].Meta.ExposureDuration.TotalSeconds;
            }
            return TimeSpan.FromSeconds(Stat.StatisticsHelper.MedianFast(seconds));
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
