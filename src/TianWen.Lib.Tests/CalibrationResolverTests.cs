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
            string instrument = "TestCam", string telescope = "T", int focalLength = 135, bool isMaster = false,
            DateTimeOffset? when = null, Filter? filter = null)
        {
            var meta = new ImageMeta(
                Instrument: instrument,
                ExposureStartTime: when ?? new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
                ExposureDuration: TimeSpan.FromSeconds(expoSec),
                FrameType: type,
                Telescope: telescope,
                PixelSizeX: 3.76f,
                PixelSizeY: 3.76f,
                FocalLength: focalLength,
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
                Offset: 25)
            { IsMaster = isMaster };
            return new FrameInfo("x.fits", 100, 100, 1, BitDepth.Int16, meta);
        }

        private static CalibrationResolver.CalGroup Group(FrameType type, double expoSec, float tempC, short gain = 100,
            string instrument = "TestCam", string telescope = "T", int focalLength = 135, int frameCount = 2, bool isMaster = false,
            DateTimeOffset? when = null, Filter? filter = null)
        {
            var f = Cal(type, expoSec, tempC, gain, instrument, telescope, focalLength, isMaster, when, filter);
            // Default 2 frames = buildable (a raw master needs >= 2); pass frameCount: 1 to model an
            // unbuildable singleton, or isMaster: true for a foreign master (a single frame IS
            // buildable -- loaded directly). The frames' content is irrelevant to Best* (they read
            // Key + Train + the master flag). EpochStart mirrors what GroupCalibration stamps, so
            // the time term scores the same here as in production.
            var frames = Enumerable.Repeat(f, frameCount).ToImmutableArray();
            return new(MasterGroupKey.FromFrame(f), CalibrationResolver.CalTrain.ForFrame(f), frames, isMaster,
                EpochStart: f.Meta.ExposureStartTime, EpochEnd: f.Meta.ExposureStartTime);
        }

        private static FrameInfo Light(double expoSec, float tempC, short gain,
            string instrument = "TestCam", string telescope = "T", int focalLength = 135, DateTimeOffset? when = null,
            Filter? filter = null)
            => Cal(FrameType.Light, expoSec, tempC, gain, instrument, telescope, focalLength, when: when, filter: filter);

        [Fact]
        public void GroupCalibration_SplitsAReShotLibraryIntoEpochs()
        {
            // The same sensor config shot in 2021 and again in 2026 must land in TWO groups: one
            // master per epoch, so the blend (which attenuated recently-emerged defects and was
            // invisible behind a single representative DATE-OBS) is structurally impossible. The
            // suffix is minted only for configs that actually split, so the single-epoch flat
            // keeps its legacy (empty) suffix and its existing cache path.
            var frames = new List<FrameInfo>
            {
                Cal(FrameType.Dark, 60, -10, when: new DateTimeOffset(2021, 9, 27, 0, 0, 0, TimeSpan.Zero)),
                Cal(FrameType.Dark, 60, -10, when: new DateTimeOffset(2021, 9, 28, 0, 0, 0, TimeSpan.Zero)),
                Cal(FrameType.Dark, 60, -10, when: new DateTimeOffset(2026, 1, 29, 0, 0, 0, TimeSpan.Zero)),
                Cal(FrameType.Dark, 60, -10, when: new DateTimeOffset(2026, 1, 30, 0, 0, 0, TimeSpan.Zero)),
                Cal(FrameType.Flat, 3, -10),
                Cal(FrameType.Flat, 3, -10),
            };

            var groups = CalibrationResolver.GroupCalibration(frames);

            var darks = groups[FrameType.Dark];
            darks.Count.ShouldBe(2, "2021 and 2026 shoots of one config are separate epochs");
            var early = darks.Single(g => g.EpochStart.Year == 2021);
            var late = darks.Single(g => g.EpochStart.Year == 2026);
            early.Frames.Length.ShouldBe(2);
            late.Frames.Length.ShouldBe(2);
            early.EpochSuffix.ShouldBe("_e20210927");
            late.EpochSuffix.ShouldBe("_e20260129");
            early.EpochEnd.ShouldBe(new DateTimeOffset(2021, 9, 28, 0, 0, 0, TimeSpan.Zero));

            groups[FrameType.Flat].Single().EpochSuffix.ShouldBe("", "a single-epoch config keeps its legacy cache path");
        }

        [Fact]
        public void BestDark_NearestEpochWins_WhenThePhysicsTie()
        {
            // Two epochs of the SAME library (identical exposure/temp/gain) is exactly the choice
            // the time term exists to decide: the lights' contemporary epoch wins, in either input
            // order (no enumeration-order luck).
            var early = Group(FrameType.Dark, 60, -5, gain: 121, when: new DateTimeOffset(2021, 9, 27, 0, 0, 0, TimeSpan.Zero));
            var late = Group(FrameType.Dark, 60, -5, gain: 121, when: new DateTimeOffset(2026, 1, 29, 0, 0, 0, TimeSpan.Zero));
            var light = Light(60, -5, gain: 121, when: new DateTimeOffset(2026, 2, 10, 0, 0, 0, TimeSpan.Zero));

            CalibrationResolver.BestDark([early, late], light).ShouldBe(late);
            CalibrationResolver.BestDark([late, early], light).ShouldBe(late);
        }

        [Fact]
        public void BestDark_TimeIsATieBreaker_NeverAPhysicalAxis()
        {
            // Staleness costs ~80 defect px/year (~1.8% of the stable defect set over 4.3 years);
            // 1 C of temperature error mis-subtracts dark current by ~12%. So a four-year-old
            // exact-temperature library must beat a fresh library 1 C off -- the time penalty is
            // sized to lose to a single degree, and this pins that sizing.
            var oldExactTemp = Group(FrameType.Dark, 60, -5, gain: 121, when: new DateTimeOffset(2022, 1, 1, 0, 0, 0, TimeSpan.Zero));
            var freshTempOff = Group(FrameType.Dark, 60, -6, gain: 121, when: new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero));
            var light = Light(60, -5, gain: 121, when: new DateTimeOffset(2026, 2, 10, 0, 0, 0, TimeSpan.Zero));

            CalibrationResolver.BestDark([freshTempOff, oldExactTemp], light).ShouldBe(oldExactTemp);
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

        [Fact]
        public void BestDark_SameGainWins_OverIdenticalTempAndExposureAtWrongGain()
        {
            // Gain participates in the dark score: a wrong-gain dark mis-scales the fixed pattern
            // that dark subtraction removes for N2N independence, so when a same-gain library
            // exists it must win: regardless of input order.
            var wrongGain = Group(FrameType.Dark, 60, -5, gain: 212);
            var sameGain = Group(FrameType.Dark, 60, -5, gain: 121);
            var light = Light(60, -5, gain: 121);

            CalibrationResolver.BestDark([wrongGain, sameGain], light).ShouldBe(sameGain);
            CalibrationResolver.BestDark([sameGain, wrongGain], light).ShouldBe(sameGain);
        }

        [Fact]
        public void BestDark_WrongGainMatchedExposureAndTemp_BeatsWarmShortSameGainDark_OnlyInLenientMode()
        {
            // The real-archive trade-off (2026: g121/60s/-5C lights, only a g212 60s/-5C library
            // and g121 4.5s/+22C flat-wizard darks exist). In LENIENT mode (requireGainMatch:
            // false, the pre-2026-08-17 default) the matched-exposure/temperature dark is the
            // better of two bad options even at the wrong gain -- this pins the penalty sizing.
            // Under the strict DEFAULT the same archive resolves NO dark at all: the wrong-gain
            // dark is hard-rejected (its residual fixed pattern is correlated between both subs of
            // an N2N pair, the exact independence violation) and the warm short dark fails the
            // exposure gate, so the session is uncalibrated rather than silently mis-calibrated.
            var wrongGainRightDark = Group(FrameType.Dark, 60, -5, gain: 212);
            var sameGainUselessDark = Group(FrameType.Dark, 4.5, 22, gain: 121);
            var light = Light(60, -5, gain: 121);

            CalibrationResolver.BestDark([sameGainUselessDark, wrongGainRightDark], light, requireGainMatch: false)
                .ShouldBe(wrongGainRightDark);
            CalibrationResolver.BestDark([sameGainUselessDark, wrongGainRightDark], light).ShouldBeNull();
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
            // light-dark. In LENIENT gain mode the matched-exposure dark wins even at the wrong gain
            // (which is still the stack pipeline's behaviour -- its MatchMaster never consults gain,
            // see task #25); under the strict DEFAULT both candidates fall (exposure gate, gain gate)
            // and the session resolves no dark rather than a wrong one of either kind.
            var darkFlat = Group(FrameType.Dark, 6.68, -5, gain: 121);      // same gain+temp, ~9x too short
            var matchedExposure = Group(FrameType.Dark, 60, -5, gain: 212); // right exposure+temp, wrong gain
            var light = Light(60, -5, gain: 121);

            CalibrationResolver.BestDark([darkFlat, matchedExposure], light, requireGainMatch: false)
                .ShouldBe(matchedExposure);
            CalibrationResolver.BestDark([darkFlat, matchedExposure], light).ShouldBeNull();
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
        public void BestFlatPedestal_OnlyDarkFlatsExist_IsUsed_ClosingTheNoneGap()
        {
            // The gap this closes: a session shot with dark-flats and no bias library (the standard
            // CMOS capture workflow) used to get "flat pedestal: NONE" and the ~2% vignetting
            // under-correction the MasterFrameBuilder tests quantify.
            var darkFlat = Group(FrameType.DarkFlat, 1.09, -5);
            var flat = Group(FrameType.Flat, 1.09, -5);

            CalibrationResolver.BestFlatPedestal(null, [darkFlat], null, flat).ShouldBe(darkFlat);
        }

        [Fact]
        public void BestFlatPedestal_AnExposureMatchedDarkFlat_BeatsABias()
        {
            // The exposure term is the physics of the choice: a matched dark-flat also removes the
            // thermal signal the flat accumulated over its exposure, which a bias cannot (the DSS
            // model's Flat column: master dark-flat subtracted, bias only as the fallback).
            var bias = Group(FrameType.Bias, 0, -5);
            var darkFlat = Group(FrameType.DarkFlat, 10, -5);
            var flat = Group(FrameType.Flat, 10, -5);

            CalibrationResolver.BestFlatPedestal([bias], [darkFlat], null, flat).ShouldBe(darkFlat);
        }

        [Fact]
        public void BestFlatPedestal_AMismatchedDarkFlat_LosesToABias()
        {
            // A 30s dark-flat against a 1s flat would subtract 29s of thermal + amp glow the flat
            // never accumulated; the bias's own exposure gap is only the flat's 1s.
            var bias = Group(FrameType.Bias, 0, -5);
            var wrongDarkFlat = Group(FrameType.DarkFlat, 30, -5);
            var flat = Group(FrameType.Flat, 1, -5);

            CalibrationResolver.BestFlatPedestal([bias], [wrongDarkFlat], null, flat).ShouldBe(bias);
        }

        [Fact]
        public void BestFlatPedestal_WithinBiases_TemperatureStillDecides()
        {
            // Every bias carries the same exposure gap (~t_flat), so the pooled exposure term must
            // not disturb the original all-bias ordering: temperature, then gain, as before.
            var warm = Group(FrameType.Bias, 0, 5);
            var matched = Group(FrameType.Bias, 0, -5);
            var flat = Group(FrameType.Flat, 1, -5);

            CalibrationResolver.BestFlatPedestal([warm, matched], null, null, flat).ShouldBe(matched);
        }

        [Fact]
        public void BestFlatPedestal_AMislabeledShortDark_IsAcceptedAsTheDarkFlatItIs()
        {
            // The archive's flat-matched sets are written IMAGETYP=DARK by N.I.N.A. (the 4.6s/6.7s
            // "darks"), so DARK groups join the pool behind the exposure-ratio gate: a dark at the
            // flat's exposure IS a dark-flat whatever its label.
            var mislabeled = Group(FrameType.Dark, 6.68, -5);
            var flat = Group(FrameType.Flat, 6.68, -5);

            CalibrationResolver.BestFlatPedestal(null, null, [mislabeled], flat).ShouldBe(mislabeled);
        }

        [Fact]
        public void BestFlatPedestal_ARealLightDark_NeverBecomesAPedestal_EvenWhenNothingElseExists()
        {
            // Outside the ratio gate the answer is NONE, not the least-bad dark: subtracting a 60s
            // dark from a 1s flat injects 59s of thermal + amp glow, worse than the ~2%
            // under-correction of no pedestal at all.
            var lightDark = Group(FrameType.Dark, 60, -5);
            var flat = Group(FrameType.Flat, 1, -5);

            CalibrationResolver.BestFlatPedestal(null, null, [lightDark], flat).ShouldBeNull();
        }

        [Fact]
        public void BestFlatPedestal_AnExposureMatchedDarkFlat_StillBeatsABias_WhenItsTemperatureIsOff()
        {
            // The case a per-degree constant weight got backwards. Dark current doubles per ~6 C, so
            // a 1 C-off dark-flat still mis-removes only ~12% of the thermal term, against the 100%
            // a temperature-PERFECT bias leaves standing. Preferring the bias here would trade an
            // eighth of the error for all of it.
            var bias = Group(FrameType.Bias, 0, -5);
            var slightlyWarmDarkFlat = Group(FrameType.DarkFlat, 1, -4);
            var flat = Group(FrameType.Flat, 1, -5);

            CalibrationResolver.BestFlatPedestal([bias], [slightlyWarmDarkFlat], null, flat).ShouldBe(slightlyWarmDarkFlat);
        }

        [Fact]
        public void BestFlatPedestal_AWildlyWarmDarkFlat_LosesToABias_AtTheBreakEven()
        {
            // The preference is not unconditional, and this is where it inverts: 12 C is two
            // doublings, so the dark-flat subtracts ~4x the thermal signal the flat actually
            // accumulated and leaves 3x t_flat of over-subtraction, against the bias's 1x of
            // under-subtraction. Break-even sits at one doubling (~6 C).
            var bias = Group(FrameType.Bias, 0, -5);
            var wildlyWarmDarkFlat = Group(FrameType.DarkFlat, 1, 7);
            var flat = Group(FrameType.Flat, 1, -5);

            CalibrationResolver.BestFlatPedestal([bias], [wildlyWarmDarkFlat], null, flat).ShouldBe(bias);
        }

        [Fact]
        public void BestFlatPedestal_AMislabeledLongDarkFlat_IsRefusedByTheSameGateAsADark()
        {
            // The gate reads exposure, never the label, in BOTH directions. The archive proves
            // labels are unreliable (its dark-flats say DARK), so a 300s set that calls itself
            // DARKFLAT is a light-dark and is refused exactly as one -- no pedestal rather than
            // that amp glow, even with nothing else in the pool.
            var lightDarkInDisguise = Group(FrameType.DarkFlat, 300, -5);
            var flat = Group(FrameType.Flat, 1, -5);

            CalibrationResolver.BestFlatPedestal(null, [lightDarkInDisguise], null, flat).ShouldBeNull();
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
            // DID have a buildable dark. So the buildable dark must win over the score-perfect
            // singleton -- both are exposure- and gain-compatible, so the buildable filter is the only
            // discriminator left (the buildable one loses on temperature score, which must not save
            // the singleton).
            var perfectSingleton = Group(FrameType.Dark, 60, -5, gain: 121, frameCount: 1); // score 0, unbuildable
            var buildable = Group(FrameType.Dark, 60, -15, gain: 121, frameCount: 2);       // 10C off, buildable
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
        public void GroupCalibration_KeepsAForeignMasterSeparateFromRawSubsOfTheSameConfig()
        {
            // A camera's proper dark can survive only as a MASTER sitting alongside raw darks of the
            // SAME sensor config. They must NOT fold into one group (a master is loaded directly, raws
            // are medianed), so the master flag is part of the grouping key -> two distinct Dark groups.
            var frames = new List<FrameInfo>
            {
                Cal(FrameType.Dark, 60, -5, gain: 121),                    // raw
                Cal(FrameType.Dark, 60, -5, gain: 121),                    // raw (same group)
                Cal(FrameType.Dark, 60, -5, gain: 121, isMaster: true),    // master (separate group)
            };

            var groups = CalibrationResolver.GroupCalibration(frames);

            groups.TryGetValue(FrameType.Dark, out var darks).ShouldBeTrue();
            darks!.Count.ShouldBe(2);
            darks.Count(g => g.IsMaster).ShouldBe(1);
            darks.Single(g => g.IsMaster).Frames.Length.ShouldBe(1);
            darks.Single(g => !g.IsMaster).Frames.Length.ShouldBe(2);
        }

        [Fact]
        public void BestDark_SelectsSingleFrameForeignMaster_ExemptFromTheBuildableFloor()
        {
            // A foreign master is a single already-integrated frame -- exempt from the >=2 buildable
            // floor that filters raw singletons, because it is loaded directly rather than medianed. So
            // the gain-perfect master wins over a buildable wrong-gain raw dark (a raw singleton would
            // still lose -- see BestDark_SkipsUnbuildableSingleton).
            var master = Group(FrameType.Dark, 60, -5, gain: 121, frameCount: 1, isMaster: true);
            var rawWrongGain = Group(FrameType.Dark, 60, -5, gain: 212, frameCount: 2);
            var light = Light(60, -5, gain: 121);

            CalibrationResolver.BestDark([master, rawWrongGain], light).ShouldBe(master);
        }

        [Fact]
        public void BestDark_RequireGainMatch_RejectsAKnownWrongGainDark_ButKeepsSameGain()
        {
            // Strict gain: a KNOWN gain mismatch is a hard reject, not a penalty. With only a g212 dark
            // for g121 lights -> null (so RequireDarkCalibration then skips the session); add a g121
            // dark and it is chosen.
            var wrongGain = Group(FrameType.Dark, 60, -5, gain: 212);
            var light = Light(60, -5, gain: 121);

            CalibrationResolver.BestDark([wrongGain], light, requireGainMatch: true).ShouldBeNull();

            var sameGain = Group(FrameType.Dark, 60, -5, gain: 121);
            CalibrationResolver.BestDark([wrongGain, sameGain], light, requireGainMatch: true).ShouldBe(sameGain);
        }

        [Fact]
        public void BestDark_RequireGainMatch_UnknownGainStaysAWildcard()
        {
            // A header-less dark library (gain -1) must not be dropped by strict gain -- unknown is
            // lenient on either side, mirroring the optical-train comparisons.
            var unknownGain = Group(FrameType.Dark, 60, -5, gain: -1);
            var light = Light(60, -5, gain: 121);

            CalibrationResolver.BestDark([unknownGain], light, requireGainMatch: true).ShouldBe(unknownGain);
        }

        [Fact]
        public void BestDark_WithoutATemperatureLimit_ALoneBadlyMismatchedDarkStillWins()
        {
            // The behaviour the limit exists to fix, pinned so it cannot be mistaken for a bug later:
            // temperature is only a score term, and a score cannot exclude a sole candidate. A -5 C
            // dark against +12 C lights passes every hard gate (same gain, same exposure, same
            // sensor) and is returned, after which the session records as calibrated.
            var tooCold = Group(FrameType.Dark, 120, -5, gain: 120);
            var warmLight = Light(120, 12, gain: 120);

            CalibrationResolver.BestDark([tooCold], warmLight).ShouldBe(tooCold);
        }

        [Fact]
        public void BestDark_MaxTempDelta_RejectsATooDistantDark_ButKeepsOneInTolerance()
        {
            // Strict temperature: a KNOWN 17 C gap is a hard reject, not a penalty. Dark current
            // roughly doubles per 6 C, so that dark under-subtracts by about 7x and leaves a residual
            // fixed pattern CORRELATED between both subs of an N2N pair. With only that dark -> null
            // (so RequireDarkCalibration then skips the session); add one at temperature and it wins.
            var tooCold = Group(FrameType.Dark, 120, -5, gain: 120);
            var warmLight = Light(120, 12, gain: 120);

            CalibrationResolver.BestDark([tooCold], warmLight, maxTempDelta: 3.0).ShouldBeNull();

            var atTemperature = Group(FrameType.Dark, 120, 12, gain: 120);
            CalibrationResolver.BestDark([tooCold, atTemperature], warmLight, maxTempDelta: 3.0).ShouldBe(atTemperature);
        }

        [Fact]
        public void BestDark_MaxTempDelta_IsInclusiveAtTheLimit()
        {
            // Exactly at the tolerance is IN, matching how the calibration map reports "within 1.0 C".
            var atLimit = Group(FrameType.Dark, 120, -8, gain: 120);
            var light = Light(120, -5, gain: 120);

            CalibrationResolver.BestDark([atLimit], light, maxTempDelta: 3.0).ShouldBe(atLimit);
            CalibrationResolver.BestDark([atLimit], light, maxTempDelta: 2.9).ShouldBeNull();
        }

        [Fact]
        public void BestDark_MaxTempDelta_UnknownTemperatureStaysAWildcard()
        {
            // Mirrors the unknown-gain rule: a header-less library must not be silently dropped by a
            // gate it cannot answer. A missing CCD-TEMP reaches MasterGroupKey as NaN -> null.
            var unknownTemp = Group(FrameType.Dark, 120, float.NaN, gain: 120);
            var light = Light(120, -5, gain: 120);

            CalibrationResolver.BestDark([unknownTemp], light, maxTempDelta: 1.0).ShouldBe(unknownTemp);
        }

        [Fact]
        public void BestFlat_UnknownTelescopeOnEitherSide_IsAWildcard_NotADrop()
        {
            // A missing TELESCOP/FOCALLEN header must not wrongly drop an otherwise-matching flat
            // (same camera) -- unknown fields are lenient, only two KNOWN differing values reject.
            // Lenient is not proven, though: this flat is kept because it was shot with the lights
            // (both default to the same instant here); the tests below take the date away.
            var flatNoScope = Group(FrameType.Flat, 3, -5, gain: 121, instrument: "ZWO ASI533MC Pro", telescope: "", focalLength: -1);
            var light = Light(60, -5, gain: 121, instrument: "ZWO ASI533MC Pro", telescope: "Askar", focalLength: 400);

            CalibrationResolver.BestFlat([flatNoScope], light).ShouldBe(flatNoScope);
        }

        private static DateTimeOffset Utc(int year, int month, int day, int hour, int minute)
            => new DateTimeOffset(year, month, day, hour, minute, 0, TimeSpan.Zero);

        [Fact]
        public void BestFlat_AFlatWithNoTrainCards_FromAnotherMonth_IsRefused_TheBorrowedLensFlat()
        {
            // The 2026-09-16-flatfloor bake, verbatim: 49 SharpCap lights through a 24 mm lens
            // (FOCALLEN=24, no TELESCOP) and three SharpCap flat sets on the same body, none of which
            // carries TELESCOP or FOCALLEN. The wildcard handed the session the 289 mm ZS61 flat
            // shot 103 days earlier through a different filter, on temperature alone; the calibration
            // map says this session has no flat of its own and must not borrow one.
            const string Camera = "ZWO ASI585MC Pro";
            var lEnhance369 = Group(FrameType.Flat, 0.1, -11, gain: 252, instrument: Camera, telescope: "", focalLength: -1, when: Utc(2024, 9, 28, 9, 54));
            var zs61Broadband = Group(FrameType.Flat, 0.5, -10, gain: 252, instrument: Camera, telescope: "", focalLength: -1, when: Utc(2024, 10, 3, 10, 21));
            var velaLens = Group(FrameType.Flat, 0.25, 17, gain: 252, instrument: Camera, telescope: "", focalLength: -1, when: Utc(2025, 2, 1, 0, 18));
            var light = Light(60, -10, gain: 252, instrument: Camera, telescope: "", focalLength: 24, when: Utc(2025, 1, 14, 12, 5));

            CalibrationResolver.BestFlat([lEnhance369, zs61Broadband, velaLens], light).ShouldBeNull();
        }

        [Fact]
        public void BestFlat_AmongFlatsWithNoTrainCards_TheOneShotWithTheLightsWins_NotTheCloserTemperature()
        {
            // The same bake: N.I.N.A. Luminance lights (FMA180 @ 180 mm, -10 C) and two SharpCap flat
            // sets on the same FMA180 with no train or filter cards, the Ha set 12 days before at
            // +10 C and the Luminance set the next day at +20 C. Temperature picked the Ha flat. With
            // nothing in the cards to tell the two apart, being shot with the lights is the evidence.
            // Both lie inside the window, so this pins the ordering, not the window.
            const string Camera = "ZWO ASI1600MM Pro";
            var haFlat = Group(FrameType.Flat, 0.5, 10, gain: 139, instrument: Camera, telescope: "", focalLength: -1, when: Utc(2025, 2, 8, 11, 1));
            var lumFlat = Group(FrameType.Flat, 0.0625, 20, gain: 139, instrument: Camera, telescope: "", focalLength: -1, when: Utc(2025, 2, 21, 22, 35));
            var light = Light(60, -10, gain: 139, instrument: Camera, telescope: "FMA180", focalLength: 180, when: Utc(2025, 2, 20, 11, 51));

            CalibrationResolver.BestFlat([haFlat, lumFlat], light).ShouldBe(lumFlat);
            CalibrationResolver.BestFlat([lumFlat, haFlat], light).ShouldBe(lumFlat);
        }

        [Fact]
        public void BestFlat_AmongFlatsWithNoTrainCards_NearestInTimeWins_EvenInsideTheWindow()
        {
            // Two unproven flats both inside the window: the SMC L-eNhance session (368.8 mm) with
            // its own flat 1.00 day later at -11 C, and the 289 mm broadband flat 6.01 days away at
            // the lights' exact temperature, so only the ordering can refuse it.
            const string Camera = "ZWO ASI585MC Pro";
            var own = Group(FrameType.Flat, 0.1, -11, gain: 252, instrument: Camera, telescope: "", focalLength: -1, when: Utc(2024, 9, 28, 9, 54));
            var otherTrain = Group(FrameType.Flat, 0.5, -10, gain: 252, instrument: Camera, telescope: "", focalLength: -1, when: Utc(2024, 10, 3, 10, 21));
            var light = Light(120, -10, gain: 252, instrument: Camera, telescope: "", focalLength: 369, when: Utc(2024, 9, 27, 10, 1));

            CalibrationResolver.BestFlat([otherTrain, own], light).ShouldBe(own);
            CalibrationResolver.BestFlat([own, otherTrain], light).ShouldBe(own);
        }

        [Fact]
        public void BestFlat_AFlatWithNoTrainCards_IsAcceptedAtTheWindow_AndRefusedJustPastIt()
        {
            var light = Light(60, -5, gain: 121, instrument: "ZWO ASI533MC Pro", telescope: "Askar", focalLength: 400, when: Utc(2026, 1, 10, 12, 0));
            var window = TimeSpan.FromDays(CalibrationResolver.UnprovenFlatMaxDays);
            var atLimit = Group(FrameType.Flat, 3, -5, gain: 121, instrument: "ZWO ASI533MC Pro", telescope: "", focalLength: -1,
                when: light.Meta.ExposureStartTime + window);
            var pastLimit = Group(FrameType.Flat, 3, -5, gain: 121, instrument: "ZWO ASI533MC Pro", telescope: "", focalLength: -1,
                when: light.Meta.ExposureStartTime - window - TimeSpan.FromMinutes(1));

            CalibrationResolver.BestFlat([atLimit], light).ShouldBe(atLimit);
            CalibrationResolver.BestFlat([pastLimit], light).ShouldBeNull();
        }

        [Fact]
        public void BestFlat_AFlatWhoseCardsProveTheTrain_NeedsNoWindow()
        {
            // A focal length known on BOTH sides and agreeing is proof, whatever the telescope card
            // says on either side: a SharpCap light (FOCALLEN only) against a N.I.N.A. flat 40 days
            // away is the same train by its cards, exactly as before this rule existed.
            var flat = Group(FrameType.Flat, 3, -5, gain: 121, instrument: "ZWO ASI1600MM Pro", telescope: "FMA180", focalLength: 180, when: Utc(2025, 1, 1, 0, 0));
            var light = Light(60, -5, gain: 121, instrument: "ZWO ASI1600MM Pro", telescope: "", focalLength: 180, when: Utc(2025, 2, 10, 0, 0));

            CalibrationResolver.BestFlat([flat], light).ShouldBe(flat);
        }

        [Fact]
        public void BestFlat_AProvenFlat_BeatsAnUnprovenOneShotTheSameNight()
        {
            // Cards that agree are proof; a date is circumstantial. When both kinds exist the proven
            // flat wins, because a wrong flat is worse than a stale right one.
            const string Camera = "ZWO ASI533MC Pro";
            var light = Light(60, -5, gain: 121, instrument: Camera, telescope: "Samyang 135", focalLength: 130, when: Utc(2026, 1, 10, 12, 0));
            var proven = Group(FrameType.Flat, 4.61, -5, gain: 121, instrument: Camera, telescope: "Samyang 135", focalLength: 130, when: Utc(2025, 12, 20, 0, 0));
            var unproven = Group(FrameType.Flat, 3, -5, gain: 121, instrument: Camera, telescope: "", focalLength: -1, when: Utc(2026, 1, 10, 20, 0));

            CalibrationResolver.BestFlat([unproven, proven], light).ShouldBe(proven);
            CalibrationResolver.BestFlat([proven, unproven], light).ShouldBe(proven);
        }

        [Fact]
        public void BestFlat_AProvenFlatOfAnotherFilter_NeverOutranksTheLightsOwnSameNightFlat()
        {
            // Astro-Unsorted, Rosette 2024-12-29: SharpCap lights on the Samyang 135 (FOCALLEN 129.5,
            // no FILTER) and their own SharpCap flat the next morning (no cards at all), beside the
            // N.I.N.A. L-Ultimate 3nm flat on the same lens a year later, whose FOCALLEN proves the
            // lens and whose FILTER card names a narrowband filter the lights do not state. Ranking
            // proof before the filter handed the session the narrowband flat.
            const string Camera = "ZWO ASI533MC Pro";
            var light = Light(120, -5, gain: 121, instrument: Camera, telescope: "", focalLength: 130, when: Utc(2024, 12, 29, 11, 24));
            var ownFlat = Group(FrameType.Flat, 1, 20, gain: 121, instrument: Camera, telescope: "", focalLength: -1, when: Utc(2024, 12, 30, 8, 40));
            var narrowband = Group(FrameType.Flat, 4.61, -5, gain: 121, instrument: Camera, telescope: "Samyang 135 f/2 ED",
                focalLength: 130, when: Utc(2026, 1, 21, 0, 0), filter: Filter.FromName("Optolong L-Ultimate 3nm"));
            MasterGroupKey.FromFrame(narrowband.Frames[0]).SameFilterAs(MasterGroupKey.FromFrame(light)).ShouldBeFalse(
                "the fixture must state a filter the lights do not, or this test proves nothing");

            CalibrationResolver.BestFlat([narrowband, ownFlat], light).ShouldBe(ownFlat);
            CalibrationResolver.BestFlat([ownFlat, narrowband], light).ShouldBe(ownFlat);
        }

        [Fact]
        public void BestFlat_AFlatThatStatesNoFilter_OutranksOneThatStatesAnother()
        {
            // Astro-Unsorted, Rim Nebula 2024-06-06: SharpCap lights whose filter ("LPS") comes from a
            // sidecar, their own "Cal Jun" SharpCap flat 40 minutes earlier with no filter and no
            // train cards, and the N.I.N.A. L-Ultimate 3nm flat on the same lens 575 days later.
            // Scoring "states no filter" the same as "states a different filter" left the proven
            // tier to decide, and it handed the session the narrowband flat.
            const string Camera = "ZWO ASI533MC Pro";
            var light = Light(120, 8, gain: 121, instrument: Camera, telescope: "", focalLength: 130,
                when: Utc(2024, 6, 6, 9, 15), filter: Filter.FromName("LPS"));
            var ownFlat = Group(FrameType.Flat, 0.1, 15, gain: 121, instrument: Camera, telescope: "", focalLength: -1,
                when: Utc(2024, 6, 6, 8, 36));
            var narrowband = Group(FrameType.Flat, 4.46, 21, gain: 121, instrument: Camera, telescope: "Samyang 135 f/2 ED",
                focalLength: 130, when: Utc(2025, 12, 2, 0, 0), filter: Filter.FromName("Optolong L-Ultimate 3nm"));

            CalibrationResolver.BestFlat([narrowband, ownFlat], light).ShouldBe(ownFlat);
            CalibrationResolver.BestFlat([ownFlat, narrowband], light).ShouldBe(ownFlat);
        }

        [Fact]
        public void BestFlat_ALightWithNoTrainCards_CannotProveAFlatEither()
        {
            // Proof needs an optics card on BOTH sides, so a light that states no train is judged by
            // date against a flat that states one, the mirror of the SharpCap-flat case.
            var flat = Group(FrameType.Flat, 3, -5, gain: 121, instrument: "ZWO ASI533MC Pro", telescope: "Askar", focalLength: 400, when: Utc(2025, 6, 1, 0, 0));
            var light = Light(60, -5, gain: 121, instrument: "ZWO ASI533MC Pro", telescope: "", focalLength: -1, when: Utc(2026, 1, 10, 0, 0));

            CalibrationResolver.BestFlat([flat], light).ShouldBeNull();
        }

        [Fact]
        public void BestFlat_AnUnprovenFlat_WithNoDateOnEitherSide_IsRefused()
        {
            // No card and no date is no evidence at all.
            var undatedFlat = Group(FrameType.Flat, 3, -5, gain: 121, instrument: "ZWO ASI533MC Pro", telescope: "", focalLength: -1, when: default(DateTimeOffset));
            var light = Light(60, -5, gain: 121, instrument: "ZWO ASI533MC Pro", telescope: "Askar", focalLength: 400, when: Utc(2026, 1, 10, 0, 0));

            CalibrationResolver.BestFlat([undatedFlat], light).ShouldBeNull();
        }

        [Fact]
        public void GroupCalibration_KeepsADriftingRunWhole_SoTwoFramesOfItCannotWinOnTheLightsDegree()
        {
            // #307 #96, the 294MC Orion M42 night of 2021-11-28: an uncooled 240 s dark run that
            // drifted across several degrees, keyed by the degree, became one group per degree, and
            // the matcher took the 2-frame group that sat on the lights' degree. As ONE run it builds
            // one master from every frame.
            var night = Utc(2021, 12, 29, 12, 0);
            var frames = new List<FrameInfo>();
            for (var i = 0; i < 50; i++)
            {
                frames.Add(Cal(FrameType.Dark, 240, 17.9f + (0.056f * i), gain: 120, when: night + TimeSpan.FromMinutes(4 * i)));
            }
            var light = Light(240, 18.3f, gain: 120, when: Utc(2021, 11, 28, 12, 0));

            var darks = CalibrationResolver.GroupCalibration(frames)[FrameType.Dark];

            var dark = darks.ShouldHaveSingleItem();
            dark.Frames.Length.ShouldBe(50);
            CalibrationResolver.BestDark(darks, light).ShouldBe(dark);
        }

        [Fact]
        public void GroupCalibration_ACoolersSettlingFrames_AreNotALibraryOfTheirOwn()
        {
            // The ASI585's 2025-08-09 library: 74 frames at -10.0 C and two at -9.4 C. Keyed by the
            // degree, those two were a library, and six sessions whose lights sat nearer -9 took it.
            var frames = new List<FrameInfo>();
            for (var i = 0; i < 74; i++)
            {
                frames.Add(Cal(FrameType.Dark, 60, -10f, gain: 252, when: Utc(2025, 8, 10, 1, 45) + TimeSpan.FromMinutes(i)));
            }
            frames.Add(Cal(FrameType.Dark, 60, -9.4f, gain: 252, when: Utc(2025, 8, 10, 1, 41)));
            frames.Add(Cal(FrameType.Dark, 60, -9.4f, gain: 252, when: Utc(2025, 8, 10, 3, 11)));
            var warmLight = Light(60, 6f, gain: 252, when: Utc(2025, 1, 30, 12, 0));

            var darks = CalibrationResolver.GroupCalibration(frames)[FrameType.Dark];

            var library = darks.ShouldHaveSingleItem();
            library.Frames.Length.ShouldBe(76);
            library.Key.TemperatureC.ShouldBe(-10);
            CalibrationResolver.BestDark(darks, warmLight)!.Frames.Length.ShouldBe(76);
        }

        [Fact]
        public void GroupCalibration_AFlatRunWhoseExposureJitters_IsOneSet_AndAFlatWizardsProbesAreNot()
        {
            // The SV605CC's 2025-12-20 L-Quad run: 46 flats at 0.47768 s and 3 at 0.47808 s, a
            // flat wizard settling. Compared exactly, that was two groups slugged alike, and two
            // sessions got the 3-frame one. The ASI533's 2026-04-22 run beside it: 50 at 6.68164 s and
            // one probe each at 0.20507 s and 16.37665 s, which must stay out of it.
            var frames = new List<FrameInfo>();
            for (var i = 0; i < 46; i++) frames.Add(Cal(FrameType.Flat, 0.47768, -5f, gain: 120, when: Utc(2025, 12, 20, 9, 0)));
            for (var i = 0; i < 3; i++) frames.Add(Cal(FrameType.Flat, 0.47808, -5f, gain: 120, when: Utc(2025, 12, 20, 9, 1)));
            for (var i = 0; i < 50; i++) frames.Add(Cal(FrameType.Flat, 6.68164, -5f, gain: 121, when: Utc(2026, 4, 22, 9, 0)));
            frames.Add(Cal(FrameType.Flat, 0.20507, -5f, gain: 121, when: Utc(2026, 4, 22, 8, 58)));
            frames.Add(Cal(FrameType.Flat, 16.37665, -5f, gain: 121, when: Utc(2026, 4, 22, 8, 59)));

            var flats = CalibrationResolver.GroupCalibration(frames)[FrameType.Flat];

            flats.Select(g => g.Frames.Length).OrderBy(n => n).ShouldBe([1, 1, 49, 50]);
        }

        [Fact]
        public void GroupCalibration_ADarksExposureStaysExact()
        {
            // Dark scaling reads a dark's exposure, so only a flat's is rounded.
            var frames = new List<FrameInfo>
            {
                Cal(FrameType.Dark, 60.0, -10f), Cal(FrameType.Dark, 60.0, -10f),
                Cal(FrameType.Dark, 60.02, -10f), Cal(FrameType.Dark, 60.02, -10f),
            };

            CalibrationResolver.GroupCalibration(frames)[FrameType.Dark].Count.ShouldBe(2);
        }

        [Fact]
        public void SessionKey_IsTheLightsMedianTemperature_NotTheFirstLights()
        {
            // Lagoon 2025-05-25: the first light read -9.4 C and the rest -10.0, so a session keyed on
            // its first light alone was a -9 C session and chose the dark on that degree.
            var lights = new List<FrameInfo> { Light(60, -9.4f, gain: 252) };
            for (var i = 0; i < 59; i++)
            {
                lights.Add(Light(60, -10f, gain: 252));
            }
            var atMinus9 = Group(FrameType.Dark, 60, -9, gain: 252);
            var atMinus10 = Group(FrameType.Dark, 60, -10, gain: 252);

            CalibrationResolver.BestDark([atMinus9, atMinus10], lights[0]).ShouldBe(atMinus9, "keyed on the first light alone");

            var sessionKey = CalibrationResolver.SessionKey(lights);
            sessionKey.TemperatureC.ShouldBe(-10);
            CalibrationResolver.BestDark([atMinus9, atMinus10], lights[0], lightKey: sessionKey).ShouldBe(atMinus10);
            CalibrationResolver.BestDark([atMinus10, atMinus9], lights[0], lightKey: sessionKey).ShouldBe(atMinus10);
        }

        [Fact]
        public void BestFlat_AmongCardProvenFlats_TheNightWins_NotTheOneShotCold()
        {
            // The ASI533's L-Ultimate flats: every set carries the same N.I.N.A. train and filter cards,
            // every one but 2026-01-21 was shot warm (cooler off), and at 10 per degree that one cold
            // set calibrated eighteen sessions up to 35 days away over their own. A flat's temperature
            // says nothing about its dust; the days between it and the lights do.
            const string Camera = "ZWO ASI533MC Pro";
            var lUltimate = Filter.FromName("Optolong L-Ultimate 3nm");
            var light = Light(60, -5.1f, gain: 121, instrument: Camera, telescope: "Samyang 135 f", focalLength: 130,
                when: Utc(2026, 1, 18, 11, 0), filter: lUltimate);
            var ownWarm = Group(FrameType.Flat, 4.6, 24, gain: 121, instrument: Camera, telescope: "Samyang 135 f", focalLength: 130,
                when: Utc(2026, 1, 18, 21, 30), filter: lUltimate);
            var otherCold = Group(FrameType.Flat, 4.61, -5.1f, gain: 121, instrument: Camera, telescope: "Samyang 135 f", focalLength: 130,
                when: Utc(2026, 1, 21, 21, 29), filter: lUltimate);

            CalibrationResolver.BestFlat([otherCold, ownWarm], light).ShouldBe(ownWarm);
            CalibrationResolver.BestFlat([ownWarm, otherCold], light).ShouldBe(ownWarm);
        }

        [Fact]
        public void BestFlat_ASameNightFlat_BeatsAColdFlatMonthsAway_WhenNoCardNamesAFilter()
        {
            // The SY135 nights of 2022-12-24: SharpCap lights and their own flats that evening (21.6 C,
            // cooler off), and an IDAS-LPS-D3 set on the same lens 265 days later at 4.9 C. No card on
            // any of them names the filter, so temperature chose the LPS-D3 flat for UV-IR-Cut lights.
            const string Camera = "ZWO ASI533MC Pro";
            var light = Light(120, -10, gain: 121, instrument: Camera, telescope: "", focalLength: 135, when: Utc(2022, 12, 24, 11, 59));
            var sameNight = Group(FrameType.Flat, 0.113, 21.6f, gain: 121, instrument: Camera, telescope: "", focalLength: 135,
                when: Utc(2022, 12, 24, 11, 18));
            var monthsLater = Group(FrameType.Flat, 0.033, 4.9f, gain: 121, instrument: Camera, telescope: "", focalLength: 135,
                when: Utc(2023, 9, 15, 21, 49));

            CalibrationResolver.BestFlat([monthsLater, sameNight], light).ShouldBe(sameNight);
            CalibrationResolver.BestFlat([sameNight, monthsLater], light).ShouldBe(sameNight);
        }
    }
}
