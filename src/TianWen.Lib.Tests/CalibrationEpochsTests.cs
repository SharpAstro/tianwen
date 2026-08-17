using System;
using System.Collections.Generic;
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
        private static FrameInfo Dark(DateTimeOffset? when)
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
                CCDTemperature: -10f,
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
    }
}
