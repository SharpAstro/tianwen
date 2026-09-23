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
    }
}
