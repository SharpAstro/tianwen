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
    /// A night whose cooler drifts across a degree boundary must be able to stack as ONE master.
    /// <see cref="LightGroupKey.FromFrame"/> rounds the temperature to the degree, which split a real
    /// 13.7 to 12.1 C session into three masters; <see cref="LightGroupKey.Assign"/> with a tolerance
    /// cuts only at gaps wider than the tolerance and keeps the default path byte-identical at zero.
    /// </summary>
    public class LightGroupKeyTests
    {
        private static FrameInfo Light(float temperatureC, string objectName = "Great Orion Nebula", string path = "x.fits")
        {
            var meta = new ImageMeta(
                Instrument: "SVBONY SV605CC",
                ExposureStartTime: default,
                ExposureDuration: TimeSpan.FromSeconds(120),
                FrameType: FrameType.Light,
                Telescope: "SH61 EDPH",
                PixelSizeX: 3.76f,
                PixelSizeY: 3.76f,
                FocalLength: 270,
                FocusPos: -1,
                Filter: Filter.None,
                BinX: 1,
                BinY: 1,
                CCDTemperature: temperatureC,
                SensorType: SensorType.RGGB,
                BayerOffsetX: 0,
                BayerOffsetY: 0,
                RowOrder: RowOrder.TopDown,
                Latitude: float.NaN,
                Longitude: float.NaN,
                Gain: 120,
                Offset: 20)
            {
                ObjectName = objectName,
            };
            return new FrameInfo(path, 100, 100, 1, BitDepth.Int16, meta);
        }

        private static readonly float[] DriftingNight = [13.7f, 13.5f, 13.2f, 12.9f, 12.6f, 12.4f, 12.1f];

        private static List<FrameInfo> Frames(IEnumerable<float> temperatures, string objectName = "Great Orion Nebula")
            => temperatures.Select((t, i) => Light(t, objectName, $"frame_{i:000}.fits")).ToList();

        [Fact]
        public void AtZeroToleranceTheAssignmentIsFromFramePerFrame()
        {
            var frames = Frames(DriftingNight);
            var keys = LightGroupKey.Assign(frames, 0);
            foreach (var f in frames)
            {
                keys[f].ShouldBe(LightGroupKey.FromFrame(f));
            }

            // Which is the three-master split the tolerance exists to avoid.
            keys.Values.Distinct().Count().ShouldBe(3);
        }

        [Fact]
        public void ADriftAcrossDegreeBoundariesIsOneGroupUnderATolerance()
        {
            var frames = Frames(DriftingNight);
            var keys = LightGroupKey.Assign(frames, 1.0);
            var groups = keys.Values.Distinct().ToList();
            groups.Count.ShouldBe(1);
            // The cluster carries the rounded MEDIAN (12.9 of seven sorted readings), so the dark match
            // and the slug see the night's typical temperature rather than its first frame's.
            groups[0].CalibrationKey.TemperatureC.ShouldBe(13);
            groups[0].Slug().ShouldContain("_13C_");
        }

        [Fact]
        public void AGapWiderThanTheToleranceStillSeparatesNights()
        {
            var frames = Frames(DriftingNight.Concat([8.1f, 7.9f, 8.3f]));
            var keys = LightGroupKey.Assign(frames, 2.0);
            var groups = keys.Values.Distinct().OrderBy(k => k.CalibrationKey.TemperatureC).ToList();
            groups.Count.ShouldBe(2);
            groups[0].CalibrationKey.TemperatureC.ShouldBe(8);
            groups[1].CalibrationKey.TemperatureC.ShouldBe(13);
            frames.Where(f => f.Meta.CCDTemperature < 9f).Select(f => keys[f]).Distinct().Count().ShouldBe(1);
        }

        [Fact]
        public void DifferentTargetsNeverMergeWhateverTheTolerance()
        {
            var frames = Frames([12.5f, 12.6f]).Concat(Frames([12.5f, 12.6f], "Horsehead Nebula")).ToList();
            var keys = LightGroupKey.Assign(frames, 5.0);
            keys.Values.Distinct().Count().ShouldBe(2);
        }

        [Fact]
        public void AFrameWithoutATemperatureKeepsItsOwnGroup()
        {
            var frames = Frames([12.5f, 12.6f]).Concat([Light(float.NaN, path: "notemp.fits")]).ToList();
            var keys = LightGroupKey.Assign(frames, 2.0);
            var groups = keys.Values.Distinct().ToList();
            groups.Count.ShouldBe(2);
            keys[frames[2]].CalibrationKey.TemperatureC.ShouldBeNull();
        }
    }
}
