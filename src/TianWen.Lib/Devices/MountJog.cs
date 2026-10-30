using System;
using TianWen.DAL;
using TianWen.Lib.Astrometry;

namespace TianWen.Lib.Devices;

/// <summary>
/// Moving the mount while a button is held (the owner, 2026-10-20): a sky direction at a speed in multiples of sidereal, turned into
/// the axis and the signed rate a move-axis takes. The directions are the guide pulse's: West runs RA faster than tracking, East
/// slower, North runs the Dec motor forward, South in reverse, so a held button moves the field the way the same button's nudge did.
/// </summary>
public static class MountJog
{
    /// <summary>
    /// The speeds a held button offers, in multiples of sidereal: the handset's slow speeds, where a move rides on tracking as a guide
    /// pulse does, and faster ones up to 128x (half a degree a second, a handset's common top jog) to bring a target back from further off.
    /// </summary>
    public static readonly double[] SiderealMultiples = [0.5, 1, 2, 8, 32, 64, 128, Max];

    /// <summary>
    /// "As fast as the mount goes": a rate past any mount's top, which the node clamps to the mount's own (DeviceOperations'
    /// move-axis), 1.88 degrees a second on the TeeSeek's OnStep. Finite, as JSON carries no infinity.
    /// </summary>
    public const double Max = 10_000;

    /// <summary><see cref="Max"/> as a move-axis rate in degrees a second: the one value the node reads as "as fast as it goes".</summary>
    public static double MaxRateDegPerSec => RateDegPerSec(Max);

    /// <summary>A speed in degrees a second, as a move-axis and a mount's axis rates state it.</summary>
    public static double RateDegPerSec(double siderealMultiple) => Constants.SIDEREAL_RATE / 3600.0 * siderealMultiple;

    /// <summary>
    /// The axis and the signed rate for <paramref name="direction"/> at <paramref name="siderealMultiple"/>: East and West on the
    /// primary axis, positive along the hemisphere's tracking (forward in the north, reverse in the south), so West is faster than
    /// tracking either way; North and South on the secondary, North positive.
    /// </summary>
    public static (TelescopeAxis Axis, double Rate) AxisRateFor(GuideDirection direction, double siderealMultiple, double siteLatitude)
    {
        var rate = RateDegPerSec(siderealMultiple);
        var trackingSign = siteLatitude < 0 ? -1.0 : 1.0;
        return direction switch
        {
            GuideDirection.West => (TelescopeAxis.Primary, rate * trackingSign),
            GuideDirection.East => (TelescopeAxis.Primary, -rate * trackingSign),
            GuideDirection.North => (TelescopeAxis.Seconary, rate),
            GuideDirection.South => (TelescopeAxis.Seconary, -rate),
            _ => throw new ArgumentOutOfRangeException(nameof(direction), direction, "Not a sky direction"),
        };
    }
}
