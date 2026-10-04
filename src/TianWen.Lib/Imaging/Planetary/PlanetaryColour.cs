using System;
using System.Globalization;
using System.Collections.Generic;
using System.Collections.Immutable;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Imaging.Degradation;

namespace TianWen.Lib.Imaging.Planetary;

/// <summary>
/// A master's blur, to put a sharper target at the master's resolution: a core Gaussian and, optionally, the limb fit's wide second
/// Gaussian carrying <paramref name="HaloFraction"/> of the light (<see cref="LimbFit.HaloFraction"/>, which sits at its bound on every
/// capture, so whether it is the stack's blur is measured, not assumed: #1212).
/// </summary>
public readonly record struct PlanetaryBlur(double CoreSigmaPx, double HaloFraction = 0, double HaloSigmaPx = 0)
{
    /// <summary>No blur at all.</summary>
    public static PlanetaryBlur None => default;

    /// <summary>A limb fit's blur, its core alone or with its halo.</summary>
    public static PlanetaryBlur Of(in LimbFit fit, bool withHalo)
        => withHalo ? new PlanetaryBlur(fit.PsfSigma, fit.HaloFraction, fit.HaloWidth) : new PlanetaryBlur(fit.PsfSigma);

    /// <summary><paramref name="plane"/> through this blur, a new plane (the same one when there is none).</summary>
    public float[] Apply(float[] plane, int width, int height)
    {
        var core = CoreSigmaPx > 0 ? Gaussian(CoreSigmaPx).Convolve(plane, width, height) : plane;
        if (HaloFraction <= 0 || HaloSigmaPx <= 0)
        {
            return core;
        }
        var halo = Gaussian(HaloSigmaPx).Convolve(plane, width, height);
        var blurred = new float[plane.Length];
        for (var i = 0; i < blurred.Length; i++)
        {
            blurred[i] = (float)(((1 - HaloFraction) * core[i]) + (HaloFraction * halo[i]));
        }
        return blurred;
    }

    private static PsfKernel Gaussian(double sigmaPx) => PsfKernel.Gaussian(sigmaPx * 2 * Math.Sqrt(2 * Math.Log(2)));
}

/// <summary>A colour in linear RGB: a disk's mean, a band's, or a target's.</summary>
public readonly record struct LinearRgb(double R, double G, double B)
{
    /// <summary>The chromaticity's red coordinate, R over the sum.</summary>
    public double ChromaR => R / (R + G + B);

    /// <summary>The chromaticity's green coordinate, G over the sum.</summary>
    public double ChromaG => G / (R + G + B);

    /// <summary>Whether the colour has a chromaticity: every channel finite and their sum above zero.</summary>
    public bool HasChroma => double.IsFinite(R) && double.IsFinite(G) && double.IsFinite(B) && R + G + B > 0;

    /// <summary>The larger of the two chromaticity coordinates' differences from <paramref name="other"/>, the distance #1212's rule reads.</summary>
    public double ChromaDistance(in LinearRgb other) => Math.Max(Math.Abs(ChromaR - other.ChromaR), Math.Abs(ChromaG - other.ChromaG));

    /// <summary>One gain a channel, green held at one, taking this colour to <paramref name="target"/>'s chromaticity.</summary>
    public LinearRgb GainsTo(in LinearRgb target) => new LinearRgb(target.R / target.G * (G / R), 1, target.B / target.G * (G / B));
}

/// <summary>
/// One of OPAL's visible filters on WFC3/UVIS as an apparition's readme lists it: its pivot wavelength, the Minnaert k its maps were
/// flattened with, and the factor from a map's FITS values to I/F (<see cref="PlanetaryColour.ReadmeFilters"/>). Every apparition
/// has its own factors, Saturn's changing by up to 15 % between years, so they are read, never kept as constants (S6, #1235).
/// </summary>
public readonly record struct OpalFilter(string Name, double PivotNm, double MinnaertK, double IfScale);

/// <summary>
/// OPAL's maps of one apparition in its visible filters, bluest first, each filter's maps one a rotation (2024c and 2024d are the two
/// rotations of November 2024), as the FITS files hold them: each is scaled to I/F by its filter's <see cref="OpalFilter.IfScale"/>
/// as it is read.
/// </summary>
public sealed record OpalApparition(int Year, ImmutableArray<OpalFilter> Filters, ImmutableArray<ImmutableArray<PlanetMap>> Maps)
{
    /// <summary>The filters' pivot wavelengths, nm.</summary>
    public double[] PivotsNm()
    {
        var pivots = new double[Filters.Length];
        for (var f = 0; f < Filters.Length; f++)
        {
            pivots[f] = Filters[f].PivotNm;
        }
        return pivots;
    }
}

/// <summary>A reflectance target's colour both ways the spectrum is joined between OPAL's filters, and the per-filter I/F behind it.</summary>
/// <param name="Linear">The spectrum joined linearly.</param>
/// <param name="Cubic">The spectrum joined by a monotone cubic.</param>
/// <param name="IfMeans">Each filter's disk-mean I/F, bluest first.</param>
/// <param name="RotationRange">The largest spread, over the central meridians, of any filter's disk mean over its mean.</param>
public readonly record struct ExpectedColour(LinearRgb Linear, LinearRgb Cubic, double[] IfMeans, double RotationRange)
{
    /// <summary>The target: the two joins' mean.</summary>
    public LinearRgb Value => new LinearRgb((Linear.R + Cubic.R) / 2, (Linear.G + Cubic.G) / 2, (Linear.B + Cubic.B) / 2);

    /// <summary>The method's uncertainty: how far the two joins' chromaticities lie apart.</summary>
    public double Uncertainty => Linear.ChromaDistance(Cubic);
}

/// <summary>
/// A planet's colour, measured on a master and expected from a reference (#1212, docs/plans/planetary-restoration.md, "A planetary
/// master's colour"). A disk's colour is the mean LINEAR RGB inside <see cref="DiskRadii"/>, each channel's sky taken off; its chroma
/// spread is how far the colour of 2-degree bands of planetographic latitude strays from their mean, inside <see cref="SpreadRadii"/>.
/// The physical target takes a reflectance spectrum, sampled by OPAL's filters, under D65 through the CIE 1931 observer into linear
/// sRGB, so a white reflector renders white.
/// </summary>
public static class PlanetaryColour
{
    /// <summary>The disk's mean colour is read inside this many radii, clear of the limb's blur.</summary>
    public const double DiskRadii = 0.9;

    /// <summary>The latitude bands are read inside this many radii.</summary>
    public const double SpreadRadii = 0.8;

    /// <summary>The width of a band of planetographic latitude, in degrees.</summary>
    public const double BandDegrees = 2;

    /// <summary>
    /// OPAL's filters in the visible, bluest first, with their WFC3/UVIS pivot wavelengths, nm. Jupiter's maps take F658N at the red end,
    /// Saturn's F763M, which the observer barely sees but which keeps the spectrum past F631N from being held flat.
    /// </summary>
    public static ImmutableArray<(string Name, double PivotNm)> OpalVisiblePivots { get; } =
        [("F395N", 395.3), ("F467M", 468.3), ("F502N", 501.0), ("F631N", 630.4), ("F658N", 656.4), ("F763M", 762.3)];

    /// <summary>
    /// The visible filters an OPAL readme's table lists (a filter's name, its Minnaert k and the factor from a map's FITS values to I/F,
    /// one a row), bluest first; a row naming a filter outside <see cref="OpalVisiblePivots"/> is passed over.
    /// </summary>
    public static ImmutableArray<OpalFilter> ReadmeFilters(IEnumerable<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var found = new Dictionary<string, OpalFilter>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines)
        {
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3)
            {
                continue;
            }
            foreach (var (name, pivotNm) in OpalVisiblePivots)
            {
                if (string.Equals(name, parts[0], StringComparison.OrdinalIgnoreCase)
                    && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var k)
                    && double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var scale))
                {
                    found.TryAdd(name, new OpalFilter(name, pivotNm, k, scale));
                }
            }
        }
        var filters = ImmutableArray.CreateBuilder<OpalFilter>();
        foreach (var (name, _) in OpalVisiblePivots)
        {
            if (found.TryGetValue(name, out var filter))
            {
                filters.Add(filter);
            }
        }
        return filters.ToImmutable();
    }

    /// <summary>Each plane's mean inside <see cref="DiskRadii"/> of <paramref name="disk"/> but where a ring covers it, its sky (<paramref name="sky"/>) taken off.</summary>
    public static LinearRgb DiskMean(ReadOnlySpan<float> red, ReadOnlySpan<float> green, ReadOnlySpan<float> blue, int width, int height,
        in MetricDisk disk, in LinearRgb sky)
    {
        double r = 0, g = 0, b = 0;
        var n = 0;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                // Saturn's globe is read where its rings leave it clear (S6, #1235).
                if (disk.RadiiAt(x, y) >= DiskRadii || disk.RingTouched(x, y))
                {
                    continue;
                }
                var i = (y * width) + x;
                if (!float.IsFinite(red[i]) || !float.IsFinite(green[i]) || !float.IsFinite(blue[i]))
                {
                    continue;
                }
                r += red[i];
                g += green[i];
                b += blue[i];
                n++;
            }
        }
        return n == 0 ? new LinearRgb(double.NaN, double.NaN, double.NaN) : new LinearRgb((r / n) - sky.R, (g / n) - sky.G, (b / n) - sky.B);
    }

    /// <summary>Each plane's sky level about <paramref name="disk"/> (<see cref="PlanetaryMetrics.SkyLevel"/>), zero where it has none.</summary>
    public static LinearRgb Sky(ReadOnlySpan<float> red, ReadOnlySpan<float> green, ReadOnlySpan<float> blue, int width, int height, in MetricDisk disk)
        => new LinearRgb(PlanetaryMetrics.SkyLevel(red, width, height, disk) ?? 0, PlanetaryMetrics.SkyLevel(green, width, height, disk) ?? 0,
            PlanetaryMetrics.SkyLevel(blue, width, height, disk) ?? 0);

    /// <summary>
    /// Each plane's mean in bands of <see cref="BandDegrees"/> of planetographic latitude inside <see cref="SpreadRadii"/>, its sky taken
    /// off, with each band's pixel count; the planes are any number (a master's three, or one a filter).
    /// </summary>
    public static (double[][] Means, int[] Counts) LatitudeBands(IReadOnlyList<float[]> planes, ReadOnlySpan<double> sky, int width, int height,
        in MetricDisk disk, in PlanetaryProjection projection)
    {
        var bands = (int)Math.Round(180 / BandDegrees);
        var sums = new double[planes.Count][];
        for (var c = 0; c < planes.Count; c++)
        {
            sums[c] = new double[bands];
        }
        var counts = new int[bands];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                if (disk.RadiiAt(x, y) >= SpreadRadii || disk.RingTouched(x, y) || !projection.TryUnproject(x, y, out var latitude, out _))
                {
                    continue;
                }
                var i = (y * width) + x;
                var finite = true;
                for (var c = 0; c < planes.Count && finite; c++)
                {
                    finite = float.IsFinite(planes[c][i]);
                }
                if (!finite)
                {
                    continue;
                }
                var band = Math.Clamp((int)((latitude + 90) / BandDegrees), 0, bands - 1);
                counts[band]++;
                for (var c = 0; c < planes.Count; c++)
                {
                    sums[c][band] += planes[c][i] - sky[c];
                }
            }
        }
        for (var c = 0; c < planes.Count; c++)
        {
            for (var band = 0; band < bands; band++)
            {
                sums[c][band] = counts[band] > 0 ? sums[c][band] / counts[band] : double.NaN;
            }
        }
        return (sums, counts);
    }

    /// <summary>
    /// The chroma spread: the pixel-weighted RMS distance of the bands' chromaticity from that of their pixel-weighted mean colour, over
    /// the bands with a chromaticity. A planet of one colour has none, however its brightness varies.
    /// </summary>
    public static double ChromaSpread(ReadOnlySpan<LinearRgb> bands, ReadOnlySpan<int> counts)
    {
        double r = 0, g = 0, b = 0;
        long n = 0;
        for (var i = 0; i < bands.Length; i++)
        {
            if (counts[i] > 0 && bands[i].HasChroma)
            {
                r += bands[i].R * counts[i];
                g += bands[i].G * counts[i];
                b += bands[i].B * counts[i];
                n += counts[i];
            }
        }
        if (n == 0)
        {
            return double.NaN;
        }
        var mean = new LinearRgb(r / n, g / n, b / n);
        double squares = 0;
        for (var i = 0; i < bands.Length; i++)
        {
            if (counts[i] > 0 && bands[i].HasChroma)
            {
                var (dr, dg) = (bands[i].ChromaR - mean.ChromaR, bands[i].ChromaG - mean.ChromaG);
                squares += counts[i] * ((dr * dr) + (dg * dg));
            }
        }
        return Math.Sqrt(squares / n);
    }

    /// <summary>
    /// The linear sRGB of a reflectance spectrum sampled at <paramref name="wavelengthsNm"/> (ascending), taken under D65 through the CIE
    /// 1931 observer and scaled so a white reflector renders (1, 1, 1). Between the samples the spectrum is joined linearly or by a
    /// monotone cubic (Fritsch and Carlson's, with shape-preserving ends), and held flat past the end samples.
    /// </summary>
    public static LinearRgb SrgbOfReflectance(ReadOnlySpan<double> wavelengthsNm, ReadOnlySpan<double> reflectance, bool monotoneCubic)
    {
        if (wavelengthsNm.Length != reflectance.Length || wavelengthsNm.Length < 2)
        {
            throw new ArgumentException("A spectrum needs at least two samples, one reflectance a wavelength.", nameof(reflectance));
        }
        var nm = wavelengthsNm.ToArray();
        var rho = reflectance.ToArray();
        var slopes = monotoneCubic ? MonotoneSlopes(nm, rho) : null;
        var white = Srgb(Tristimulus(_ => 1));
        var (r, g, b) = Srgb(Tristimulus(lambda => Interpolate(nm, rho, slopes, lambda)));
        return new LinearRgb(r / white.R, g / white.G, b / white.B);
    }

    /// <summary>
    /// The disk-mean I/F of an OPAL map rendered at <paramref name="aspect"/>'s sub-observer latitude and phase, its limb darkening put
    /// back with <paramref name="minnaertK"/>, at central meridians every <paramref name="stepDeg"/> degrees: their mean and range, the colour
    /// the planet shows whichever side faces us, and how much that depends on the side.
    /// </summary>
    public static (double Mean, double Min, double Max) RotationMeanIf(PlanetMap map, double ifScale, double minnaertK, in PlanetAspect aspect,
        double stepDeg = 30)
    {
        const int size = 160;
        const double radius = 70;
        var placement = new DiskPlacement((size - 1) / 2.0, (size - 1) / 2.0, radius, -90);
        var options = PlanetaryLimbFit.OptionsFor(aspect);
        // Saturn's globe as a master shows it: its rings drawn for the shadow they cast, and the globe read where they leave it clear.
        var disk = new MetricDisk(placement.CenterX, placement.CenterY, radius, options.AxisRatio, placement.NorthAngleDeg)
        {
            Rings = options.Rings is { } ringed ? DiskRings.Of(placement.NorthAngleDeg, placement.NorthAngleDeg, options, ringed) : null,
        };
        double sum = 0, min = double.PositiveInfinity, max = double.NegativeInfinity;
        var n = 0;
        for (var cm = 0.0; cm < 360; cm += stepDeg)
        {
            var render = PlanetaryRender.Render(map, aspect.TurnedTo(cm), placement, size, size, minnaertK, rings: options.Rings);
            var mean = ifScale * DiskMean(render, render, render, size, size, disk, default).R;
            sum += mean;
            min = Math.Min(min, mean);
            max = Math.Max(max, mean);
            n++;
        }
        return (sum / n, min, max);
    }

    /// <summary>
    /// The colour <paramref name="apparition"/>'s maps give the disk at <paramref name="aspect"/>'s sub-observer latitude and phase,
    /// averaged over a rotation and over the apparition's rotations (<see cref="RotationMeanIf"/>).
    /// </summary>
    public static ExpectedColour ExpectedDiskColour(OpalApparition apparition, in PlanetAspect aspect)
    {
        var means = new double[apparition.Filters.Length];
        var range = 0.0;
        for (var f = 0; f < apparition.Filters.Length; f++)
        {
            var filter = apparition.Filters[f];
            foreach (var map in apparition.Maps[f])
            {
                var (mean, min, max) = RotationMeanIf(map, filter.IfScale, filter.MinnaertK, aspect);
                means[f] += mean / apparition.Maps[f].Length;
                range = Math.Max(range, (max - min) / mean);
            }
        }
        var pivots = apparition.PivotsNm();
        return new ExpectedColour(SrgbOfReflectance(pivots, means, monotoneCubic: false), SrgbOfReflectance(pivots, means, monotoneCubic: true),
            means, range);
    }

    /// <summary>
    /// The colour of each latitude band <paramref name="apparition"/>'s maps give a disk placed and seen as a master's, once through
    /// each of <paramref name="blurs"/>: its maps rendered at <paramref name="aspect"/> and averaged over the apparition's rotations,
    /// blurred, banded (<see cref="LatitudeBands"/>) and each band's spectrum taken through the observer, joined by a monotone cubic.
    /// </summary>
    public static (LinearRgb[] Bands, int[] Counts)[] ExpectedBands(OpalApparition apparition, in PlanetAspect aspect, in DiskPlacement placement,
        int width, int height, in MetricDisk disk, IReadOnlyList<PlanetaryBlur> blurs)
    {
        var rendered = new float[apparition.Filters.Length][];
        for (var f = 0; f < apparition.Filters.Length; f++)
        {
            var filter = apparition.Filters[f];
            var plane = rendered[f] = new float[width * height];
            foreach (var map in apparition.Maps[f])
            {
                var render = PlanetaryRender.Render(map, aspect, placement, width, height, filter.MinnaertK, rings: PlanetaryLimbFit.OptionsFor(aspect).Rings);
                var scale = (float)(filter.IfScale / apparition.Maps[f].Length);
                for (var i = 0; i < plane.Length; i++)
                {
                    plane[i] += render[i] * scale;
                }
            }
        }
        var projection = new PlanetaryProjection(aspect, placement);
        var pivots = apparition.PivotsNm();
        var results = new (LinearRgb[] Bands, int[] Counts)[blurs.Count];
        for (var k = 0; k < blurs.Count; k++)
        {
            var planes = new float[rendered.Length][];
            for (var f = 0; f < rendered.Length; f++)
            {
                planes[f] = blurs[k].Apply(rendered[f], width, height);
            }
            var (means, counts) = LatitudeBands(planes, new double[planes.Length], width, height, disk, projection);
            var bands = new LinearRgb[counts.Length];
            var spectrum = new double[planes.Length];
            for (var b = 0; b < bands.Length; b++)
            {
                for (var f = 0; f < planes.Length; f++)
                {
                    spectrum[f] = means[f][b];
                }
                bands[b] = counts[b] > 0 ? SrgbOfReflectance(pivots, spectrum, monotoneCubic: true) : new LinearRgb(double.NaN, double.NaN, double.NaN);
            }
            results[k] = (bands, counts);
        }
        return results;
    }

    /// <summary>The colour of each band of three planes' <see cref="LatitudeBands"/>.</summary>
    public static (LinearRgb[] Bands, int[] Counts) RgbBands(float[] red, float[] green, float[] blue, in LinearRgb sky, int width, int height,
        in MetricDisk disk, in PlanetaryProjection projection)
    {
        var (means, counts) = LatitudeBands([red, green, blue], [sky.R, sky.G, sky.B], width, height, disk, projection);
        var bands = new LinearRgb[counts.Length];
        for (var b = 0; b < bands.Length; b++)
        {
            bands[b] = new LinearRgb(means[0][b], means[1][b], means[2][b]);
        }
        return (bands, counts);
    }

    /// <summary>
    /// The planes of an sRGB-encoded picture (a PNG off the web, such as the OPAL composite) as LINEAR sRGB in [0, 1], box-averaged
    /// by <paramref name="bin"/> on each axis: decoded through the sRGB transfer function before any averaging, since averaging code
    /// values would darken every edge.
    /// </summary>
    public static (float[] R, float[] G, float[] B, int Width, int Height) LinearFromSrgb(Image image, int bin = 1)
    {
        if (image.ChannelCount < 3)
        {
            throw new ArgumentException("An sRGB picture has three channels.", nameof(image));
        }
        var divisor = image.UnitScaleDivisor;
        var (width, height) = (image.Width / bin, image.Height / bin);
        var planes = new float[3][];
        for (var c = 0; c < 3; c++)
        {
            var source = image.GetChannelSpan(c);
            var plane = planes[c] = new float[width * height];
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    double sum = 0;
                    for (var dy = 0; dy < bin; dy++)
                    {
                        for (var dx = 0; dx < bin; dx++)
                        {
                            sum += Bt2020Pq.SrgbEotf(source[(((y * bin) + dy) * image.Width) + (x * bin) + dx] / divisor);
                        }
                    }
                    plane[(y * width) + x] = (float)(sum / (bin * bin));
                }
            }
        }
        return (planes[0], planes[1], planes[2], width, height);
    }

    /// <summary>
    /// <paramref name="plane"/> shrunk by <paramref name="factor"/> (at most one) by area: each output pixel the mean of the input pixels
    /// whose centres fall in it, so a disk at radius R in the input lies at R times the factor, its centre at (c + 0.5) times the
    /// factor less a half, as <see cref="PlacedAt"/> says.
    /// </summary>
    public static (float[] Plane, int Width, int Height) Shrink(ReadOnlySpan<float> plane, int width, int height, double factor)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(factor, 1);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(factor);
        var (outWidth, outHeight) = ((int)Math.Ceiling(width * factor), (int)Math.Ceiling(height * factor));
        var sums = new double[outWidth * outHeight];
        var counts = new int[outWidth * outHeight];
        for (var y = 0; y < height; y++)
        {
            var oy = Math.Min((int)((y + 0.5) * factor), outHeight - 1);
            for (var x = 0; x < width; x++)
            {
                var o = (oy * outWidth) + Math.Min((int)((x + 0.5) * factor), outWidth - 1);
                sums[o] += plane[(y * width) + x];
                counts[o]++;
            }
        }
        var shrunk = new float[sums.Length];
        for (var i = 0; i < shrunk.Length; i++)
        {
            shrunk[i] = counts[i] > 0 ? (float)(sums[i] / counts[i]) : 0;
        }
        return (shrunk, outWidth, outHeight);
    }

    /// <summary>Where a disk placed at <paramref name="placement"/> lies once its picture is shrunk by <paramref name="factor"/> (<see cref="Shrink"/>).</summary>
    public static DiskPlacement PlacedAt(in DiskPlacement placement, double factor) => placement with
    {
        CenterX = ((placement.CenterX + 0.5) * factor) - 0.5,
        CenterY = ((placement.CenterY + 0.5) * factor) - 0.5,
        EquatorialRadius = placement.EquatorialRadius * factor,
    };

    // X, Y, Z of a reflectance under D65 through the CIE 1931 2-degree observer, Y of a white reflector one: CieReferenceData's 5 nm
    // tabulation, every curve on one grid.
    private static (double X, double Y, double Z) Tristimulus(Func<double, double> reflectanceAtNm)
    {
        var (xBar, yBar, zBar, d65) = (CieReferenceData.X1931, CieReferenceData.Y1931, CieReferenceData.Z1931, CieReferenceData.D65);
        double x = 0, y = 0, z = 0, white = 0;
        for (var i = 0; i < d65.Count; i++)
        {
            var light = d65.Throughputs[i];
            var reflected = light * reflectanceAtNm(d65.Wavelengths[i] / 10);
            x += reflected * xBar.Throughputs[i];
            y += reflected * yBar.Throughputs[i];
            z += reflected * zBar.Throughputs[i];
            white += light * yBar.Throughputs[i];
        }
        return (x / white, y / white, z / white);
    }

    // Linear sRGB from XYZ-D65: the inverse of the sRGB primaries CameraColorMatrix holds.
    private static LinearRgb Srgb((double X, double Y, double Z) xyz)
    {
        var m = CameraColorMatrix.SrgbToXyz;
        var (a, b, c, d, e, f, g, h, k) = (m[0], m[1], m[2], m[3], m[4], m[5], m[6], m[7], m[8]);
        var det = (a * ((e * k) - (f * h))) - (b * ((d * k) - (f * g))) + (c * ((d * h) - (e * g)));
        var (x, y, z) = xyz;
        return new LinearRgb(
            ((((e * k) - (f * h)) * x) + (((c * h) - (b * k)) * y) + (((b * f) - (c * e)) * z)) / det,
            ((((f * g) - (d * k)) * x) + (((a * k) - (c * g)) * y) + (((c * d) - (a * f)) * z)) / det,
            ((((d * h) - (e * g)) * x) + (((b * g) - (a * h)) * y) + (((a * e) - (b * d)) * z)) / det);
    }

    // The value at `lambda` of the samples joined linearly (no slopes) or as a cubic Hermite through the given slopes, held flat past the ends.
    internal static double Interpolate(double[] nm, double[] rho, double[]? slopes, double lambda)
    {
        if (lambda <= nm[0])
        {
            return rho[0];
        }
        if (lambda >= nm[^1])
        {
            return rho[^1];
        }
        var k = 0;
        while (lambda > nm[k + 1])
        {
            k++;
        }
        var h = nm[k + 1] - nm[k];
        var t = (lambda - nm[k]) / h;
        if (slopes is null)
        {
            return rho[k] + (t * (rho[k + 1] - rho[k]));
        }
        var (t2, t3) = (t * t, t * t * t);
        return (((2 * t3) - (3 * t2) + 1) * rho[k]) + ((t3 - (2 * t2) + t) * h * slopes[k]) + (((-2 * t3) + (3 * t2)) * rho[k + 1])
            + ((t3 - t2) * h * slopes[k + 1]);
    }

    // Fritsch and Carlson's monotone slopes (the weighted harmonic mean of the neighbouring secants, zero at a turn), with the
    // shape-preserving three-point ends SciPy's PCHIP uses: the cubic never overshoots a sample.
    internal static double[] MonotoneSlopes(double[] nm, double[] rho)
    {
        var n = nm.Length;
        var h = new double[n - 1];
        var secant = new double[n - 1];
        for (var k = 0; k < n - 1; k++)
        {
            h[k] = nm[k + 1] - nm[k];
            secant[k] = (rho[k + 1] - rho[k]) / h[k];
        }
        var slopes = new double[n];
        if (n == 2)
        {
            slopes[0] = slopes[1] = secant[0];
            return slopes;
        }
        for (var k = 1; k < n - 1; k++)
        {
            if (secant[k - 1] * secant[k] <= 0)
            {
                continue;
            }
            var (w1, w2) = ((2 * h[k]) + h[k - 1], h[k] + (2 * h[k - 1]));
            slopes[k] = (w1 + w2) / ((w1 / secant[k - 1]) + (w2 / secant[k]));
        }
        slopes[0] = EndSlope(h[0], h[1], secant[0], secant[1]);
        slopes[n - 1] = EndSlope(h[n - 2], h[n - 3], secant[n - 2], secant[n - 3]);
        return slopes;
    }

    private static double EndSlope(double h0, double h1, double s0, double s1)
    {
        var slope = (((2 * h0) + h1) * s0 - (h0 * s1)) / (h0 + h1);
        if (Math.Sign(slope) != Math.Sign(s0))
        {
            return 0;
        }
        return Math.Sign(s0) != Math.Sign(s1) && Math.Abs(slope) > Math.Abs(3 * s0) ? 3 * s0 : slope;
    }
}
