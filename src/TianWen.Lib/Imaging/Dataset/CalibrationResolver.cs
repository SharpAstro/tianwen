using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Imaging.Calibration;

namespace TianWen.Lib.Imaging.Dataset;

/// <summary>
/// Resolves the calibration (bias/dark/flat) for a dataset session by <b>archive-wide header
/// match</b>, never by session folder (docs/plans/ai-denoise-deconv.md §2.4: dark/bias libraries are
/// shared across sessions). Calibration frames are grouped by the stacker's <see cref="MasterGroupKey"/>
/// identity; a session's representative light is matched to the best-fitting dark + flat group, and the
/// masters are built once + cached via <see cref="MasterCache"/> (build-once holds across all sessions
/// and re-runs). The assembled <see cref="Calibrator"/> carries <b>no bias</b> — a matched-exposure
/// master dark built from raw darks already contains the bias signal, so subtracting both would
/// double-subtract the pedestal (same reasoning as <c>StackingPipeline</c>).
///
/// <para>Dark subtraction is the load-bearing step for the dataset: dark current is a FIXED pattern
/// that would be identical in two subs of an N2N pair and violate the noise-independence assumption,
/// so an uncalibrated pair is not a valid N2N sample. Flats (vignetting/dust) are a lesser concern for
/// denoise and are applied when available.</para>
/// </summary>
public static class CalibrationResolver
{
    /// <summary>A set of calibration frames that combine into one master, scoped both to their
    /// sensor-config <see cref="MasterGroupKey"/> and to the optical train (<see cref="CalTrain"/>)
    /// they were captured through. <paramref name="IsMaster"/> marks a group of already-integrated
    /// FOREIGN masters (IMAGETYP=MASTER*): such a group is served by loading the master file directly
    /// (a single frame is enough — no &gt;=2-raw median), whereas a raw group builds its master by
    /// combination. Raw and master frames of the same sensor-config + train are kept in SEPARATE
    /// groups (the flag is part of the grouping key), so a camera whose dark library survives only as
    /// a master still resolves while raw libraries build normally.</summary>
    public sealed record CalGroup(MasterGroupKey Key, CalTrain Train, ImmutableArray<FrameInfo> Frames, bool IsMaster = false);

    /// <summary>
    /// Optical-train identity a light and its calibration must share, ON TOP of the sensor-config
    /// <see cref="MasterGroupKey"/>. The dataset builder is the one context that scans a whole
    /// MULTI-camera archive in a single pass, so -- unlike the single-capture stacker, which only
    /// ever sees one camera -- it can be handed two bodies that share a sensor model (e.g. an IMX533
    /// in both a ZWO ASI533MC Pro and an SVBONY SV605CC): identical dimensions + Bayer pattern, yet
    /// their darks (amp glow + unit-to-unit fixed pattern) and especially their flats (vignetting +
    /// dust of a DIFFERENT scope) are NOT interchangeable. Darks/bias are scoped to the CAMERA (the
    /// sensor sees no optics); flats to the full optical train (camera + telescope + focal length) --
    /// which is also the grain at which the PSF / field-radius profile is characterised, since a
    /// Newtonian's coma grows with field radius while a refractor's does not, so their profiles must
    /// never be merged. Comparisons treat an unknown (empty / non-positive) field as a WILDCARD, so a
    /// missing FITS header never wrongly drops otherwise-matching calibration.
    /// </summary>
    public readonly record struct CalTrain(string Instrument, string Telescope, int FocalLength)
    {
        /// <summary>Camera-only scope: darks, bias, dark-flats -- captured with no light path, so the
        /// telescope + focal length are irrelevant to the fixed pattern they carry.</summary>
        public static CalTrain Camera(FrameInfo frame) => new(Norm(frame.Meta.Instrument), "", -1);

        /// <summary>Full optical-train scope: flats + the PSF field-radius profile (vignetting, dust,
        /// and off-axis aberrations are all optics-specific).</summary>
        public static CalTrain OpticalTrain(FrameInfo frame) =>
            new(Norm(frame.Meta.Instrument), Norm(frame.Meta.Telescope), frame.Meta.FocalLength);

        /// <summary>The scope a calibration frame was captured through: a flat sees the whole optical
        /// train, every other calibration type (dark / bias / dark-flat) only the camera.</summary>
        public static CalTrain ForFrame(FrameInfo frame) =>
            frame.FrameType is FrameType.Flat ? OpticalTrain(frame) : Camera(frame);

        /// <summary>Two KNOWN instruments that differ is a hard mismatch (the cross-camera bug); an
        /// unknown instrument on either side is a wildcard. The gate a dark/bias must pass.</summary>
        public bool CameraCompatibleWith(CalTrain light) => !KnownAndDiffer(Instrument, light.Instrument);

        /// <summary>Same camera AND telescope AND focal length (each lenient on unknown) -- the gate a
        /// flat must pass against its light so one scope's vignetting/dust is never borrowed for another.</summary>
        public bool TrainCompatibleWith(CalTrain light) =>
            !KnownAndDiffer(Instrument, light.Instrument)
            && !KnownAndDiffer(Telescope, light.Telescope)
            && !KnownAndDiffer(FocalLength, light.FocalLength);

        /// <summary>Filename-safe suffix appended to the master slug so two trains that share a
        /// <see cref="MasterGroupKey"/> (same sensor/gain/temp/filter) never collide on the shared
        /// cache path or in the in-flight build map. Empty when the camera is unknown (preserves the
        /// legacy single-camera master filename for header-less archives).</summary>
        public string SlugSuffix()
        {
            if (Instrument.Length == 0) return "";
            var sb = new System.Text.StringBuilder(32);
            sb.Append('_').Append(Sanitize(Instrument));
            if (Telescope.Length > 0) sb.Append('_').Append(Sanitize(Telescope));
            if (FocalLength > 0) sb.Append('_').Append(FocalLength.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append("mm");
            return sb.ToString();
        }

        /// <summary>Human label for the PSF/noise report, e.g. "ZWO ASI533MC Pro / Askar @ 1000mm".</summary>
        public string Describe()
        {
            var sb = new System.Text.StringBuilder(Instrument.Length > 0 ? Instrument : "unknown camera");
            if (Telescope.Length > 0) sb.Append(" / ").Append(Telescope);
            if (FocalLength > 0) sb.Append(" @ ").Append(FocalLength.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append("mm");
            return sb.ToString();
        }

        private static string Norm(string? s) => s?.Trim() ?? "";

        private static bool KnownAndDiffer(string a, string b) =>
            a.Length > 0 && b.Length > 0 && !string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        private static bool KnownAndDiffer(int a, int b) => a > 0 && b > 0 && a != b;

        private static string Sanitize(string raw)
        {
            var sb = new System.Text.StringBuilder(raw.Length);
            foreach (var c in raw)
            {
                if (char.IsLetterOrDigit(c)) sb.Append(c);
                else if (c is '+' or '-') sb.Append(c);
            }
            return sb.Length > 0 ? sb.ToString() : "cam";
        }
    }

    /// <summary>Groups the calibration frames (Bias / Dark / DarkFlat / Flat) among
    /// <paramref name="frames"/> by <see cref="MasterGroupKey"/>. Lights and anything else are
    /// ignored. Pure — the caller passes the already-scanned archive frames (one scan feeds this and
    /// <see cref="SessionDiscovery.GroupSessions"/>).</summary>
    public static IReadOnlyDictionary<FrameType, List<CalGroup>> GroupCalibration(IEnumerable<FrameInfo> frames)
    {
        // Compose the sensor-config key with the optical train so two cameras that share a sensor
        // model never fold their calibration into one master (their dark/flat patterns differ), and
        // with the master flag so a foreign already-integrated master is never medianed together with
        // raw subs of the same config (it is loaded directly, they are combined).
        var byKey = new Dictionary<(MasterGroupKey Key, CalTrain Train, bool IsMaster), List<FrameInfo>>();
        foreach (var frame in frames)
        {
            if (frame.FrameType is not (FrameType.Bias or FrameType.Dark or FrameType.DarkFlat or FrameType.Flat))
            {
                continue;
            }
            var composite = (MasterGroupKey.FromFrame(frame), CalTrain.ForFrame(frame), frame.IsMaster);
            if (!byKey.TryGetValue(composite, out var list))
            {
                byKey[composite] = list = new List<FrameInfo>();
            }
            list.Add(frame);
        }

        var byType = new Dictionary<FrameType, List<CalGroup>>();
        foreach (var ((key, train, isMaster), list) in byKey)
        {
            if (!byType.TryGetValue(key.Type, out var groups))
            {
                byType[key.Type] = groups = new List<CalGroup>();
            }
            groups.Add(new CalGroup(key, train, [.. list], isMaster));
        }
        return byType;
    }

    /// <summary>
    /// Resolves the best-matching <see cref="Calibrator"/> for a session (dark + flat, no bias),
    /// building/loading the matched masters through <paramref name="masterCache"/>. Returns
    /// <c>null</c> only when neither a compatible dark nor a compatible flat exists (the session must
    /// then register uncalibrated — logged as a warning, since that weakens N2N validity).
    /// </summary>
    public static async Task<Calibrator?> ResolveAsync(
        ImagingSession session,
        IReadOnlyDictionary<FrameType, List<CalGroup>> calGroups,
        MasterCache masterCache,
        bool requireGainMatch = false,
        ILogger? logger = null,
        CancellationToken cancellationToken = default)
    {
        var light = session.Lights[0];
        var lightKey = MasterGroupKey.FromFrame(light);

        var darkGroup = BestDark(calGroups.GetValueOrDefault(FrameType.Dark), light, requireGainMatch);
        var flatGroup = BestFlat(calGroups.GetValueOrDefault(FrameType.Flat), light);

        // A gain/offset-mismatched dark is only ever picked when no same-gain library exists (the
        // penalty guarantees a matching one wins) -- but it mis-scales the fixed pattern that dark
        // subtraction exists to remove for N2N independence, so the fallback must be LOUD, not
        // silent: the actionable fix is shooting a matching dark library, which the fingerprinted
        // master cache then picks up on the next run without invalidating anything else.
        if (darkGroup is not null
            && (GainMismatch(darkGroup.Key, lightKey) || OffsetMismatch(darkGroup.Key, lightKey)))
        {
            logger?.LogWarning(
                "  [{Session}] dark {Dark} gain/offset mismatch (dark g{DarkGain}/o{DarkOffset} vs lights g{LightGain}/o{LightOffset}) -- " +
                "no matching dark library in the archive; dark current will be mis-scaled (weakens N2N validity). " +
                "Shoot a dark library at the lights' gain/offset/exposure/temperature.",
                session.Id, darkGroup.Key.Slug(), darkGroup.Key.Gain, darkGroup.Key.Offset, lightKey.Gain, lightKey.Offset);
        }

        // The domain a normalised foreign master (dark/bias) must be rescaled INTO is the lights' own
        // storage full-scale: N.I.N.A. left-aligns the camera's ADC output into the light's integer
        // container (Int16 -> [0, 65535]), and a tool normalises a master by that same full-scale, so
        // rescaling by it recovers the ADU domain the lights live in. Derived from the light's bit
        // depth, never hard-coded -- a light stored at a different depth would carry a different scale
        // (null for a float light: no fixed container, so a normalised master can't be reconciled).
        var normalizedAduScale = light.BitDepth.UnsignedFullScale is { } fullScale ? (float)fullScale : (float?)null;

        var dark = darkGroup is null ? null : await masterCache.GetOrBuildAsync(darkGroup.Key, darkGroup.Train, darkGroup.Frames, darkGroup.IsMaster, normalizedAduScale, cancellationToken);
        var flat = flatGroup is null ? null : await masterCache.GetOrBuildAsync(flatGroup.Key, flatGroup.Train, flatGroup.Frames, flatGroup.IsMaster, normalizedAduScale, cancellationToken);

        logger?.LogInformation("  [{Session}] calibration: dark={Dark} flat={Flat}",
            session.Id, darkGroup is null ? "NONE" : darkGroup.Key.Slug(), flatGroup is null ? "NONE" : flatGroup.Key.Slug());

        if (dark is null && flat is null)
        {
            logger?.LogWarning(
                "  [{Session}] no compatible dark/flat found (sensor={Sensor} {W}x{H}x{Ch}, {Exp:F0}s, {Temp}) -- registering UNCALIBRATED (weakens N2N validity)",
                session.Id, lightKey.SensorType, lightKey.Width, lightKey.Height, lightKey.ChannelCount,
                lightKey.Exposure.TotalSeconds, lightKey.TemperatureC?.ToString() ?? "no-temp");
            return null;
        }
        return new Calibrator(Bias: null, Dark: dark, Flat: flat);
    }

    /// <summary>Penalty for a gain mismatch. Sized deliberately: BELOW a grossly-wrong
    /// temperature+exposure alternative — on the reference archive the only choices for 60s/−5°C
    /// g121 lights are a 60s/−5°C g212 dark (this penalty, 200) and a 4.5s/+22°C g121 flat-wizard
    /// dark (~325), and the matched-exposure/temperature dark is the better of the two bad options —
    /// but ABOVE any same-library temperature jitter, so a same-gain dark wins whenever one exists.</summary>
    private const double GainMismatchPenalty = 200.0;

    /// <summary>Half of <see cref="GainMismatchPenalty"/> when either side's gain is unknown (−1):
    /// a known-matching dark beats an unknown one, and an unknown beats a known mismatch.</summary>
    private const double GainUnknownPenalty = 100.0;

    /// <summary>Offset (black level) mismatch shifts the dark's pedestal, but is a smaller error
    /// than a gain mismatch (pattern amplitude) — quarter weight.</summary>
    private const double OffsetMismatchPenalty = 50.0;
    private const double OffsetUnknownPenalty = 25.0;

    /// <summary>A light-dark must fall within this factor of the light's exposure to be a candidate.
    /// Dark current + amp glow scale with exposure and this pipeline does NOT dark-scale, so a dark
    /// far from the light's exposure is not a valid subtraction. Load-bearing: N.I.N.A. writes
    /// <b>dark-flats</b> (short darks matched to the FLAT exposure, e.g. 4.6s/6.7s) with
    /// <c>IMAGETYP=DARK</c> — only their <c>DARKFLAT\</c> folder distinguishes them — so without this
    /// gate a 6.7s dark-flat out-scores the matched-exposure 60s light-dark once gain is weighted, and
    /// silently calibrates 60s lights with a 9x-too-short frame. The stack pipeline sidesteps this by
    /// weighting exposure over gain in its matcher; the gate makes it explicit (and, with
    /// RequireDarkCalibration, a session whose only "dark" is a dark-flat is correctly skipped).</summary>
    private const double ExposureCompatibleLow = 0.5;
    private const double ExposureCompatibleHigh = 2.0;

    /// <summary>Best dark for a light: EXPOSURE-compatible first (see <see cref="ExposureCompatibleLow"/>
    /// — excludes dark-flats), same CAMERA (a dark is the body's own fixed pattern -- amp glow,
    /// unit-to-unit variation -- never borrowed across bodies even when they share a sensor model),
    /// dimension/sensor-compatible, then ranked by closest temperature, closest exposure (dark current
    /// scales with both), then matching gain/offset (a wrong-gain dark mis-scales the fixed pattern;
    /// see <see cref="GainMismatchPenalty"/>). Score ties break by ordinal <see cref="MasterGroupKey.Slug"/>
    /// so the pick never depends on dictionary / filesystem enumeration order (the build's determinism
    /// claim).</summary>
    internal static CalGroup? BestDark(List<CalGroup>? darks, FrameInfo light, bool requireGainMatch = false)
    {
        if (darks is null) return null;
        var lightKey = MasterGroupKey.FromFrame(light);
        var lightCamera = CalTrain.Camera(light);
        CalGroup? best = null;
        var bestScore = double.PositiveInfinity;
        foreach (var g in darks)
        {
            // A raw group with <2 frames can never build a master (the median needs >=2), so it is
            // not a valid candidate -- selecting it would resolve to a null dark and, under
            // RequireDarkCalibration, wrongly skip a session that had a buildable (if worse-scoring)
            // dark. A foreign master group is exempt (a single already-integrated frame is served
            // directly). With requireGainMatch on, a KNOWN gain mismatch is a hard reject (not just a
            // score penalty) -- the "be strict" gate, so a wrong-gain dark is never silently used.
            if (!Buildable(g)
                || !ExposureCompatible(g.Key.Exposure, lightKey.Exposure)
                || !DimensionCompatible(g.Key, lightKey)
                || !g.Train.CameraCompatibleWith(lightCamera)
                || !GainCompatible(g.Key, lightKey, requireGainMatch)) continue;
            var score = TempPenalty(g.Key, lightKey) * 10.0
                + Math.Abs((g.Key.Exposure - lightKey.Exposure).TotalSeconds)
                + GainPenalty(g.Key, lightKey)
                + OffsetPenalty(g.Key, lightKey);
            if (score < bestScore || (score == bestScore && SlugBefore(g, best)))
            {
                bestScore = score;
                best = g;
            }
        }
        return best;
    }

    /// <summary>Best flat for a light: same OPTICAL TRAIN (camera + telescope + focal length -- a
    /// flat encodes this train's vignetting + dust, so a different scope / body / focal length flat
    /// is simply wrong), dimension/sensor-compatible, preferring the same filter (Name + Bandpass),
    /// then closest temperature, then matching gain (flat division normalises most of the gain away,
    /// but same-gain is still the better master when both exist). Exposure is irrelevant for flats;
    /// offset cancels in the flat normalisation. Ties break by ordinal slug, as for darks.</summary>
    internal static CalGroup? BestFlat(List<CalGroup>? flats, FrameInfo light)
    {
        if (flats is null) return null;
        var lightKey = MasterGroupKey.FromFrame(light);
        var lightTrain = CalTrain.OpticalTrain(light);
        CalGroup? best = null;
        var bestScore = double.PositiveInfinity;
        foreach (var g in flats)
        {
            // Skip unbuildable singletons (see BestDark): a lone raw flat frame can't build a master,
            // so it must not out-rank a multi-frame flat and leave the session with no flat at all. A
            // foreign master flat is exempt (loaded directly).
            if (!Buildable(g) || !DimensionCompatible(g.Key, lightKey) || !g.Train.TrainCompatibleWith(lightTrain)) continue;
            var filterMismatch = g.Key.FilterName == lightKey.FilterName && g.Key.FilterBandpass == lightKey.FilterBandpass ? 0.0 : 1000.0;
            var score = filterMismatch + TempPenalty(g.Key, lightKey) * 10.0 + GainPenalty(g.Key, lightKey);
            if (score < bestScore || (score == bestScore && SlugBefore(g, best)))
            {
                bestScore = score;
                best = g;
            }
        }
        return best;
    }

    /// <summary>True when a dark's exposure is within <see cref="ExposureCompatibleLow"/>..
    /// <see cref="ExposureCompatibleHigh"/> of the light's — i.e. a plausible light-dark, not a
    /// dark-flat. A non-positive light exposure (unknown) disables the gate (accept any).</summary>
    private static bool ExposureCompatible(TimeSpan dark, TimeSpan light)
    {
        var l = light.TotalSeconds;
        if (l <= 0) return true;
        var ratio = dark.TotalSeconds / l;
        return ratio >= ExposureCompatibleLow && ratio <= ExposureCompatibleHigh;
    }

    /// <summary>A group can serve a master when it is a foreign already-integrated master (a single
    /// frame is loaded directly) or it holds &gt;= 2 raw frames to combine. Guards the Best* candidate
    /// filters so an unbuildable raw singleton is never selected (which would resolve to a null master
    /// and, under RequireDarkCalibration, wrongly skip a session).</summary>
    private static bool Buildable(CalGroup g) => g.Frames.Length >= (g.IsMaster ? 1 : 2);

    /// <summary>The strict gain gate (opt-in via RequireGainMatch): when on, a dark whose gain is
    /// KNOWN and differs from the light's is rejected outright, so a wrong-gain dark can never be
    /// silently substituted. An unknown gain on either side stays a wildcard (a header-less library
    /// is not dropped) -- the same lenient-on-unknown policy the optical-train comparisons use. When
    /// off, gain only weights the score (see <see cref="GainPenalty"/>).</summary>
    private static bool GainCompatible(MasterGroupKey g, MasterGroupKey light, bool requireGainMatch) =>
        !requireGainMatch || !(g.Gain >= 0 && light.Gain >= 0 && g.Gain != light.Gain);

    private static bool DimensionCompatible(MasterGroupKey g, MasterGroupKey light) =>
        g.SensorType == light.SensorType
        && g.ChannelCount == light.ChannelCount
        && g.Width == light.Width
        && g.Height == light.Height;

    private static double TempPenalty(MasterGroupKey g, MasterGroupKey light) =>
        g.TemperatureC is { } gt && light.TemperatureC is { } lt ? Math.Abs(gt - lt) : 100.0;

    private static double GainPenalty(MasterGroupKey g, MasterGroupKey light) =>
        g.Gain >= 0 && light.Gain >= 0
            ? g.Gain == light.Gain ? 0.0 : GainMismatchPenalty
            : GainUnknownPenalty;

    private static double OffsetPenalty(MasterGroupKey g, MasterGroupKey light) =>
        g.Offset >= 0 && light.Offset >= 0
            ? g.Offset == light.Offset ? 0.0 : OffsetMismatchPenalty
            : OffsetUnknownPenalty;

    private static bool GainMismatch(MasterGroupKey g, MasterGroupKey light) =>
        g.Gain >= 0 && light.Gain >= 0 && g.Gain != light.Gain;

    private static bool OffsetMismatch(MasterGroupKey g, MasterGroupKey light) =>
        g.Offset >= 0 && light.Offset >= 0 && g.Offset != light.Offset;

    /// <summary>Deterministic tie-break: does <paramref name="g"/> order before the current best by
    /// ordinal slug? Two groups CAN tie exactly (e.g. two dark libraries differing only in a field
    /// the dark score ignores), and without this the winner would follow dictionary insertion /
    /// filesystem enumeration order.</summary>
    private static bool SlugBefore(CalGroup g, CalGroup? best) =>
        best is not null && string.CompareOrdinal(g.Key.Slug(), best.Key.Slug()) < 0;
}
