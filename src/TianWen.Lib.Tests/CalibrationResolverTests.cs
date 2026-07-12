using Shouldly;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
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
        private static FrameInfo Cal(FrameType type, double expoSec, float tempC, short gain = 100,
            string instrument = "TestCam", string telescope = "T", int focalLength = 135)
        {
            var meta = new ImageMeta(
                Instrument: instrument,
                ExposureStartTime: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
                ExposureDuration: TimeSpan.FromSeconds(expoSec),
                FrameType: type,
                Telescope: telescope,
                PixelSizeX: 3.76f,
                PixelSizeY: 3.76f,
                FocalLength: focalLength,
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
                Gain: gain,
                Offset: 25);
            return new FrameInfo("x.fits", 100, 100, 1, BitDepth.Int16, meta);
        }

        private static CalibrationResolver.CalGroup Group(FrameType type, double expoSec, float tempC, short gain = 100,
            string instrument = "TestCam", string telescope = "T", int focalLength = 135, int frameCount = 2)
        {
            var f = Cal(type, expoSec, tempC, gain, instrument, telescope, focalLength);
            // Default 2 frames = buildable (a master needs >= 2); pass frameCount: 1 to model an
            // unbuildable singleton. The frames' content is irrelevant to Best* (they read Key + Train).
            var frames = Enumerable.Repeat(f, frameCount).ToImmutableArray();
            return new(MasterGroupKey.FromFrame(f), CalibrationResolver.CalTrain.ForFrame(f), frames);
        }

        private static FrameInfo Light(double expoSec, float tempC, short gain,
            string instrument = "TestCam", string telescope = "T", int focalLength = 135)
            => Cal(FrameType.Light, expoSec, tempC, gain, instrument, telescope, focalLength);

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

        [Fact]
        public void BestDark_SameGainWins_OverIdenticalTempAndExposureAtWrongGain()
        {
            // Gain participates in the dark score: a wrong-gain dark mis-scales the fixed pattern
            // that dark subtraction removes for N2N independence, so when a same-gain library
            // exists it must win — regardless of input order.
            var wrongGain = Group(FrameType.Dark, 60, -5, gain: 212);
            var sameGain = Group(FrameType.Dark, 60, -5, gain: 121);
            var light = Light(60, -5, gain: 121);

            CalibrationResolver.BestDark([wrongGain, sameGain], light).ShouldBe(sameGain);
            CalibrationResolver.BestDark([sameGain, wrongGain], light).ShouldBe(sameGain);
        }

        [Fact]
        public void BestDark_WrongGainMatchedExposureAndTemp_BeatsWarmShortSameGainDark()
        {
            // The real-archive trade-off (2026: g121/60s/-5C lights, only a g212 60s/-5C library
            // and g121 4.5s/+22C flat-wizard darks exist): the matched-exposure/temperature dark
            // is the better of two bad options even at the wrong gain — the warm short dark holds
            // essentially none of the lights' dark-current pattern. Pins the penalty sizing.
            var wrongGainRightDark = Group(FrameType.Dark, 60, -5, gain: 212);
            var sameGainUselessDark = Group(FrameType.Dark, 4.5, 22, gain: 121);
            var light = Light(60, -5, gain: 121);

            CalibrationResolver.BestDark([sameGainUselessDark, wrongGainRightDark], light).ShouldBe(wrongGainRightDark);
        }

        [Fact]
        public void BestDark_ScoreTie_BreaksBySlugOrdinal_RegardlessOfInputOrder()
        {
            // Exact score ties are real (here: unknown-gain penalty 100 == 10C-off temp penalty
            // 10x10). Without a deterministic tie-break the winner would follow dictionary /
            // filesystem enumeration order, breaking the build's re-run determinism claim.
            var unknownGain = Group(FrameType.Dark, 60, -5, gain: -1);   // slug "dark_60s_-5C"
            var tempOff = Group(FrameType.Dark, 60, -15, gain: 121);     // slug "dark_60s_-15C_g121"
            var light = Light(60, -5, gain: 121);

            // Ordinal: '1' < '5' at the temp digit, so "dark_60s_-15C_g121" sorts first.
            CalibrationResolver.BestDark([unknownGain, tempOff], light).ShouldBe(tempOff);
            CalibrationResolver.BestDark([tempOff, unknownGain], light).ShouldBe(tempOff);
        }

        [Fact]
        public void BestDark_ExcludesAShortDarkFlat_ForALongLight_EvenAtMatchingGain()
        {
            // The 4.6s/6.7s -5C "darks" in the archive are DARK-FLATS (matched to the flat exposure,
            // shot in a DARKFLAT\ folder) that N.I.N.A. labels IMAGETYP=DARK. They must never calibrate
            // a 60s LIGHT: dark current scales with exposure, so a ~9x-too-short frame is not a valid
            // light-dark. The matched-exposure dark wins even at the wrong gain (mirrors the stack
            // pipeline's exposure-primary matcher).
            var darkFlat = Group(FrameType.Dark, 6.68, -5, gain: 121);      // same gain+temp, ~9x too short
            var matchedExposure = Group(FrameType.Dark, 60, -5, gain: 212); // right exposure+temp, wrong gain
            var light = Light(60, -5, gain: 121);

            CalibrationResolver.BestDark([darkFlat, matchedExposure], light).ShouldBe(matchedExposure);
        }

        [Fact]
        public void BestDark_OnlyADarkFlatExists_ReturnsNull_SoRequireDarkSkipsTheSession()
        {
            // No light-exposure dark, only a short dark-flat -> no valid light-dark -> null, so
            // RequireDarkCalibration skips the session rather than calibrating lights with a dark-flat.
            var darkFlatOnly = Group(FrameType.Dark, 6.68, -5, gain: 121);
            var light = Light(60, -5, gain: 121);

            CalibrationResolver.BestDark([darkFlatOnly], light).ShouldBeNull();
        }

        [Fact]
        public void BestFlat_SameGainPreferred_WhenFilterAndTempTie()
        {
            var wrongGain = Group(FrameType.Flat, 3, -5, gain: 212);
            var sameGain = Group(FrameType.Flat, 3, -5, gain: 121);
            var light = Light(60, -5, gain: 121);

            CalibrationResolver.BestFlat([wrongGain, sameGain], light).ShouldBe(sameGain);
            CalibrationResolver.BestFlat([sameGain, wrongGain], light).ShouldBe(sameGain);
        }

        [Fact]
        public void BestDark_RejectsDarkFromADifferentCamera_EvenWhenSensorGainTempExposureMatch()
        {
            // Two IMX533 bodies share dimensions + Bayer + gain + temp, but a dark is the CAMERA's own
            // fixed pattern (amp glow, unit-to-unit variation) -- never interchangeable across bodies.
            // Its own gain/temp/exposure are a perfect match; only the instrument differs.
            var foreign = Group(FrameType.Dark, 60, -5, gain: 121, instrument: "SVBONY SV605CC", telescope: "SV", focalLength: 400);
            var light = Light(60, -5, gain: 121, instrument: "ZWO ASI533MC Pro", telescope: "Askar", focalLength: 400);

            CalibrationResolver.BestDark([foreign], light).ShouldBeNull();
        }

        [Fact]
        public void BestFlat_RejectsFlatFromADifferentCamera_EvenWhenSensorMatches()
        {
            // Same sensor, different body -> a DIFFERENT scope's vignetting + dust. Wrong flat.
            var foreign = Group(FrameType.Flat, 3, -5, gain: 121, instrument: "SVBONY SV605CC", telescope: "Askar", focalLength: 400);
            var light = Light(60, -5, gain: 121, instrument: "ZWO ASI533MC Pro", telescope: "Askar", focalLength: 400);

            CalibrationResolver.BestFlat([foreign], light).ShouldBeNull();
        }

        [Fact]
        public void BestFlat_RejectsFlatFromTheSameCameraButADifferentFocalLength()
        {
            // Same camera + scope, but a focal reducer changes the illumination cone -> wrong flat.
            var reduced = Group(FrameType.Flat, 3, -5, gain: 121, instrument: "ZWO ASI533MC Pro", telescope: "Askar", focalLength: 300);
            var light = Light(60, -5, gain: 121, instrument: "ZWO ASI533MC Pro", telescope: "Askar", focalLength: 400);

            CalibrationResolver.BestFlat([reduced], light).ShouldBeNull();
        }

        [Fact]
        public void BestFlat_MatchesFlatFromTheSameOpticalTrain()
        {
            var ok = Group(FrameType.Flat, 3, -5, gain: 121, instrument: "ZWO ASI533MC Pro", telescope: "Askar", focalLength: 400);
            var light = Light(60, -5, gain: 121, instrument: "ZWO ASI533MC Pro", telescope: "Askar", focalLength: 400);

            CalibrationResolver.BestFlat([ok], light).ShouldBe(ok);
        }

        [Fact]
        public void BestDark_SkipsUnbuildableSingleton_EvenWithAPerfectScore()
        {
            // A 1-frame group can never build a master (median needs >= 2). If Best* returned it, the
            // resolved dark would be null and RequireDarkCalibration would wrongly skip a session that
            // DID have a buildable dark. So the buildable (exposure-matched) dark must win over the
            // gain-perfect singleton -- both are exposure-compatible, isolating the buildable filter.
            var perfectSingleton = Group(FrameType.Dark, 60, -5, gain: 121, frameCount: 1); // score 0, unbuildable
            var buildable = Group(FrameType.Dark, 60, -5, gain: 212, frameCount: 2);        // exposure-matched, wrong gain, buildable
            var light = Light(60, -5, gain: 121);

            CalibrationResolver.BestDark([perfectSingleton, buildable], light).ShouldBe(buildable);
        }

        [Fact]
        public void BestFlat_SkipsUnbuildableSingleton_ForABuildableGroup()
        {
            // Real archive: a lone 0.21s flat frame (slug sorts first) was out-ranking the multi-frame
            // 4.61s flat and leaving the session with no flat at all.
            var singleton = Group(FrameType.Flat, 0.21, -5, gain: 121, frameCount: 1);
            var buildable = Group(FrameType.Flat, 4.61, -5, gain: 121, frameCount: 2);
            var light = Light(60, -5, gain: 121);

            CalibrationResolver.BestFlat([singleton, buildable], light).ShouldBe(buildable);
        }

        [Fact]
        public void BestFlat_UnknownTelescopeOnEitherSide_IsAWildcard_NotADrop()
        {
            // A missing TELESCOP/FOCALLEN header must not wrongly drop an otherwise-matching flat
            // (same camera) -- unknown fields are lenient, only two KNOWN differing values reject.
            var flatNoScope = Group(FrameType.Flat, 3, -5, gain: 121, instrument: "ZWO ASI533MC Pro", telescope: "", focalLength: -1);
            var light = Light(60, -5, gain: 121, instrument: "ZWO ASI533MC Pro", telescope: "Askar", focalLength: 400);

            CalibrationResolver.BestFlat([flatNoScope], light).ShouldBe(flatNoScope);
        }
    }
}
