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
            string instrument = "TestCam", string telescope = "T", int focalLength = 135, bool isMaster = false)
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
                Offset: 25)
            { IsMaster = isMaster };
            return new FrameInfo("x.fits", 100, 100, 1, BitDepth.Int16, meta);
        }

        private static CalibrationResolver.CalGroup Group(FrameType type, double expoSec, float tempC, short gain = 100,
            string instrument = "TestCam", string telescope = "T", int focalLength = 135, int frameCount = 2, bool isMaster = false)
        {
            var f = Cal(type, expoSec, tempC, gain, instrument, telescope, focalLength, isMaster);
            // Default 2 frames = buildable (a raw master needs >= 2); pass frameCount: 1 to model an
            // unbuildable singleton, or isMaster: true for a foreign master (a single frame IS
            // buildable -- loaded directly). The frames' content is irrelevant to Best* (they read
            // Key + Train + the master flag).
            var frames = Enumerable.Repeat(f, frameCount).ToImmutableArray();
            return new(MasterGroupKey.FromFrame(f), CalibrationResolver.CalTrain.ForFrame(f), frames, isMaster);
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
            var flatNoScope = Group(FrameType.Flat, 3, -5, gain: 121, instrument: "ZWO ASI533MC Pro", telescope: "", focalLength: -1);
            var light = Light(60, -5, gain: 121, instrument: "ZWO ASI533MC Pro", telescope: "Askar", focalLength: 400);

            CalibrationResolver.BestFlat([flatNoScope], light).ShouldBe(flatNoScope);
        }
    }
}
