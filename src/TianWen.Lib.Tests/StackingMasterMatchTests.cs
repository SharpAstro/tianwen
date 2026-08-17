using System;
using System.Collections.Generic;
using Shouldly;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Calibration;
using TianWen.Lib.Imaging.Stacking;
using Xunit;

namespace TianWen.Lib.Tests
{
    /// <summary>
    /// Pins <c>StackingPipeline.MatchMaster</c>'s post-#25 semantics, which are the dataset
    /// resolver's own gates and penalties: until 2026-08-17 the stacker's matcher never consulted
    /// gain, offset, filter or capture date at all -- a g252 dark silently calibrated g121 lights
    /// whenever it won on temperature/exposure, a mislabeled 6.7s dark-flat could calibrate 60s
    /// lights when it was the only candidate, and a Ha flat could serve an OIII group.
    /// </summary>
    [Collection("Stacking")]
    public class StackingMasterMatchTests
    {
        private static (MasterGroupKey Key, Image Master) Master(
            FrameType type, double expoSec, float tempC, short gain = 121, int offset = 25,
            Filter? filter = null, DateTimeOffset? when = null)
        {
            var meta = new ImageMeta(
                Instrument: "TestCam",
                ExposureStartTime: when ?? new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
                ExposureDuration: TimeSpan.FromSeconds(expoSec),
                FrameType: type,
                Telescope: "T",
                PixelSizeX: 3.76f,
                PixelSizeY: 3.76f,
                FocalLength: 135,
                FocusPos: -1,
                Filter: filter ?? Filter.None,
                BinX: 1,
                BinY: 1,
                CCDTemperature: tempC,
                SensorType: SensorType.RGGB,
                BayerOffsetX: 0,
                BayerOffsetY: 0,
                RowOrder: RowOrder.TopDown,
                Latitude: float.NaN,
                Longitude: float.NaN,
                Gain: gain,
                Offset: offset);
            var image = new Image(
                data: [new float[2, 2]],
                bitDepth: BitDepth.Float32,
                maxValue: 1f, minValue: 0f, pedestal: 0f, imageMeta: meta);
            var frame = new FrameInfo("m.fits", 2, 2, 1, BitDepth.Int16, meta);
            return (MasterGroupKey.FromFrame(frame), image);
        }

        private static MasterGroupKey LightKey(
            double expoSec, float tempC, short gain = 121, int offset = 25, Filter? filter = null)
        {
            var (key, _) = Master(FrameType.Light, expoSec, tempC, gain, offset, filter);
            return key;
        }

        private static readonly DateTimeOffset Now = new(2026, 2, 10, 0, 0, 0, TimeSpan.Zero);

        [Fact]
        public void Dark_AWrongGainDarkIsRejected_UnlessTheGateIsLoosened()
        {
            var wrongGain = Master(FrameType.Dark, 60, -5, gain: 252);
            var masters = new List<(MasterGroupKey, Image)> { wrongGain };
            var lightKey = LightKey(60, -5, gain: 121);

            StackingPipeline.MatchMaster(masters, lightKey, StackingPipeline.MasterMatchKind.Dark, Now)
                .Key.ShouldBeNull("a wrong-gain dark mis-scales the fixed pattern; NO dark beats a wrong one");
            StackingPipeline.MatchMaster(masters, lightKey, StackingPipeline.MasterMatchKind.Dark, Now, requireGainMatch: false)
                .Key.ShouldBe(wrongGain.Key, "the lenient escape hatch still serves it");
        }

        [Fact]
        public void Dark_AMislabeledDarkFlatNeverCalibratesALight()
        {
            // N.I.N.A. writes flat-matched short darks as IMAGETYP=DARK; the exposure band
            // (0.5x..2x) is what keeps a 6.7s frame from calibrating 60s lights when it is the
            // only candidate.
            var darkFlat = Master(FrameType.Dark, 6.68, -5);
            var masters = new List<(MasterGroupKey, Image)> { darkFlat };

            StackingPipeline.MatchMaster(masters, LightKey(60, -5), StackingPipeline.MasterMatchKind.Dark, Now)
                .Key.ShouldBeNull();
        }

        [Fact]
        public void Dark_TheNearestEpochWins_WhenThePhysicsTie()
        {
            var early = Master(FrameType.Dark, 60, -5, when: new DateTimeOffset(2021, 9, 27, 0, 0, 0, TimeSpan.Zero));
            var late = Master(FrameType.Dark, 60, -5, when: new DateTimeOffset(2026, 1, 29, 0, 0, 0, TimeSpan.Zero));
            var lightKey = LightKey(60, -5);

            StackingPipeline.MatchMaster([early, late], lightKey, StackingPipeline.MasterMatchKind.Dark, Now)
                .Master.ShouldBeSameAs(late.Master);
            StackingPipeline.MatchMaster([late, early], lightKey, StackingPipeline.MasterMatchKind.Dark, Now)
                .Master.ShouldBeSameAs(late.Master);
        }

        [Fact]
        public void Flat_TheMatchingFilterBeatsACloserTemperature()
        {
            var ha = new Filter("Ha", "Ha", Bandpass.None);
            var oiii = new Filter("OIII", "O3", Bandpass.None);
            var wrongFilter = Master(FrameType.Flat, 3, -5, filter: ha);
            var rightFilter = Master(FrameType.Flat, 3, -15, filter: oiii);
            var lightKey = LightKey(60, -5, filter: oiii);

            // 10C of temperature (penalty 100) must lose to the filter mismatch (1000): a flat
            // encodes the filter's dust and transmission, and a Ha flat is simply wrong for OIII.
            StackingPipeline.MatchMaster([wrongFilter, rightFilter], lightKey, StackingPipeline.MasterMatchKind.Flat, Now)
                .Key.ShouldBe(rightFilter.Key);
        }
    }
}
