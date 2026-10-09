using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.Catalogs;

namespace TianWen.Lib.Imaging.Planetary;

/// <summary>
/// A synthetic capture's truth as <c>planetary degrade</c> and <c>planetary twin</c> write it: the planet rendered through the pupil alone,
/// in ADU over the sky, with the disk it was rendered at (DISKX, DISKY, DISKR, NORTHANG; round, the planet's limb options give its axis
/// ratio and rings), the instant (DATE-OBS) and the wavelength (WAVELEN). One reading of it for every verb that scores against it and for
/// the AUTO mode's tune (docs/plans/planetary-stacking.md, A2).
/// </summary>
public sealed record PlanetaryTruth(float[] Plane, int Width, int Height, MetricDisk Disk, DateTimeOffset? Time, double WavelengthNm)
{
    /// <summary>
    /// The truth at <paramref name="path"/>; null where the file is not a readable FITS image or carries no disk (DISKX). AXISRAT, which
    /// <c>planetary lucky-frames</c> writes for a window scored with the stack's own oblate disk, is read where it is there.
    /// </summary>
    public static PlanetaryTruth? Read(string path)
    {
        if (!Image.TryReadFitsFile(path, out var image))
        {
            return null;
        }
        using var fits = Image.OpenFitsHeader(path);
        if (fits.ReadFirstImageHduHeaderOnly()?.Header is not { } header || !double.IsFinite(header.GetDoubleValue("DISKX", double.NaN)))
        {
            return null;
        }
        var disk = new MetricDisk(header.GetDoubleValue("DISKX", double.NaN), header.GetDoubleValue("DISKY", double.NaN),
            header.GetDoubleValue("DISKR", double.NaN), header.GetDoubleValue("AXISRAT", 1), header.GetDoubleValue("NORTHANG", 0));
        var time = DateTimeOffset.TryParse(header.GetStringValue("DATE-OBS"), CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var utc) ? utc : (DateTimeOffset?)null;
        return new PlanetaryTruth(image.GetChannelSpan(0).ToArray(), image.Width, image.Height, disk, time, header.GetDoubleValue("WAVELEN", double.NaN));
    }

    /// <summary>
    /// A truth's disk with what the planet's limb options say of it: the axis ratio and, for Saturn, its rings about the north the truth
    /// was drawn with (S4), so the scores read its sky, moons and limb around them.
    /// </summary>
    public static MetricDisk WithPlanet(MetricDisk truthDisk, LimbFitOptions options) => truthDisk with
    {
        AxisRatio = options.AxisRatio,
        Rings = options.Rings is { } rings ? DiskRings.Of(truthDisk.AxisAngleDeg, truthDisk.AxisAngleDeg, options, rings) : null,
    };

    /// <summary>
    /// <paramref name="plane"/> (one channel) against this truth: its own limb fitted and moved onto the truth's disk, both normalised on
    /// it, then the fidelity a band at a time inside 0.9 radii (<see cref="PlanetaryMetrics.Fidelity"/>). Null where the truth has no
    /// time, the plane is off the truth's grid, or its limb does not fit.
    /// </summary>
    public ImmutableArray<BandFidelity>? FidelityOf(Image plane, CatalogIndex planet)
    {
        ArgumentNullException.ThrowIfNull(plane);
        if (Time is not { } when || plane.Width != Width || plane.Height != Height)
        {
            return null;
        }
        var limbOptions = PlanetaryLimbFit.OptionsFor(PhysicalEphemeris.Compute(planet, when));
        var disk = WithPlanet(Disk, limbOptions);
        if (PlanetaryMetrics.Register(plane, limbOptions, disk) is not { } fitted)
        {
            return null;
        }
        var reference = PlanetaryMetrics.Normalise(Plane, Width, Height, disk);
        return PlanetaryMetrics.Fidelity(fitted.Plane, reference, Width, Height, disk);
    }

    /// <summary>
    /// How far <paramref name="master"/> is from its truths: each channel against its own (one for a mono master, red, green and blue for a
    /// colour one), the band errors summed over the first <paramref name="bands"/> bands inside 0.9 radii, averaged over the channels. The
    /// one number the AUTO mode's tune minimises (A2), and the "bands 1 to 4" figure <c>planetary sharpen --truth</c> prints. Null where a
    /// channel cannot be scored.
    /// </summary>
    public static double? BandError(Image master, IReadOnlyList<PlanetaryTruth> truths, CatalogIndex planet, int bands = 4)
    {
        ArgumentNullException.ThrowIfNull(master);
        ArgumentNullException.ThrowIfNull(truths);
        if (truths.Count != master.ChannelCount)
        {
            throw new ArgumentException($"a {master.ChannelCount}-channel master is scored against as many truths, not {truths.Count}", nameof(truths));
        }
        double sum = 0;
        for (var c = 0; c < truths.Count; c++)
        {
            var plane = master.ChannelCount == 1 ? master : master.ChannelImage(c);
            try
            {
                if (truths[c].FidelityOf(plane, planet) is not { } fidelity)
                {
                    return null;
                }
                for (var b = 0; b < Math.Min(bands, fidelity.Length); b++)
                {
                    sum += fidelity[b].Error;
                }
            }
            finally
            {
                if (master.ChannelCount != 1)
                {
                    plane.Release();
                }
            }
        }
        return sum / truths.Count;
    }
}
