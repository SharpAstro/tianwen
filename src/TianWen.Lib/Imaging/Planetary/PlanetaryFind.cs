using System;
using System.Globalization;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Astrometry.VSOP87;

namespace TianWen.Lib.Imaging.Planetary;

/// <summary>
/// What a planetary find learned (#1684): where a blind-solved full frame really points, how far that is from where the mount
/// said, the focal length the solve's scale gives, and how far the planet is from the field.
/// </summary>
/// <param name="Solution">The solve, on the full frame.</param>
/// <param name="MountOffsetDeg">How far the solved centre is from the mount's own position; null with no mount position.</param>
/// <param name="MeasuredFocalLengthMm">The focal length the solve's scale and the camera's photosite give; null without a photosite.</param>
/// <param name="ProfileFocalLengthMm">The focal length the profile names; null when it names none.</param>
/// <param name="Planet">The planet the find is for; null when none was named.</param>
/// <param name="PlanetSeparationDeg">How far the planet is from the solved centre now; null without a planet.</param>
public sealed record PlanetaryFindResult(
    WCS Solution,
    double? MountOffsetDeg,
    double? MeasuredFocalLengthMm,
    int? ProfileFocalLengthMm,
    CatalogIndex? Planet,
    double? PlanetSeparationDeg)
{
    /// <summary>
    /// How far a measured focal length may differ from the profile's before the operator is asked whether to update it (the owner,
    /// 2026-10-19): a barlow's or a focuser's travel moves it by more than this, a solve's scale by far less.
    /// </summary>
    public const double FocalLengthTolerance = 0.03;

    /// <summary>
    /// The measured focal length to OFFER the operator, rounded to the millimetre, when it is off the profile's by more than
    /// <see cref="FocalLengthTolerance"/>; null when there is nothing to offer. Never written unasked.
    /// </summary>
    public int? FocalLengthToOffer => MeasuredFocalLengthMm is { } measured && double.IsFinite(measured)
        && (ProfileFocalLengthMm is not { } named || named <= 0 || Math.Abs(measured - named) / named > FocalLengthTolerance)
            ? (int)Math.Round(measured)
            : null;

    /// <summary>Where the planet is in the field now: within the field's half-diagonal of its centre, or not.</summary>
    public bool PlanetInField(int width, int height)
        => PlanetSeparationDeg is { } separation && Solution.PixelScaleArcsec is > 0 and var scale
           && separation <= Math.Sqrt(width * (double)width + height * (double)height) / 2 * scale / 3600;

    /// <summary>The result in a sentence, for the panel's card and the job's last step.</summary>
    public string InWords()
    {
        var at = string.Create(CultureInfo.InvariantCulture,
            $"Solved at RA {Solution.CenterRA:0.000} h, Dec {Solution.CenterDec:+0.00;-0.00} deg, {Solution.PixelScaleArcsec:0.0000}\"/px");
        var mount = MountOffsetDeg is { } off ? string.Create(CultureInfo.InvariantCulture, $"; the mount was {off:0.00} deg off") : "";
        var fl = FocalLengthToOffer is { } offer
            ? $"; the focal length is {offer} mm{(ProfileFocalLengthMm is { } named ? $", the profile says {named}" : "")}"
            : "";
        var planet = Planet is { } body && PlanetSeparationDeg is { } sep
            ? string.Create(CultureInfo.InvariantCulture, $"; {body} is {sep:0.00} deg away")
            : "";
        return at + mount + fl + planet;
    }

    /// <summary>
    /// The find's result from a solve: <paramref name="believedRaHours"/>/<paramref name="believedDecDeg"/> where the mount said it
    /// pointed (NaN for none), <paramref name="pixelSizeUm"/> the camera's photosite (unbinned; NaN for unknown).
    /// </summary>
    public static PlanetaryFindResult From(WCS solution, double believedRaHours, double believedDecDeg, double pixelSizeUm,
        int? profileFocalLengthMm, CatalogIndex? planet, DateTimeOffset utc)
    {
        double? offset = double.IsFinite(believedRaHours) && double.IsFinite(believedDecDeg)
            ? Separation(solution.CenterRA, solution.CenterDec, believedRaHours, believedDecDeg)
            : null;
        double? measured = pixelSizeUm > 0 && solution.PixelScaleArcsec is > 0 and var scale ? 206.264806 * pixelSizeUm / scale : null;
        double? planetSeparation = planet is { } body && VSOP87a.ReduceJ2000(body, utc, out var ra, out var dec, out _)
            ? Separation(solution.CenterRA, solution.CenterDec, ra, dec)
            : null;
        return new PlanetaryFindResult(solution, offset, measured, profileFocalLengthMm, planet, planetSeparation);
    }

    // Great-circle separation in degrees, RA in hours.
    private static double Separation(double ra1Hours, double dec1Deg, double ra2Hours, double dec2Deg)
    {
        var (r1, d1, r2, d2) = (ra1Hours * 15 * Math.PI / 180, dec1Deg * Math.PI / 180, ra2Hours * 15 * Math.PI / 180, dec2Deg * Math.PI / 180);
        var cos = Math.Sin(d1) * Math.Sin(d2) + Math.Cos(d1) * Math.Cos(d2) * Math.Cos(r1 - r2);
        return Math.Acos(Math.Clamp(cos, -1, 1)) * 180 / Math.PI;
    }
}
