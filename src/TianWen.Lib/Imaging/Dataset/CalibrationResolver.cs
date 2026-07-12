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
    /// <summary>A set of calibration frames that combine into one master.</summary>
    public sealed record CalGroup(MasterGroupKey Key, ImmutableArray<FrameInfo> Frames);

    /// <summary>Groups the calibration frames (Bias / Dark / DarkFlat / Flat) among
    /// <paramref name="frames"/> by <see cref="MasterGroupKey"/>. Lights and anything else are
    /// ignored. Pure — the caller passes the already-scanned archive frames (one scan feeds this and
    /// <see cref="SessionDiscovery.GroupSessions"/>).</summary>
    public static IReadOnlyDictionary<FrameType, List<CalGroup>> GroupCalibration(IEnumerable<FrameInfo> frames)
    {
        var byKey = new Dictionary<MasterGroupKey, List<FrameInfo>>();
        foreach (var frame in frames)
        {
            if (frame.FrameType is not (FrameType.Bias or FrameType.Dark or FrameType.DarkFlat or FrameType.Flat))
            {
                continue;
            }
            var key = MasterGroupKey.FromFrame(frame);
            if (!byKey.TryGetValue(key, out var list))
            {
                byKey[key] = list = new List<FrameInfo>();
            }
            list.Add(frame);
        }

        var byType = new Dictionary<FrameType, List<CalGroup>>();
        foreach (var (key, list) in byKey)
        {
            if (!byType.TryGetValue(key.Type, out var groups))
            {
                byType[key.Type] = groups = new List<CalGroup>();
            }
            groups.Add(new CalGroup(key, [.. list]));
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
        ILogger? logger = null,
        CancellationToken cancellationToken = default)
    {
        var lightKey = MasterGroupKey.FromFrame(session.Lights[0]);

        var darkGroup = BestDark(calGroups.GetValueOrDefault(FrameType.Dark), lightKey);
        var flatGroup = BestFlat(calGroups.GetValueOrDefault(FrameType.Flat), lightKey);

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

        var dark = darkGroup is null ? null : await masterCache.GetOrBuildAsync(darkGroup.Key, darkGroup.Frames, cancellationToken);
        var flat = flatGroup is null ? null : await masterCache.GetOrBuildAsync(flatGroup.Key, flatGroup.Frames, cancellationToken);

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

    /// <summary>Best dark for a light: dimension/sensor-compatible, ranked by closest temperature,
    /// closest exposure (dark current scales with both), then matching gain/offset (a wrong-gain
    /// dark mis-scales the fixed pattern; see <see cref="GainMismatchPenalty"/>). Score ties break
    /// by ordinal <see cref="MasterGroupKey.Slug"/> so the pick never depends on dictionary /
    /// filesystem enumeration order (the build's determinism claim).</summary>
    internal static CalGroup? BestDark(List<CalGroup>? darks, MasterGroupKey light)
    {
        if (darks is null) return null;
        CalGroup? best = null;
        var bestScore = double.PositiveInfinity;
        foreach (var g in darks)
        {
            if (!DimensionCompatible(g.Key, light)) continue;
            var score = TempPenalty(g.Key, light) * 10.0
                + Math.Abs((g.Key.Exposure - light.Exposure).TotalSeconds)
                + GainPenalty(g.Key, light)
                + OffsetPenalty(g.Key, light);
            if (score < bestScore || (score == bestScore && SlugBefore(g, best)))
            {
                bestScore = score;
                best = g;
            }
        }
        return best;
    }

    /// <summary>Best flat for a light: dimension/sensor-compatible, preferring the same filter
    /// (Name + Bandpass), then closest temperature, then matching gain (flat division normalises
    /// most of the gain away, but same-gain is still the better master when both exist). Exposure
    /// is irrelevant for flats; offset cancels in the flat normalisation. Ties break by ordinal
    /// slug, as for darks.</summary>
    internal static CalGroup? BestFlat(List<CalGroup>? flats, MasterGroupKey light)
    {
        if (flats is null) return null;
        CalGroup? best = null;
        var bestScore = double.PositiveInfinity;
        foreach (var g in flats)
        {
            if (!DimensionCompatible(g.Key, light)) continue;
            var filterMismatch = g.Key.FilterName == light.FilterName && g.Key.FilterBandpass == light.FilterBandpass ? 0.0 : 1000.0;
            var score = filterMismatch + TempPenalty(g.Key, light) * 10.0 + GainPenalty(g.Key, light);
            if (score < bestScore || (score == bestScore && SlugBefore(g, best)))
            {
                bestScore = score;
                best = g;
            }
        }
        return best;
    }

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
