using System;
using System.Runtime.InteropServices;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.Catalogs;

namespace TianWen.Lib.Imaging.Planetary;

/// <summary>Where one colour lay against green, in mosaic pixels: its content sits at (x + <c>Dx</c>, y + <c>Dy</c>).</summary>
public readonly record struct PlanetaryChannelShift(double Dx, double Dy)
{
    /// <summary>How far, px.</summary>
    public double Length => Math.Sqrt((Dx * Dx) + (Dy * Dy));

    /// <inheritdoc/>
    public override string ToString() => $"{Signed(Dx)}, {Signed(Dy)} px";

    // Rounded first: a negative that rounds to zero took the positive section and a minus sign of its own ("-+0.00").
    private static string Signed(double value)
    {
        var rounded = Math.Round(value, 2);
        return rounded == 0 ? "+0.00" : FormattableString.Invariant($"{rounded:+0.00;-0.00}");
    }
}

/// <summary>How a colour's offset from green was read.</summary>
public enum PlanetaryChannelReading
{
    /// <summary>Each colour's disk fitted at its limb with the planet's ephemeris (<see cref="PlanetaryLimbFit"/>): the geometry.</summary>
    Limb,

    /// <summary>A plain correlation over the disk, its peak climbed: where the planet or its instant is unknown, or a fit failed.</summary>
    Correlation,
}

/// <summary>
/// What <see cref="PlanetaryChannelAlignment.Align"/> read and did: red and blue against green, how they were read, and for a
/// split-CFA master the second green against the first (<see cref="GreenCheck"/>, zero when the photosites' phase was taken out
/// right). Not <see cref="Applied"/> when a reading was beyond what dispersion can be (<see cref="Refusal"/> says which), and the
/// master was left as stacked.
/// </summary>
public sealed record PlanetaryChannelAlignmentResult(PlanetaryChannelShift Red, PlanetaryChannelShift Blue, PlanetaryChannelShift? GreenCheck,
    PlanetaryChannelReading Reading, bool Applied, string? Refusal = null)
{
    /// <summary>What was read and done, in words: ONE wording for <c>planetary stack</c> and a live view's derivation.</summary>
    public string Describe()
    {
        var greens = GreenCheck is { } check ? FormattableString.Invariant($"; the greens {check.Length:0.00} px apart") : "";
        return Applied
            ? $"colours moved onto green, read by {(Reading == PlanetaryChannelReading.Limb ? "their limbs" : "correlation")}: red was at {Red}, blue at {Blue}{greens}"
            : $"colours left as stacked: {Refusal} (red {Red}, blue {Blue}{greens})";
    }
}

/// <summary>
/// Aligns a colour master's planes onto green, as AutoStakkert's RGB align does (docs/plans/planetary-restoration.md, "A colour
/// master's planes aligned onto each other", #1202). The atmosphere's dispersion moves each colour's image along the vertical,
/// and the stacker registers every frame by its luminance, so the colours stay apart in the master: a coloured fringe at the limb.
/// <para>
/// A split-CFA master is aligned on its four stacked sub-planes, before the demosaic, where each holds one colour as sampled, at a
/// quarter of the pixels. A sub-plane samples the sky at its own photosite, half a sub-plane pixel from its neighbours, so a reading
/// of its position carries that phase too; it is known and taken out. Each colour is read against both greens and the two
/// averaged, and the greens against each other, which reads zero when the arithmetic is right. A three-plane master (an RGB
/// source, or a drizzled one, whose samples land at their own photosites) is aligned as it is.
/// </para>
/// <para>
/// Each colour's offset is read from its own limb fit, the planet's ephemeris giving its shape and lighting, since each colour sees
/// its own belts and poles: a correlation, or any measure that weighs brightness, takes a colour's north-south albedo difference for a
/// shift. On R5a's colour twin, whose dispersion is known, correlation read blue 0.45 px too far along the planet's axis. Where the
/// planet or its instant is unknown, or a fit fails, a plain correlation over the disk is read instead, its peak climbed
/// (<see cref="CorrelationRegistrar"/>). Each plane is moved by the stack's own resampling, Lanczos-3 clamped. Green stays where the
/// stack put it.
/// </para>
/// </summary>
public static class PlanetaryChannelAlignment
{
    /// <summary>A reading longer than this fraction of the disk's radius is not dispersion, and the master is left as stacked.</summary>
    public const double MaxShiftOfRadius = 0.25;

    /// <summary>The greens disagreeing by more than this, px, says the photosites are not where the master's Bayer offsets put them.</summary>
    public const double MaxGreenDisagreement = 0.5;

    /// <summary>
    /// The limb fit's options for <paramref name="planet"/> at <paramref name="instant"/>, or null where the colours are read by
    /// correlation instead: no planet, one without a rotation model, or no instant. Saturn's carry its rings (S5, #1234).
    /// </summary>
    public static LimbFitOptions? LimbOptionsFor(CatalogIndex? planet, DateTimeOffset? instant)
        => planet is { } body && PhysicalEphemeris.Supports(body) && instant is { } when
            ? PlanetaryLimbFit.OptionsFor(PhysicalEphemeris.Compute(body, when))
            : null;

    /// <summary>
    /// <paramref name="stacked"/> with its colour planes moved onto green, and what was read; the image itself and null for a mono
    /// master. A split-CFA master (<see cref="PlanetaryFrameLayout.SplitCfa"/>, four sub-planes) is aligned as sub-planes and stays
    /// four, for the demosaic to follow. The colours are read by their limbs under <paramref name="limb"/> (<see cref="LimbOptionsFor"/>),
    /// else by correlation. The green planes are shared with <paramref name="stacked"/>, the moved ones are new.
    /// </summary>
    public static (Image Aligned, PlanetaryChannelAlignmentResult? Result) Align(Image stacked, PlanetaryFrameLayout layout, LimbFitOptions? limb = null)
    {
        ArgumentNullException.ThrowIfNull(stacked);
        var split = layout == PlanetaryFrameLayout.SplitCfa && stacked.ChannelCount == 4;
        if (!split && stacked.ChannelCount != 3)
        {
            return (stacked, null);
        }

        // Each plane's photosite in the 2 x 2 cell, [R, G1, G2, B] (Image.SplitBayerChannels); a full plane's pixel is the mosaic's.
        var ox = stacked.ImageMeta.BayerOffsetX & 1;
        var oy = stacked.ImageMeta.BayerOffsetY & 1;
        (int X, int Y)[] phases = split ? [(ox, oy), (1 - ox, oy), (ox, 1 - oy), (1 - ox, 1 - oy)] : [(0, 0), (0, 0), (0, 0)];
        var step = split ? 2 : 1;
        if (PlanetaryLimbFit.Start(stacked.GetChannelSpan(1), stacked.Width, stacked.Height, limb?.AxisRatio ?? 1) is not { } disk)
        {
            return (stacked, new PlanetaryChannelAlignmentResult(default, default, null, PlanetaryChannelReading.Correlation, Applied: false,
                "no disk stands out of the sky"));
        }

        // A ringed fit starts from the rings' reach, never the bright area, which is the rings' as much as the globe's (S2).
        var limbStart = limb is { Rings: not null } ringed ? PlanetaryLimbFit.StartRinged(stacked.GetChannelSpan(1), stacked.Width, stacked.Height, ringed) : disk;
        var (red, blue, greenCheck, reading) = (limb is { } options && limbStart is { } start ? ByLimb(stacked, start, step, phases, options) : null)
            ?? ByCorrelation(stacked, disk, step, phases);

        string? refusal = null;
        // The disk's radius is in the planes' pixels, a sub-plane's two mosaic pixels.
        var bound = MaxShiftOfRadius * disk.Radius * (split ? 2 : 1);
        if (Math.Max(red.Length, blue.Length) > bound)
        {
            refusal = FormattableString.Invariant($"a colour reads {Math.Max(red.Length, blue.Length):0.0} px off green, more than a quarter of the disk's radius");
        }
        else if (greenCheck is { } check && check.Length > MaxGreenDisagreement)
        {
            refusal = FormattableString.Invariant($"the two greens read {check.Length:0.00} px apart, so the Bayer pattern is not where the master says");
        }
        if (refusal is not null)
        {
            return (stacked, new PlanetaryChannelAlignmentResult(red, blue, greenCheck, reading, Applied: false, refusal));
        }

        return (Apply(stacked, layout, red, blue), new PlanetaryChannelAlignmentResult(red, blue, greenCheck, reading, Applied: true));
    }

    /// <summary>
    /// <paramref name="stacked"/> with its red and blue planes moved onto green by shifts already read (<see cref="Align"/>'s, in mosaic
    /// pixels): a live view's later masters, moved by what its derivation read on one (#1202). A new image; its green planes are
    /// <paramref name="stacked"/>'s.
    /// </summary>
    public static Image Apply(Image stacked, PlanetaryFrameLayout layout, PlanetaryChannelShift red, PlanetaryChannelShift blue)
    {
        ArgumentNullException.ThrowIfNull(stacked);
        var split = layout == PlanetaryFrameLayout.SplitCfa && stacked.ChannelCount == 4;
        if (!split && stacked.ChannelCount != 3)
        {
            throw new ArgumentException($"a {stacked.ChannelCount}-plane {layout} master has no colour planes to move", nameof(stacked));
        }
        // A sub-plane's pixel is two mosaic pixels, so it moves by half the physical shift; it stays at its own photosite.
        var scale = split ? 0.5 : 1.0;
        var planes = new float[stacked.ChannelCount][,];
        for (var c = 0; c < planes.Length; c++)
        {
            planes[c] = stacked.GetChannelArray(c);
        }
        var redIndex = 0;
        var blueIndex = split ? 3 : 2;
        planes[redIndex] = Moved(planes[redIndex], red.Dx * scale, red.Dy * scale);
        planes[blueIndex] = Moved(planes[blueIndex], blue.Dx * scale, blue.Dy * scale);
        return new Image(planes, stacked.BitDepth, stacked.MaxValue, stacked.MinValue, stacked.Pedestal, stacked.ImageMeta);
    }

    private readonly record struct Readings(PlanetaryChannelShift Red, PlanetaryChannelShift Blue, PlanetaryChannelShift? GreenCheck,
        PlanetaryChannelReading Reading);

    // Each plane's disk fitted at its limb from a cold start (a start from green's fit, with its short search, left the twin's
    // sub-planes unconverged), green's first and every other colour lit from the side green's found, so the phase model cannot
    // differ by colour; the other colours are fitted at once. A plane's centre in mosaic pixels is step times its own plus its
    // photosite. Null when any fit fails, for the correlation to read instead.
    private static Readings? ByLimb(Image stacked, (double X, double Y, double Radius) disk, int step, (int X, int Y)[] phases, LimbFitOptions options)
    {
        var (width, height) = (stacked.Width, stacked.Height);
        LimbFit? FitPlane(int channel, LimbFitOptions lighting)
            => PlanetaryLimbFit.Fit(stacked.GetChannelSpan(channel), width, height, disk.X, disk.Y, disk.Radius, lighting);
        if (FitPlane(1, options) is not { Converged: true } green)
        {
            return null;
        }
        var lit = green.SunSide != 0 ? options with { SunSide = green.SunSide } : options;
        var fits = new LimbFit?[phases.Length];
        fits[1] = green;
        ParallelFor.Run(phases.Length, c =>
        {
            if (c != 1)
            {
                fits[c] = FitPlane(c, lit);
            }
        });
        var centres = new (double X, double Y)[phases.Length];
        for (var c = 0; c < phases.Length; c++)
        {
            if (fits[c] is not { Converged: true } fit)
            {
                return null;
            }
            centres[c] = ((step * fit.CenterX) + phases[c].X, (step * fit.CenterY) + phases[c].Y);
        }

        var split = phases.Length == 4;
        var greenX = split ? 0.5 * (centres[1].X + centres[2].X) : centres[1].X;
        var greenY = split ? 0.5 * (centres[1].Y + centres[2].Y) : centres[1].Y;
        var blueIndex = split ? 3 : 2;
        return new Readings(
            new PlanetaryChannelShift(centres[0].X - greenX, centres[0].Y - greenY),
            new PlanetaryChannelShift(centres[blueIndex].X - greenX, centres[blueIndex].Y - greenY),
            split ? new PlanetaryChannelShift(centres[2].X - centres[1].X, centres[2].Y - centres[1].Y) : null,
            PlanetaryChannelReading.Limb);
    }

    // A plain correlation over a power of two about the disk with half its radius of sky each side (so the circular correlation never
    // wraps the disk onto itself): each colour against both greens, averaged, for a split master; against green otherwise.
    private static Readings ByCorrelation(Image stacked, (double X, double Y, double Radius) disk, int step, (int X, int Y)[] phases)
    {
        var size = NextPowerOfTwo((int)Math.Ceiling(3.2 * disk.Radius));
        var x0 = (int)Math.Round(disk.X) - (size / 2);
        var y0 = (int)Math.Round(disk.Y) - (size / 2);
        float[] CropOf(int channel) => SkyFilledCrop(stacked.GetChannelArray(channel), x0, y0, size);

        if (phases.Length == 4)
        {
            var crops = new[] { CropOf(0), CropOf(1), CropOf(2), CropOf(3) };
            var onG1 = new CorrelationRegistrar(crops[1], size);
            var onG2 = new CorrelationRegistrar(crops[2], size);
            return new Readings(
                Mean(Physical(onG1, crops[0], step, phases[0], phases[1]), Physical(onG2, crops[0], step, phases[0], phases[2])),
                Mean(Physical(onG1, crops[3], step, phases[3], phases[1]), Physical(onG2, crops[3], step, phases[3], phases[2])),
                Physical(onG1, crops[2], step, phases[2], phases[1]),
                PlanetaryChannelReading.Correlation);
        }
        var onGreen = new CorrelationRegistrar(CropOf(1), size);
        return new Readings(Physical(onGreen, CropOf(0), step, phases[0], phases[1]), Physical(onGreen, CropOf(2), step, phases[2], phases[1]), null,
            PlanetaryChannelReading.Correlation);
    }

    // The colour's physical shift against green, in mosaic px, from a correlation between two planes whose pixel is `step` mosaic
    // pixels, sampled at photosite phases `colourPhase` and `greenPhase` (both zero for full planes). With the colour's content at
    // x + D, the colour plane's sample s reads green's content at step s + colourPhase - D; the registrar's d puts green's sample
    // s at the colour plane's s + d, so greenPhase = step d + colourPhase - D.
    private static PlanetaryChannelShift Physical(CorrelationRegistrar onGreen, float[] colour, int step, (int X, int Y) colourPhase, (int X, int Y) greenPhase)
    {
        var (_, dx, dy) = onGreen.Register(colour);
        return new PlanetaryChannelShift((step * dx) + colourPhase.X - greenPhase.X, (step * dy) + colourPhase.Y - greenPhase.Y);
    }

    private static PlanetaryChannelShift Mean(PlanetaryChannelShift a, PlanetaryChannelShift b)
        => new PlanetaryChannelShift(0.5 * (a.Dx + b.Dx), 0.5 * (a.Dy + b.Dy));

    // plane(x + dx, y + dy) at every pixel, by the stack's resampling, row bands in parallel (each writes only its own rows).
    internal static float[,] Moved(float[,] plane, double dx, double dy) => Moved(plane, dx, dy, plane.GetLength(1), plane.GetLength(0));

    // The same onto a grid of its own size (a stack cropped to where its frames reached, #1300, moved onto another's): zero where the
    // plane does not reach.
    internal static float[,] Moved(float[,] plane, double dx, double dy, int outWidth, int outHeight)
    {
        var height = plane.GetLength(0);
        var width = plane.GetLength(1);
        var moved = new float[outHeight, outWidth];
        ParallelFor.RunBands(outHeight, (first, end) =>
        {
            var flat = MemoryMarshal.CreateReadOnlySpan(ref plane[0, 0], plane.Length);
            for (var y = first; y < end; y++)
            {
                for (var x = 0; x < outWidth; x++)
                {
                    var v = Image.Lanczos3Value(flat, width, height, (float)(x + dx), (float)(y + dy), Image.LanczosClampingThreshold);
                    moved[y, x] = float.IsNaN(v) ? 0f : v;
                }
            }
        });
        return moved;
    }

    // A size x size crop of the plane at (x0, y0), a pixel beyond the plane reading the plane's sky (the median of its border), so a
    // crop larger than a tightly cut frame has no step at the frame's edge for the correlation to lock onto.
    private static float[] SkyFilledCrop(float[,] plane, int x0, int y0, int size)
    {
        var height = plane.GetLength(0);
        var width = plane.GetLength(1);
        var crop = new float[size * size];
        var inside = x0 >= 0 && y0 >= 0 && x0 + size <= width && y0 + size <= height;
        var sky = inside ? 0f : BorderMedian(plane);
        for (var y = 0; y < size; y++)
        {
            var sy = y + y0;
            for (var x = 0; x < size; x++)
            {
                var sx = x + x0;
                crop[(y * size) + x] = sy >= 0 && sy < height && sx >= 0 && sx < width ? plane[sy, sx] : sky;
            }
        }
        return crop;
    }

    private static float BorderMedian(float[,] plane)
    {
        var height = plane.GetLength(0);
        var width = plane.GetLength(1);
        var border = new float[Math.Max(1, (2 * width) + (2 * Math.Max(0, height - 2)))];
        var n = 0;
        for (var x = 0; x < width; x++)
        {
            border[n++] = plane[0, x];
            if (height > 1)
            {
                border[n++] = plane[height - 1, x];
            }
        }
        for (var y = 1; y < height - 1; y++)
        {
            border[n++] = plane[y, 0];
            if (width > 1)
            {
                border[n++] = plane[y, width - 1];
            }
        }
        var values = border.AsSpan(0, n);
        values.Sort();
        return n == 0 ? 0f : values[n / 2];
    }

    private static int NextPowerOfTwo(int value)
    {
        var p = 32;
        while (p < value)
        {
            p <<= 1;
        }
        return p;
    }
}
