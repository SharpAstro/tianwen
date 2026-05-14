using System;

namespace TianWen.Lib.Imaging.Calibration;

/// <summary>
/// Equality-able key identifying a set of calibration frames that can be
/// combined into a single master. Frames matching on this key share the same
/// sensor configuration, exposure, temperature setpoint, filter, dimensions,
/// and frame type — i.e. they're calibration-interchangeable.
/// </summary>
/// <param name="Type">Bias / Dark / Flat / DarkFlat. Lights are never grouped
/// for master generation (the integrator handles those).</param>
/// <param name="Exposure">Exposure duration. Compared exactly — callers that
/// want tolerance (e.g. group 60.001 s with 60.000 s darks) should quantize
/// before constructing the key.</param>
/// <param name="TemperatureC">Sensor temperature in Celsius, rounded to the
/// nearest integer (most cameras stabilize to ~0.1 C, but 1 C tolerance is
/// the practical lower bound for noise-pattern matching). <c>null</c> when
/// the FITS header had no <c>CCD-TEMP</c>.</param>
/// <param name="Filter">Filter in the optical path. Empty/None for bias and
/// darks; meaningful for flats. Compared by <c>Name</c> + <c>Bandpass</c>
/// only — <c>RawName</c> drifts across FITS round-trips.</param>
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
    string FilterName,
    Bandpass FilterBandpass,
    int Width,
    int Height,
    int ChannelCount,
    SensorType SensorType,
    short Gain,
    int Offset)
{
    /// <summary>Derives the master-group key from a single frame's parsed header.</summary>
    public static MasterGroupKey FromFrame(FrameInfo frame)
    {
        var meta = frame.Meta;
        var temp = float.IsNaN(meta.CCDTemperature) ? null as int? : (int)Math.Round(meta.CCDTemperature);
        return new MasterGroupKey(
            Type: meta.FrameType,
            Exposure: meta.ExposureDuration,
            TemperatureC: temp,
            // Compare Filter by canonical Name + Bandpass only — RawName drifts
            // across FITS round-trips and would partition otherwise-identical
            // flats into spurious groups.
            FilterName: meta.Filter.Name,
            FilterBandpass: meta.Filter.Bandpass,
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
            var f = FilterName.Length > 0 ? FilterName : "nofilter";
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
