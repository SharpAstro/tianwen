using System;

namespace TianWen.Lib.Imaging.Calibration;

/// <summary>
/// Equality-able key identifying a set of calibration frames that can be
/// combined into a single master. Frames matching on this key share the same
/// sensor configuration, exposure, temperature setpoint, filter, dimensions,
/// and frame type, i.e. they're calibration-interchangeable.
/// </summary>
/// <param name="Type">Bias / Dark / Flat / DarkFlat. Lights are never grouped
/// for master generation (the integrator handles those).</param>
/// <param name="Exposure">Exposure duration. Compared exactly; callers that
/// want tolerance (e.g. group 60.001 s with 60.000 s darks) should quantize
/// before constructing the key.</param>
/// <param name="TemperatureC">Sensor temperature in Celsius, rounded to the
/// nearest integer (most cameras stabilize to ~0.1 C, but 1 C tolerance is
/// the practical lower bound for noise-pattern matching). <c>null</c> when
/// the FITS header had no <c>CCD-TEMP</c>.</param>
/// <param name="FilterIdentity">Filter in the optical path, as
/// <see cref="Filter.IdentityKey"/>. Empty for bias and darks; meaningful for flats.
/// <para><b>This is the one filter-identity answer, deliberately not a second one.</b> It was
/// <c>Filter.Name</c> + <c>Bandpass</c>, on the reasoning that <c>RawName</c> drifts across FITS
/// round-trips. It does, but the canonical name alone merges every UNRECOGNISED filter into a
/// single <c>Unknown</c> bucket, so <c>IDAS LPS-D3</c> and <c>Optolong L-Pro</c> shared one flat
/// master and a light shot through either matched the other. <see cref="Filter.IdentityKey"/>
/// already resolves exactly this: it canonicalises where the name IS recognised (so "Ha 3nm" and
/// "H-Alpha" still agree, which is the drift the old comment was defending against) and falls back
/// to the trimmed <c>RawName</c> only where it is not, which is precisely where merging is
/// destructive. <c>Bandpass</c> is dropped with it and loses nothing: it is a function of the
/// canonical name for every recognised filter and <c>None</c> for every unrecognised one, so it
/// never distinguished anything the name did not.</para></param>
/// <param name="Width">Image width in pixels.</param>
/// <param name="Height">Image height in pixels.</param>
/// <param name="ChannelCount">1 for mono / raw-Bayer, 3 for pre-debayered RGB.</param>
/// <param name="SensorType">Monochrome / RGGB / Color / etc. Critical for the
/// flat-master path (Bayer flats need per-quadrant normalization).</param>
/// <param name="Gain">Camera gain register setting. -1 = unknown; treated as
/// a distinct value from any real gain.</param>
/// <param name="Offset">Camera offset / black-level register. -1 = unknown.</param>
public sealed record MasterGroupKey(
    FrameType Type,
    TimeSpan Exposure,
    int? TemperatureC,
    string FilterIdentity,
    int Width,
    int Height,
    int ChannelCount,
    SensorType SensorType,
    short Gain,
    int Offset)
{
    /// <summary>
    /// Do these two keys name the same filter? <b>The one filter-match test</b>, because it was
    /// hand-spelled at five call sites (flat scoring in <c>CalibrationResolver</c> and
    /// <c>StackingPipeline</c>, twice in <c>CalibrationCoverageReport</c>, and the ghost-group log)
    /// and every one had to be found and changed to fix the <c>Unknown</c>-bucket merge. A change to
    /// what "same filter" means now lands here or nowhere.
    /// </summary>
    public bool SameFilterAs(MasterGroupKey other) => FilterIdentity == other.FilterIdentity;

    /// <summary>Derives the master-group key from a single frame's parsed header.</summary>
    public static MasterGroupKey FromFrame(FrameInfo frame)
    {
        var meta = frame.Meta;
        var temp = float.IsNaN(meta.CCDTemperature) ? null as int? : (int)Math.Round(meta.CCDTemperature);
        return new MasterGroupKey(
            Type: meta.FrameType,
            Exposure: meta.ExposureDuration,
            TemperatureC: temp,
            // Filter.IdentityKey is the single "are these the same filter" answer; see the
            // FilterIdentity param docs for why the canonical name alone was wrong here.
            FilterIdentity: meta.Filter.IdentityKey,
            Width: frame.Width,
            Height: frame.Height,
            ChannelCount: frame.ChannelCount,
            SensorType: meta.SensorType,
            Gain: meta.Gain,
            Offset: meta.Offset);
    }

    /// <summary>
    /// Filename-safe slug summarizing the group's identifying fields, suitable
    /// for embedding in output filenames like <c>master_dark_300s_-10C.fits</c>.
    /// Skips fields irrelevant to the frame type (filter for bias/dark,
    /// exposure for bias).
    /// </summary>
    public string Slug()
    {
        var sb = new System.Text.StringBuilder(64);
        sb.Append(Type.ToString().ToLowerInvariant());

        // Bias is instantaneous; exposure on bias is meaningless noise from
        // the camera firmware's clock granularity. For flats and darks, use
        // 2-decimal precision so sub-second flats (0.5s, 0.3s, etc.) don't
        // round to "0s" via banker's rounding.
        if (Type is not FrameType.Bias)
        {
            sb.Append('_').Append(Exposure.TotalSeconds.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)).Append('s');
        }

        if (TemperatureC is { } t)
        {
            sb.Append('_').Append(t.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append('C');
        }

        // Filter only meaningful on flats; bias / dark are filter-independent.
        if (Type is FrameType.Flat or FrameType.DarkFlat)
        {
            // IdentityKey is "" for Filter.None, which is what makes this fallback reachable at
            // all: Filter.None.Name is the literal string "None", so the old key spelled an
            // unfiltered flat "_None_" and never took this branch.
            var f = FilterIdentity.Length > 0 ? FilterIdentity : "nofilter";
            sb.Append('_').Append(SanitizeForFilename(f));
        }

        if (Gain >= 0)
        {
            sb.Append("_g").Append(Gain);
        }

        return sb.ToString();
    }

    private static string SanitizeForFilename(string raw)
    {
        var sb = new System.Text.StringBuilder(raw.Length);
        foreach (var c in raw)
        {
            if (char.IsLetterOrDigit(c)) sb.Append(c);
            else if (c is '+' or '-' or '_') sb.Append(c);
            // skip everything else (spaces, slashes, unicode glyphs like Hα)
        }
        return sb.Length > 0 ? sb.ToString() : "filter";
    }
}
