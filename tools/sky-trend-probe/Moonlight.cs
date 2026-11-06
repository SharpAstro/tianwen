namespace SkyTrendProbe;

/// <summary>Scattered moonlight after Krisciunas and Schaefer 1991 (PASP 103, 1033), V band, in nanolamberts.</summary>
public static class Moonlight
{
    private const double Extinction = 0.172;

    /// <summary>Airmass X(Z) = (1 - 0.96 sin^2 Z)^-1/2.</summary>
    public static double Airmass(double altDeg)
    {
        var z = (90 - Math.Clamp(altDeg, 0, 90)) * Math.PI / 180;
        var s = Math.Sin(z);
        return 1 / Math.Sqrt(1 - 0.96 * s * s);
    }

    /// <param name="moonAltDeg">Moon altitude; at or below 0 there is no moonlight.</param>
    /// <param name="phaseAngleDeg">Moon phase angle alpha: 0 full, 90 quarter, 120 a 25 % crescent.</param>
    /// <param name="separationDeg">Moon to target angle rho.</param>
    /// <param name="objAltDeg">Target altitude.</param>
    public static double MoonBrightnessNl(double moonAltDeg, double phaseAngleDeg, double separationDeg, double objAltDeg)
    {
        if (!(moonAltDeg > 0))
        {
            return 0;
        }
        var a = Math.Abs(phaseAngleDeg);
        var istar = Math.Pow(10, -0.4 * (3.84 + 0.026 * a + 4e-9 * Math.Pow(a, 4)));
        var rho = separationDeg * Math.PI / 180;
        var cos = Math.Cos(rho);
        var f = Math.Pow(10, 5.36) * (1.06 + cos * cos) + Math.Pow(10, 6.15 - separationDeg / 40);
        var xMoon = Airmass(moonAltDeg);
        var xObj = Airmass(double.IsFinite(objAltDeg) ? objAltDeg : 50);
        return f * istar * Math.Pow(10, -0.4 * Extinction * xMoon) * (1 - Math.Pow(10, -0.4 * Extinction * xObj));
    }

    /// <summary>The factor a sky of <paramref name="darkNl"/> nanolamberts is brightened by (1 with no Moon).</summary>
    public static double SkyFactor(double moonAltDeg, double phaseAngleDeg, double separationDeg, double objAltDeg, double darkNl)
        => 1 + MoonBrightnessNl(moonAltDeg, phaseAngleDeg, separationDeg, objAltDeg) / darkNl;

    public sealed record Scenario(string Name, double Illumination, double RiseAtFraction, double RiseRateDegPerHour = 12, double MaxAltDeg = 40)
    {
        /// <summary>Phase angle for the illuminated fraction k = (1 + cos alpha) / 2.</summary>
        public double PhaseAngleDeg => Math.Acos(2 * Illumination - 1) * 180 / Math.PI;

        /// <summary>The modelled Moon altitude at <paramref name="t"/> in a series that runs from <paramref name="start"/> to <paramref name="end"/>.</summary>
        public double AltitudeAt(DateTimeOffset t, DateTimeOffset start, DateTimeOffset end)
        {
            var rise = start + (end - start) * RiseAtFraction;
            return t < rise ? -5 : Math.Min(MaxAltDeg, (t - rise).TotalHours * RiseRateDegPerHour);
        }
    }

    /// <summary>A 25 % crescent and a quarter Moon, each rising mid-run and rising three quarters of the way through (before dawn).</summary>
    public static readonly IReadOnlyList<Scenario> Scenarios =
    [
        new("crescent-rises-mid-run", 0.25, 0.5),
        new("crescent-rises-before-dawn", 0.25, 0.75),
        new("quarter-rises-mid-run", 0.5, 0.5),
        new("quarter-rises-before-dawn", 0.5, 0.75),
    ];
}
