using System;

namespace TianWen.Lib.Sequencing;

/// <summary>
/// Single source of truth for mapping the on-demand flat-run <c>source</c> / <c>period</c> strings onto their
/// enums, shared by the CLI <c>tianwen flats</c> command and the <c>POST /api/v1/session/flats</c> endpoint so
/// the accepted spellings never drift (mirrors <c>EnhanceOptions.TryParse</c>). Case-insensitive, with a few
/// friendly aliases. Returns false (and a sensible default) on an unrecognised value so callers report the error.
/// </summary>
public static class FlatRunParsing
{
    /// <summary>
    /// Parses a flat illumination source: <c>calibrator</c>/<c>panel</c> or <c>sky</c>/<c>twilight</c>. A manual
    /// (hand-switched) panel is not a source — it is a <see cref="Devices.ManualCoverDevice"/> assigned to the
    /// OTA's cover slot, captured through the <c>calibrator</c> path.
    /// </summary>
    public static bool TryParseSource(string? text, out FlatIlluminationSource source)
    {
        switch (text?.Trim().ToLowerInvariant())
        {
            case "calibrator" or "panel":
                source = FlatIlluminationSource.Calibrator;
                return true;
            case "sky" or "twilight" or "twilightsky":
                source = FlatIlluminationSource.TwilightSky;
                return true;
            default:
                source = FlatIlluminationSource.Calibrator;
                return false;
        }
    }

    /// <summary>Parses a twilight period: <c>dusk</c>/<c>evening</c> or <c>dawn</c>/<c>morning</c>.</summary>
    public static bool TryParsePeriod(string? text, out TwilightPeriod period)
    {
        switch (text?.Trim().ToLowerInvariant())
        {
            case "dusk" or "evening":
                period = TwilightPeriod.Dusk;
                return true;
            case "dawn" or "morning":
                period = TwilightPeriod.Dawn;
                return true;
            default:
                period = TwilightPeriod.Dusk;
                return false;
        }
    }
}
