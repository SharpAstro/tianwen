using System;
using System.Collections.Generic;
using System.Linq;
using Shouldly;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Calibration;
using Xunit;

namespace TianWen.Lib.Tests
{
    /// <summary>
    /// Pins the epoch-splitting rule (task #25): a calibration library is however many nights the
    /// operator spent shooting it, so frames CHAIN while consecutive capture dates gap by no more
    /// than <see cref="CalibrationEpochs.MaxEpochGapDays"/>, and a larger gap starts a new epoch.
    /// This is what makes a 2021+2026 blend of one sensor config structurally impossible while a
    /// two-week acquisition run (or a deliberate monthly cadence) still builds one master.
    /// </summary>
    public class CalibrationEpochsTests
    {
        private static FrameInfo Dark(DateTimeOffset? when, float tempC = -10f)
        {
            var meta = new ImageMeta(
                Instrument: "TestCam",
                ExposureStartTime: when ?? default,
                ExposureDuration: TimeSpan.FromSeconds(60),
                FrameType: FrameType.Dark,
                Telescope: "",
                PixelSizeX: 3.76f,
                PixelSizeY: 3.76f,
                FocalLength: -1,
                FocusPos: -1,
                Filter: Filter.None,
                BinX: 1,
                BinY: 1,
                CCDTemperature: tempC,
                SensorType: SensorType.RGGB,
                BayerOffsetX: 0,
                BayerOffsetY: 0,
                RowOrder: RowOrder.TopDown,
                Latitude: float.NaN,
                Longitude: float.NaN,
                Gain: 121,
                Offset: 25);
            return new FrameInfo("x.fits", 100, 100, 1, BitDepth.Int16, meta);
        }

        private static DateTimeOffset Day(int year, int month, int day) => new(year, month, day, 0, 0, 0, TimeSpan.Zero);

        [Fact]
        public void ConsecutiveNightsChain_AYearGapSplits()
        {
            var frames = new List<FrameInfo>
            {
                Dark(Day(2026, 1, 29)),
                Dark(Day(2021, 9, 27)),   // deliberately unsorted input
                Dark(Day(2021, 9, 28)),
                Dark(Day(2021, 10, 5)),   // 7 days on: still the same library
                Dark(Day(2026, 1, 30)),
            };

            var epochs = CalibrationEpochs.Split(frames);

            epochs.Count.ShouldBe(2);
            epochs[0].Start.ShouldBe(Day(2021, 9, 27));
            epochs[0].End.ShouldBe(Day(2021, 10, 5));
            epochs[0].Frames.Count.ShouldBe(3);
            epochs[1].Start.ShouldBe(Day(2026, 1, 29));
            epochs[1].Frames.Count.ShouldBe(2);
        }

        [Fact]
        public void AMonthlyCadenceChainsIntoOneEpoch()
        {
            // The rule is a GAP rule, not a calendar bucket: a deliberate ~25-day cadence never
            // gaps past the threshold, so it remains one library however long it runs.
            var frames = new List<FrameInfo>
            {
                Dark(Day(2025, 1, 1)),
                Dark(Day(2025, 1, 26)),
                Dark(Day(2025, 2, 20)),
                Dark(Day(2025, 3, 17)),
            };

            var epochs = CalibrationEpochs.Split(frames);

            epochs.Count.ShouldBe(1);
            epochs[0].Frames.Count.ShouldBe(4);
        }

        [Fact]
        public void UndatedFramesFormTheirOwnEpoch_Last()
        {
            var frames = new List<FrameInfo>
            {
                Dark(null),
                Dark(Day(2025, 5, 21)),
                Dark(null),
            };

            var epochs = CalibrationEpochs.Split(frames);

            epochs.Count.ShouldBe(2);
            epochs[0].Start.ShouldBe(Day(2025, 5, 21));
            epochs[1].Start.ShouldBe(default);
            epochs[1].Frames.Count.ShouldBe(2);
        }

        [Fact]
        public void EpochSlug_NamesTheStartDate_AndTheUndatedCase()
        {
            CalibrationEpochs.EpochSlug(Day(2025, 5, 21)).ShouldBe("_e20250521");
            CalibrationEpochs.EpochSlug(default).ShouldBe("_eundated");
        }

        /// <summary>A run of <paramref name="count"/> frames on one night, drifting from
        /// <paramref name="fromC"/> by <paramref name="stepC"/> a frame, the way an uncooled camera's
        /// dark run does.</summary>
        private static List<FrameInfo> Run(DateTimeOffset night, int count, float fromC, float stepC)
        {
            var frames = new List<FrameInfo>(count);
            for (var i = 0; i < count; i++)
            {
                frames.Add(Dark(night + TimeSpan.FromMinutes(4 * i), fromC + (stepC * i)));
            }
            return frames;
        }

        [Fact]
        public void SplitSets_ADriftingRunIsOneSet_ASetpointLibraryIsAnother()
        {
            // An uncooled run crossing six degrees is ONE calibration set, keyed on its median; the
            // cooled library beside it, with no reading between the two, is a second.
            var frames = Run(Day(2021, 12, 29), 50, 14.1f, 0.12f);
            frames.AddRange(Run(Day(2021, 12, 30), 20, -10f, 0f));

            var sets = CalibrationEpochs.SplitSets(frames);

            sets.Count.ShouldBe(2);
            sets[0].TemperatureC.ShouldBe(-10);
            sets[0].Frames.Count.ShouldBe(20);
            sets[1].TemperatureC.ShouldBe(17, "the median of 14.1 to 20.0 C");
            sets[1].Frames.Count.ShouldBe(50);
            sets.ShouldAllBe(s => s.EpochSuffix == "", "one epoch, so the legacy cache names hold");
        }

        [Fact]
        public void SplitSets_ACoolersSettlingFramesJoinItsLibrary()
        {
            // The ASI585's 2025-08-09 library: 74 frames at -10.0 C and two that read -9.4 while the
            // cooler settled. Keyed by the degree, those two were a library of their own, and won
            // every session whose lights sat nearer -9 than -10.
            var frames = Run(Day(2025, 8, 10), 74, -10f, 0f);
            frames.Add(Dark(Day(2025, 8, 10), -9.4f));
            frames.Add(Dark(Day(2025, 8, 10) + TimeSpan.FromHours(1.5), -9.4f));

            var set = CalibrationEpochs.SplitSets(frames).ShouldHaveSingleItem();

            set.TemperatureC.ShouldBe(-10);
            set.Frames.Count.ShouldBe(76);
        }

        [Fact]
        public void SplitSets_SplitsEpochsBeforeTemperature_SoAnotherShootCannotBridgeTwoSetpoints()
        {
            // One night at 10 C and 14 C, and months later a run that reads 11, 12 and 13: pooled,
            // those readings would chain 10 to 14 into one set. Epochs first keeps the first night's
            // two setpoints apart.
            var frames = new List<FrameInfo>
            {
                Dark(Day(2021, 1, 1), 10f), Dark(Day(2021, 1, 1), 10f),
                Dark(Day(2021, 1, 2), 14f), Dark(Day(2021, 1, 2), 14f),
                Dark(Day(2021, 6, 1), 11f), Dark(Day(2021, 6, 1), 12f), Dark(Day(2021, 6, 1), 13f),
            };

            var sets = CalibrationEpochs.SplitSets(frames);

            sets.Select(s => (s.TemperatureC, s.Frames.Count)).ToArray().ShouldBe(new (int?, int)[] { (10, 2), (14, 2), (12, 3) });
            sets[0].EpochSuffix.ShouldBe("_e20210101");
            sets[2].EpochSuffix.ShouldBe("_e20210601");
        }

        [Fact]
        public void SplitSets_ASetSpansItsOwnFrames_NotTheWholeEpoch()
        {
            // Two setpoints shot on different nights of one epoch: each set's span is its own nights,
            // which is what the time terms and the coverage report's age read.
            var frames = new List<FrameInfo>
            {
                Dark(Day(2026, 1, 5), -10f), Dark(Day(2026, 1, 6), -10f),
                Dark(Day(2026, 1, 20), -5f), Dark(Day(2026, 1, 21), -5f),
            };

            var sets = CalibrationEpochs.SplitSets(frames);

            sets.Count.ShouldBe(2);
            (sets[0].Start, sets[0].End).ShouldBe((Day(2026, 1, 5), Day(2026, 1, 6)));
            (sets[1].Start, sets[1].End).ShouldBe((Day(2026, 1, 20), Day(2026, 1, 21)));
        }

        [Fact]
        public void SplitSets_AtZeroTolerance_KeepsOneSetPerDegree()
        {
            // The foreign-master path: a master is one integrated file, served as it is.
            var frames = new List<FrameInfo> { Dark(Day(2025, 1, 1), -10f), Dark(Day(2025, 1, 1), -9.4f), Dark(Day(2025, 1, 1), -10.2f) };

            var sets = CalibrationEpochs.SplitSets(frames, temperatureToleranceC: 0);

            sets.Select(s => (s.TemperatureC, s.Frames.Count)).ToArray().ShouldBe(new (int?, int)[] { (-10, 2), (-9, 1) });
        }

        private static FrameInfo Flat(string folder, DateTimeOffset when, double exposureSeconds, string filter = "Red",
            FrameType type = FrameType.Flat, float tempC = -20f)
        {
            var meta = new ImageMeta(
                Instrument: "QSI 683ws",
                ExposureStartTime: when,
                ExposureDuration: TimeSpan.FromSeconds(exposureSeconds),
                FrameType: type,
                Telescope: "FSQ-106",
                PixelSizeX: 5.4f,
                PixelSizeY: 5.4f,
                FocalLength: 530,
                FocusPos: -1,
                Filter: FilterOf(filter),
                BinX: 1,
                BinY: 1,
                CCDTemperature: tempC,
                SensorType: SensorType.Monochrome,
                BayerOffsetX: 0,
                BayerOffsetY: 0,
                RowOrder: RowOrder.TopDown,
                Latitude: float.NaN,
                Longitude: float.NaN);
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "flats", folder, $"{when:yyyyMMddHHmmss}_{exposureSeconds:0.000}.fits");
            return new FrameInfo(path, 100, 100, 1, BitDepth.Int16, meta);
        }

        /// <summary>Built as the header parse builds it: the canonical filter, carrying the raw text.</summary>
        private static Filter FilterOf(string name) => name.Length > 0 ? Filter.FromName(name) with { RawName = name } : Filter.None;

        /// <summary>The stacker's grouping, end to end: <see cref="CalibrationEpochs.SetGroupKey"/>, then
        /// <see cref="CalibrationEpochs.SplitSets"/>, then <see cref="CalibrationEpochs.JoinFlatRuns"/>.</summary>
        private static List<(bool Scope, MasterGroupKey Key, CalibrationEpochs.CalibrationSet Set)> Group(IEnumerable<FrameInfo> frames)
        {
            var sets = new List<(bool Scope, MasterGroupKey Key, CalibrationEpochs.CalibrationSet Set)>();
            foreach (var group in frames.GroupBy(CalibrationEpochs.SetGroupKey))
            {
                foreach (var set in CalibrationEpochs.SplitSets(group.ToList()))
                {
                    sets.Add((false, group.Key with { TemperatureC = set.TemperatureC }, set));
                }
            }
            return CalibrationEpochs.JoinFlatRuns(sets);
        }

        [Fact]
        public void JoinFlatRuns_ASkyFlatRunAtTwentyExposuresIsOneSet()
        {
            // LDN 1622's Red flats (QSI 683ws, 2015-11-13): dusk and dawn of two days, every frame at its
            // own exposure and at flat level. Split by exposure they were twenty singletons, and only one
            // pair of them could build a master.
            var exposures = new[] { 1.132, 1.183, 1.307, 1.444, 1.613, 1.795, 1.995, 2.230, 2.504, 2.814,
                1.635, 1.495, 1.348, 1.217, 1.096, 1.029, 1.074, 1.204, 1.353, 1.530 };
            var frames = exposures.Select((e, i) => Flat("Red/2015-11-13/FLAT", Day(2015, 11, 14) + TimeSpan.FromMinutes(40 * i), e));

            var (_, key, set) = Group(frames).ShouldHaveSingleItem();

            set.Frames.Count.ShouldBe(20);
            key.Exposure.ShouldBe(TimeSpan.FromSeconds(1.4), "the median exposure (1.3985 s) to three significant figures");
            key.TemperatureC.ShouldBe(-20);
            set.EpochSuffix.ShouldBe("");
            (set.Start, set.End).ShouldBe((Day(2015, 11, 14), Day(2015, 11, 14) + TimeSpan.FromMinutes(40 * 19)));
        }

        [Fact]
        public void JoinFlatRuns_AFolderDominatedByOneExposureKeepsItsName()
        {
            // The ASI533 L-Quad 2026-04-22 shape: fifty at 6.682 s and one each at 0.205 s and 16.377 s,
            // all three at the set's level. One set, and the median keeps the key, so its master's name.
            var frames = Enumerable.Range(0, 50).Select(i => Flat("Lq/2026-04-22/FLAT", Day(2026, 4, 22) + TimeSpan.FromSeconds(10 * i), 6.682))
                .Append(Flat("Lq/2026-04-22/FLAT", Day(2026, 4, 22) - TimeSpan.FromMinutes(2), 0.205))
                .Append(Flat("Lq/2026-04-22/FLAT", Day(2026, 4, 22) - TimeSpan.FromMinutes(1), 16.377));

            var (_, key, set) = Group(frames).ShouldHaveSingleItem();

            set.Frames.Count.ShouldBe(52);
            key.Exposure.ShouldBe(TimeSpan.FromSeconds(6.68));
            key.Slug().ShouldBe("flat_6.68s_-20C_Red");
        }

        [Fact]
        public void JoinFlatRuns_NightsInTheirOwnFoldersStayApart_WhateverTheirExposures()
        {
            // The ASI533 L-Ultimate shape: a set a night, each at its own exposure, within one 30-day
            // epoch. Dropping the exposure from the key would blend them; the flat choice prefers the
            // lights' own night, so each stays its own set.
            var frames = new[] { (5, 4.46), (12, 4.48), (19, 4.61), (26, 5.63) }
                .SelectMany(n => Enumerable.Range(0, 3).Select(i =>
                    Flat($"Lu/2025-12-{n.Item1:00}/FLAT", Day(2025, 12, n.Item1) + TimeSpan.FromMinutes(i), n.Item2)));

            var sets = Group(frames);

            sets.Select(s => (s.Key.Exposure.TotalSeconds, s.Set.Frames.Count)).ToArray()
                .ShouldBe(new[] { (4.46, 3), (4.48, 3), (4.61, 3), (5.63, 3) });
        }

        [Fact]
        public void JoinFlatRuns_JoinsOnlyFlatsOfOneFilterAtOneTemperature()
        {
            var night = Day(2015, 11, 14);
            var frames = new[]
            {
                Flat("Mixed/FLAT", night, 1.1), Flat("Mixed/FLAT", night.AddMinutes(1), 1.3),
                Flat("Mixed/FLAT", night.AddMinutes(2), 2.1, filter: "Green"), Flat("Mixed/FLAT", night.AddMinutes(3), 2.4, filter: "Green"),
                // Another setpoint in the same folder is another set.
                Flat("Mixed/FLAT", night.AddMinutes(4), 1.5, tempC: -5f), Flat("Mixed/FLAT", night.AddMinutes(5), 1.7, tempC: -5f),
                // Darks keep their exact exposure, which their scaling reads.
                Flat("Mixed/FLAT", night.AddMinutes(6), 1.1, filter: "", type: FrameType.Dark),
                Flat("Mixed/FLAT", night.AddMinutes(7), 1.3, filter: "", type: FrameType.Dark),
            };

            var sets = Group(frames);

            sets.Select(s => (s.Key.Type, s.Key.FilterIdentity, s.Key.TemperatureC, s.Set.Frames.Count)).ToArray().ShouldBe(new (FrameType, string, int?, int)[]
            {
                (FrameType.Flat, FilterOf("Red").IdentityKey, -20, 2),
                (FrameType.Flat, FilterOf("Green").IdentityKey, -20, 2),
                (FrameType.Flat, FilterOf("Red").IdentityKey, -5, 2),
                (FrameType.Dark, "", -20, 1),
                (FrameType.Dark, "", -20, 1),
            }, ignoreOrder: true);
        }
    }
}
