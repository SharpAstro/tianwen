namespace TianWen.Lib.Imaging.Calibration;

/// <summary>
/// Equality-able key partitioning a folder of light frames into independently
/// stackable sets. A folder may mix multiple targets (NINA writes all lights
/// to a single LIGHT directory regardless of target), and frames of different
/// targets never register against each other — they look at different sky.
///
/// <para>The key wraps a <see cref="MasterGroupKey"/> (which encodes the
/// sensor configuration, exposure, temperature, filter, dimensions, gain, and
/// offset) plus the FITS <c>OBJECT</c> header value. When <see cref="ObjectName"/>
/// is empty, two frames group together iff their calibration keys match —
/// identical to the legacy behavior. When set, frames of different targets
/// are forced into separate groups even when otherwise identical.</para>
///
/// <para>Calibration master grouping continues to use <see cref="MasterGroupKey"/>
/// directly: bias / dark / flat masters are sky-independent and should be
/// shared across all targets imaged with the same sensor configuration.</para>
/// </summary>
/// <param name="CalibrationKey">The sensor / exposure / filter signature used
/// to find the matching calibration masters for this light group.</param>
/// <param name="ObjectName">FITS <c>OBJECT</c> header value, empty when unset.</param>
public sealed record LightGroupKey(MasterGroupKey CalibrationKey, string ObjectName)
{
    /// <summary>Derives the light-group key from a single frame's parsed header.</summary>
    public static LightGroupKey FromFrame(FrameInfo frame)
        => new(MasterGroupKey.FromFrame(frame), frame.Meta.ObjectName);

    /// <summary>
    /// Filename-safe slug for the group, of the form
    /// <c>&lt;object&gt;_&lt;calibration-slug&gt;</c>. Falls back to just the
    /// calibration slug when <see cref="ObjectName"/> is empty.
    /// </summary>
    public string Slug()
    {
        var calSlug = CalibrationKey.Slug();
        if (string.IsNullOrEmpty(ObjectName))
        {
            return calSlug;
        }
        return SanitizeForFilename(ObjectName) + "_" + calSlug;
    }

    private static string SanitizeForFilename(string raw)
    {
        var sb = new System.Text.StringBuilder(raw.Length);
        foreach (var c in raw)
        {
            if (char.IsLetterOrDigit(c)) sb.Append(c);
            else if (c is '+' or '-' or '_') sb.Append(c);
            // skip everything else (spaces, slashes, unicode glyphs)
        }
        return sb.Length > 0 ? sb.ToString() : "object";
    }
}
