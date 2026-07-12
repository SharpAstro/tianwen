using Shouldly;
using System;
using System.Collections.Generic;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Calibration;
using TianWen.Lib.Imaging.Dataset;
using Xunit;

namespace TianWen.Lib.Tests
{
    /// <summary>
    /// Pure-logic coverage for <see cref="CalibrationResolver.GroupCalibration"/> (dataset builder
    /// #43): calibration frames bucket by <see cref="MasterGroupKey"/> and by frame type, and
    /// non-calibration frames are ignored. The archive-wide match + master build is exercised
    /// end-to-end by <see cref="DatasetBuildRunnerTests"/>.
    /// </summary>
    public class CalibrationResolverTests
    {
        private static FrameInfo Cal(FrameType type, double expoSec, float tempC)
        {
            var meta = new ImageMeta(
                Instrument: "TestCam",
                ExposureStartTime: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
                ExposureDuration: TimeSpan.FromSeconds(expoSec),
                FrameType: type,
                Telescope: "T",
                PixelSizeX: 3.76f,
                PixelSizeY: 3.76f,
                FocalLength: 135,
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
                Gain: 100);
            return new FrameInfo("x.fits", 100, 100, 1, BitDepth.Int16, meta);
        }

        [Fact]
        public void GroupCalibration_BucketsByTypeAndKey_IgnoresLights()
        {
            var frames = new List<FrameInfo>
            {
                Cal(FrameType.Dark, 60, -10),
                Cal(FrameType.Dark, 60, -10),   // same key as the first -> one group of two
                Cal(FrameType.Dark, 60, -5),    // different temp -> a second dark group
                Cal(FrameType.Flat, 3, -10),
                Cal(FrameType.Flat, 3, -10),    // one flat group of two
                Cal(FrameType.Light, 60, -10),  // ignored (not a calibration frame)
            };

            var groups = CalibrationResolver.GroupCalibration(frames);

            groups.ContainsKey(FrameType.Light).ShouldBeFalse();

            groups.TryGetValue(FrameType.Dark, out var darks).ShouldBeTrue();
            darks!.Count.ShouldBe(2); // -10C and -5C are distinct MasterGroupKeys
            var darkFrameTotal = 0;
            foreach (var g in darks)
            {
                g.Key.Type.ShouldBe(FrameType.Dark);
                darkFrameTotal += g.Frames.Length;
            }
            darkFrameTotal.ShouldBe(3);

            groups.TryGetValue(FrameType.Flat, out var flats).ShouldBeTrue();
            flats!.Count.ShouldBe(1);
            flats[0].Frames.Length.ShouldBe(2);
        }
    }
}
