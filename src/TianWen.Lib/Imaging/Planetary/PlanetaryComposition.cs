using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.Catalogs;

namespace TianWen.Lib.Imaging.Planetary;

/// <summary>
/// A mono planetary stack ready to compose (#1278): one plane, the planet it shows, its instant (the capture's middle) and the
/// filter it was taken through, Red, Green, Blue or Luminance (an IR-pass or L stack). <see cref="PlanetaryComposition.Ingest"/>
/// labels one, and the labels travel in its FITS (<c>OBJECT</c>, <c>DATE-OBS</c> with <c>EXPTIME</c> 0, <c>FILTER</c>), so every
/// later step reads them back with <see cref="PlanetaryComposition.FromIngested"/>.
/// </summary>
public sealed record PlanetaryMonoStack(string Name, Image Image, CatalogIndex Planet, DateTimeOffset Instant, Filter Filter);

/// <summary>
/// The registration of a set of stacks (<see cref="PlanetaryComposition.Register"/>): each moved so its disk's centre lies on the
/// reference's, the reference the one <see cref="PlanetaryComposition.ReferenceIndex"/> chooses, and its limb fit.
/// </summary>
public sealed record PlanetaryRegistration(ImmutableArray<PlanetaryMonoStack> Stacks, int ReferenceIndex, LimbFit ReferenceFit);

/// <summary>
/// The de-rotation of a registered set (<see cref="PlanetaryComposition.Derotate"/>): every stack carried to the reference's instant
/// on its disk, the disk's north decided by agreement (<see cref="PlanetaryDerotation.AgreementBothWays"/>), and the two stacks it was
/// decided on (null where the planet turned too little between any two stacks of one filter to tell, and the fit's north was kept).
/// </summary>
public sealed record PlanetaryCompositionDerotation(ImmutableArray<PlanetaryMonoStack> Stacks, DateTimeOffset Instant, DiskPlacement Placement,
    PlanetaryNorthDecision North, (string Earlier, string Later)? DecidedOn);

/// <summary>A composed colour master (R, G, B), and the luminance stacks joined the same way, or null for none.</summary>
public sealed record PlanetaryComposed(Image Master, Image? Luminance);

/// <summary>
/// Planetary compose (#1278, docs/plans/planetary-restoration.md, "Planetary compose: a colour master from mono stacks"): a colour master
/// from a mono camera's stacks, one a filter and several runs of each, the planet turned between them. The planetary twin of the deep-sky
/// composition (<see cref="ColourComposition"/>): every step is one routine, run by a verb of its own through FITS files, and
/// <see cref="Run"/> calls the same routines in order with nothing between them a file would not carry.
/// <list type="number">
/// <item><see cref="Ingest"/>: a stack labelled with its planet, instant and filter, from its header or its WinJUPOS-style name.</item>
/// <item><see cref="Register"/>: each stack's limb fitted at its own instant, the stack moved so its disk's centre lands on the
/// reference's: a rigid move that carries the limb, the rings and the sky with the disk.</item>
/// <item><see cref="Derotate"/>: each stack carried to the reference's instant through the spheroid on the reference's disk
/// (<see cref="PlanetaryDerotation"/>), its north decided by agreement between two stacks of one filter, never by one limb fit.</item>
/// <item><see cref="Join"/>: each filter's stacks averaged, red, green and blue joined into one colour master, the luminance beside it.</item>
/// </list>
/// The colour master then takes the planetary chain a colour master takes: sharpened (<see cref="PlanetarySharpening"/>), balanced to the
/// planet's colour (<see cref="PlanetaryColourBalance"/>), and a look as a rendering.
/// </summary>
public static class PlanetaryComposition
{
    /// <summary>A stack's limb radius may differ from the reference's by this fraction before registration refuses the set: one camera at one focus.</summary>
    public const double MaxRadiusDisagreement = 0.03;

    /// <summary>
    /// Step 1: <paramref name="image"/>, a mono stack read from <paramref name="path"/>, labelled: its planet (given, else its
    /// <c>OBJECT</c>, else its path's name, <see cref="PlanetaryCaptureName.Planet"/>), its instant (given, else its DATE-OBS and
    /// EXPTIME's middle, else its WinJUPOS-style name, <see cref="PlanetaryCaptureName.Instant"/>) and its filter (given, else its
    /// <c>FILTER</c>, else its name: R, G, B, and IR or L as the luminance). Its pixels are kept as they are, as 32-bit floats, so its
    /// FITS carries them exactly. Null with the reason in words when any label cannot be read, or the stack is not one plane.
    /// </summary>
    public static (PlanetaryMonoStack? Stack, string? Refusal) Ingest(string path, Image image, CatalogIndex? planet = null, DateTimeOffset? instant = null,
        Filter? filter = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(image);
        var name = System.IO.Path.GetFileNameWithoutExtension(path);
        if (image.ChannelCount != 1)
        {
            return (null, $"{name}: {image.ChannelCount} planes, where a mono stack has one");
        }
        if ((planet ?? PlanetaryCaptureName.Named(image.ImageMeta.ObjectName) ?? PlanetaryCaptureName.Planet(path)) is not { } body)
        {
            return (null, $"{name}: no planet in its header or its name; give it");
        }
        if (!PhysicalEphemeris.Supports(body))
        {
            return (null, $"{name}: {body} has no rotation model to de-rotate by (Jupiter and Saturn have)");
        }
        if ((instant ?? PlanetaryBestStack.InstantOf(image, epoch: null) ?? PlanetaryCaptureName.Instant(path)) is not { } at)
        {
            return (null, $"{name}: no instant in its header or a WinJUPOS-style name (yyyy-MM-dd-HHmm_t); give it");
        }
        if ((filter ?? RoleOf(image.ImageMeta.Filter) ?? RoleOf(PlanetaryCaptureName.WavelengthNm(path))) is not { } role)
        {
            return (null, $"{name}: no filter in its header or its name (R, G, B, IR or L); give it");
        }
        var plane = image.GetChannelArray(0);
        var meta = image.ImageMeta with
        {
            ObjectName = body.ToString(),
            ExposureStartTime = at,
            ExposureDuration = TimeSpan.Zero,
            Filter = role,
        };
        var (max, min) = Extent([plane]);
        return (new PlanetaryMonoStack(name, new Image([plane], BitDepth.Float32, max, min, 0, meta), body, at, role), null);
    }

    /// <summary>
    /// An ingested stack read back from its own FITS (<paramref name="image"/>, named <paramref name="name"/>): its planet, instant and
    /// filter from its header, as <see cref="Ingest"/> wrote them. Null when a label is missing.
    /// </summary>
    public static PlanetaryMonoStack? FromIngested(string name, Image image)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (image.ChannelCount != 1
            || PlanetaryCaptureName.Named(image.ImageMeta.ObjectName) is not { } planet
            || PlanetaryBestStack.InstantOf(image, epoch: null) is not { } instant
            || RoleOf(image.ImageMeta.Filter) is not { } filter)
        {
            return null;
        }
        return new PlanetaryMonoStack(name, image, planet, instant, filter);
    }

    /// <summary>The composing role of a filter: Red, Green, Blue, or Luminance for an L or IR-pass; null for none.</summary>
    public static Filter? RoleOf(Filter filter) => filter.Name switch
    {
        nameof(Filter.Red) => Filter.Red,
        nameof(Filter.Green) => Filter.Green,
        nameof(Filter.Blue) => Filter.Blue,
        nameof(Filter.Luminance) => Filter.Luminance,
        _ => (Filter?)null,
    };

    /// <summary>The composing role of a filter by its effective wavelength (<see cref="PlanetaryCaptureName.WavelengthNm"/>): 650 red, 530 green, 460 blue, an IR-pass's 750 or an L's 550 the luminance.</summary>
    public static Filter? RoleOf(double? wavelengthNm) => wavelengthNm switch
    {
        650 => Filter.Red,
        530 => Filter.Green,
        460 => Filter.Blue,
        750 or 550 => Filter.Luminance,
        _ => (Filter?)null,
    };

    /// <summary>
    /// Which of <paramref name="stacks"/> the set is registered and de-rotated onto: a green stack, else any colour's, else the
    /// first, nearest the middle of the colour stacks' instants (the set's middle when it holds no colour), so the colours turn least.
    /// </summary>
    public static int ReferenceIndex(IReadOnlyList<PlanetaryMonoStack> stacks)
    {
        ArgumentNullException.ThrowIfNull(stacks);
        var colours = Enumerable.Range(0, stacks.Count).Where(i => stacks[i].Filter != Filter.Luminance).ToArray();
        var over = colours.Length > 0 ? colours : Enumerable.Range(0, stacks.Count).ToArray();
        var middle = Middle(over.Select(i => stacks[i].Instant));
        var greens = over.Where(i => stacks[i].Filter == Filter.Green).ToArray();
        var from = greens.Length > 0 ? greens : over;
        return from.OrderBy(i => Math.Abs((stacks[i].Instant - middle).Ticks)).ThenBy(i => i).First();
    }

    /// <summary>
    /// Step 2: every stack's limb fitted at its own instant (<see cref="PlanetaryLimbFit"/>, Saturn's rings in the model), and the stack
    /// moved so its disk's centre lands on the reference's (<see cref="ReferenceIndex"/>), by the stack's own clamped Lanczos-3 kernel:
    /// a rigid move, so the limb, the rings and the sky move with the disk. Refuses, in words, a set of more than one planet or size, a
    /// limb that does not fit, or radii further apart than <see cref="MaxRadiusDisagreement"/>.
    /// </summary>
    public static (PlanetaryRegistration? Registration, string? Refusal) Register(IReadOnlyList<PlanetaryMonoStack> stacks, Action<string>? say = null)
    {
        ArgumentNullException.ThrowIfNull(stacks);
        if (stacks.Count == 0)
        {
            return (null, "no stacks to register");
        }
        var (planet, width, height) = (stacks[0].Planet, stacks[0].Image.Width, stacks[0].Image.Height);
        if (stacks.FirstOrDefault(s => s.Planet != planet) is { } otherPlanet)
        {
            return (null, $"{otherPlanet.Name} is {otherPlanet.Planet}, where {stacks[0].Name} is {planet}");
        }
        if (stacks.FirstOrDefault(s => s.Image.Width != width || s.Image.Height != height) is { } otherSize)
        {
            return (null, $"{otherSize.Name} is {otherSize.Image.Width}x{otherSize.Image.Height}, where {stacks[0].Name} is {width}x{height}");
        }
        var fits = new LimbFit[stacks.Count];
        for (var i = 0; i < stacks.Count; i++)
        {
            if (FitOf(stacks[i]) is not { } fit)
            {
                return (null, $"{stacks[i].Name}: its limb did not fit");
            }
            fits[i] = fit;
        }
        var reference = ReferenceIndex(stacks);
        var referenceFit = fits[reference];
        var inv = CultureInfo.InvariantCulture;
        var moved = ImmutableArray.CreateBuilder<PlanetaryMonoStack>(stacks.Count);
        for (var i = 0; i < stacks.Count; i++)
        {
            var radiusRatio = fits[i].EquatorialRadius / referenceFit.EquatorialRadius;
            if (Math.Abs(radiusRatio - 1) > MaxRadiusDisagreement)
            {
                return (null, string.Create(inv,
                    $"{stacks[i].Name}: its disk is {radiusRatio:0.000} of {stacks[reference].Name}'s, past {MaxRadiusDisagreement:P0}: not one camera at one scale"));
            }
            var (dx, dy) = (fits[i].CenterX - referenceFit.CenterX, fits[i].CenterY - referenceFit.CenterY);
            // The move as the disk makes it; 0 - d keeps a zero move from printing as minus zero.
            var (moveX, moveY) = (0 - dx, 0 - dy);
            say?.Invoke(string.Create(inv,
                $"{stacks[i].Name} ({stacks[i].Filter.ShortName}, {stacks[i].Instant:HH:mm:ss} UTC): disk R {fits[i].EquatorialRadius:0.00} px, moved ({moveX:+0.00;-0.00;0.00}, {moveY:+0.00;-0.00;0.00}) px{(i == reference ? ", the reference" : "")}"));
            var image = stacks[i].Image;
            var plane = i == reference ? image.GetChannelArray(0) : PlanetaryChannelAlignment.Moved(image.GetChannelArray(0), dx, dy);
            var (max, min) = Extent([plane]);
            moved.Add(stacks[i] with { Image = new Image([plane], BitDepth.Float32, max, min, 0, image.ImageMeta) });
        }
        return (new PlanetaryRegistration(moved.MoveToImmutable(), reference, referenceFit), null);
    }

    /// <summary>
    /// Step 3: a registered set (<see cref="Register"/>, its stacks on one disk) carried to the reference's instant through the spheroid
    /// (<see cref="PlanetaryDerotation.Derotate"/>), the disk the reference's limb fit, refitted here as the registration fitted it. Its
    /// north is decided by agreement: of the filters with two stacks, the pair the planet turned most between, the earlier carried to the
    /// later both ways round (<see cref="PlanetaryDerotation.AgreementBothWays"/>), so stacks of one filter, which show the same detail,
    /// decide it. Under <see cref="PlanetaryDerotation.LeastTurnToTellNorthDeg"/> the fit's north is kept.
    /// </summary>
    public static (PlanetaryCompositionDerotation? Derotation, string? Refusal) Derotate(IReadOnlyList<PlanetaryMonoStack> registered, Action<string>? say = null)
    {
        ArgumentNullException.ThrowIfNull(registered);
        if (registered.Count == 0)
        {
            return (null, "no stacks to de-rotate");
        }
        var reference = ReferenceIndex(registered);
        var planet = registered[reference].Planet;
        if (FitOf(registered[reference]) is not { } fit)
        {
            return (null, $"{registered[reference].Name}: its limb did not fit");
        }
        var instant = registered[reference].Instant;
        var target = PhysicalEphemeris.Compute(planet, instant);
        var fitted = new DiskPlacement(fit.CenterX, fit.CenterY, fit.EquatorialRadius, fit.NorthAngleDeg);
        var k = fit.LimbDarkening;
        var inv = CultureInfo.InvariantCulture;

        // The north: the same-filter pair the planet turned most between.
        (int Earlier, int Later, double Turn)? pair = null;
        for (var i = 0; i < registered.Count; i++)
        {
            for (var j = 0; j < registered.Count; j++)
            {
                if (i == j || registered[i].Filter != registered[j].Filter || registered[i].Instant >= registered[j].Instant)
                {
                    continue;
                }
                var turn = Math.Abs(Math.IEEERemainder(
                    PhysicalEphemeris.Compute(planet, registered[j].Instant).CentralMeridianIII - PhysicalEphemeris.Compute(planet, registered[i].Instant).CentralMeridianIII, 360));
                if (pair is not { } best || turn > best.Turn)
                {
                    pair = (i, j, turn);
                }
            }
        }
        PlanetaryNorthDecision north;
        (string, string)? decidedOn = null;
        if (pair is { } p && p.Turn >= PlanetaryDerotation.LeastTurnToTellNorthDeg)
        {
            var (asFitted, turnedOver) = PlanetaryDerotation.AgreementBothWays(registered[p.Earlier].Image, PhysicalEphemeris.Compute(planet, registered[p.Earlier].Instant),
                registered[p.Later].Image, PhysicalEphemeris.Compute(planet, registered[p.Later].Instant), fitted, k);
            north = new PlanetaryNorthDecision(fit.NorthAngleDeg + (turnedOver < asFitted ? 180 : 0), asFitted, turnedOver);
            decidedOn = (registered[p.Earlier].Name, registered[p.Later].Name);
            say?.Invoke(string.Create(inv,
                $"north decided on {registered[p.Earlier].Name} and {registered[p.Later].Name} ({p.Turn:0.00} degrees apart): RMS apart as fitted {asFitted:0.00000}, turned over {turnedOver:0.00000} -> {(north.TurnedOver ? "turned over" : "as fitted")}, {north.NorthAngleDeg:0.0} degrees"));
        }
        else
        {
            north = new PlanetaryNorthDecision(fit.NorthAngleDeg, double.NaN, double.NaN);
            say?.Invoke(string.Create(inv,
                $"no two stacks of one filter turned {PlanetaryDerotation.LeastTurnToTellNorthDeg:0} degree apart: the limb fit's north kept, {fit.NorthAngleDeg:0.0} degrees"));
        }
        var placement = fitted with { NorthAngleDeg = north.NorthAngleDeg };

        var carried = ImmutableArray.CreateBuilder<PlanetaryMonoStack>(registered.Count);
        for (var i = 0; i < registered.Count; i++)
        {
            var stack = registered[i];
            if (stack.Instant == instant)
            {
                carried.Add(stack);
                continue;
            }
            var from = PhysicalEphemeris.Compute(planet, stack.Instant);
            var derotated = PlanetaryDerotation.Derotate(stack.Image, from, target, placement, k).Image;
            say?.Invoke(string.Create(inv,
                $"{stack.Name}: carried {(instant - stack.Instant).TotalMinutes:+0.0;-0.0} minutes, the central meridian {Math.IEEERemainder(target.CentralMeridianIII - from.CentralMeridianIII, 360):+0.00;-0.00} degrees"));
            // Carried, it shows the planet at the reference's instant, and its header says so for the join's verb to read back.
            carried.Add(stack with { Image = WithMeta(derotated, stack.Image.ImageMeta with { ExposureStartTime = instant }), Instant = instant });
        }
        return (new PlanetaryCompositionDerotation(carried.MoveToImmutable(), instant, placement, north, decidedOn), null);
    }

    /// <summary>
    /// Step 4: a de-rotated set (<see cref="Derotate"/>, every stack at one instant on one disk) joined: each filter's stacks averaged, red,
    /// green and blue as one colour master at that instant, the luminance's (IR or L) averaged the same way beside it, null for none. Refuses,
    /// in words, a set with no red, green or blue, or stacks at more than one instant.
    /// </summary>
    public static (PlanetaryComposed? Composed, string? Refusal) Join(IReadOnlyList<PlanetaryMonoStack> derotated)
    {
        ArgumentNullException.ThrowIfNull(derotated);
        if (derotated.Count == 0)
        {
            return (null, "no stacks to join");
        }
        var instant = derotated[0].Instant;
        if (derotated.FirstOrDefault(s => s.Instant != instant) is { } other)
        {
            return (null, $"{other.Name} is at {other.Instant:HH:mm:ss}, the others at {instant:HH:mm:ss}: de-rotate them to one instant first");
        }
        var planes = new float[3][,];
        Filter[] colours = [Filter.Red, Filter.Green, Filter.Blue];
        for (var c = 0; c < 3; c++)
        {
            if (Mean(derotated, colours[c]) is not { } mean)
            {
                return (null, $"no {colours[c].DisplayName} stack: a colour master needs red, green and blue");
            }
            planes[c] = mean;
        }
        var first = derotated[0].Image;
        var meta = first.ImageMeta with { Filter = Filter.None, SensorType = SensorType.Color };
        var (max, min) = Extent(planes);
        var master = new Image(planes, BitDepth.Float32, max, min, 0, meta);
        Image? luminance = null;
        if (Mean(derotated, Filter.Luminance) is { } l)
        {
            var (lMax, lMin) = Extent([l]);
            luminance = new Image([l], BitDepth.Float32, lMax, lMin, 0, first.ImageMeta with { Filter = Filter.Luminance });
        }
        return (new PlanetaryComposed(master, luminance), null);
    }

    /// <summary>
    /// The recipe: <see cref="Register"/>, <see cref="Derotate"/> and <see cref="Join"/> on ingested stacks, in order, with nothing
    /// between them a file would not carry, so the steps' verbs run one by one give the same master to the bit.
    /// </summary>
    public static (PlanetaryComposed? Composed, PlanetaryCompositionDerotation? Derotation, string? Refusal) Run(IReadOnlyList<PlanetaryMonoStack> ingested,
        Action<string>? say = null)
    {
        var (registration, refusal) = Register(ingested, say);
        if (registration is null)
        {
            return (null, null, refusal);
        }
        var (derotation, derotateRefusal) = Derotate(registration.Stacks, say);
        if (derotation is null)
        {
            return (null, null, derotateRefusal);
        }
        var (composed, joinRefusal) = Join(derotation.Stacks);
        return (composed, derotation, joinRefusal);
    }

    // A stack's limb, fitted at its own instant (Saturn's rings in the model).
    private static LimbFit? FitOf(PlanetaryMonoStack stack)
        => PlanetaryLimbFit.Fit(stack.Image, PlanetaryLimbFit.OptionsFor(PhysicalEphemeris.Compute(stack.Planet, stack.Instant)));

    // The mean of a filter's stacks' planes, or null for none.
    private static float[,]? Mean(IReadOnlyList<PlanetaryMonoStack> stacks, Filter filter)
    {
        var of = stacks.Where(s => s.Filter == filter).ToArray();
        if (of.Length == 0)
        {
            return null;
        }
        var (width, height) = (of[0].Image.Width, of[0].Image.Height);
        var sum = new double[height, width];
        foreach (var stack in of)
        {
            var plane = stack.Image.GetChannelSpan(0);
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    sum[y, x] += plane[(y * width) + x];
                }
            }
        }
        var mean = new float[height, width];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                mean[y, x] = (float)(sum[y, x] / of.Length);
            }
        }
        return mean;
    }

    // An image with another header.
    private static Image WithMeta(Image image, ImageMeta meta)
    {
        var planes = new float[image.ChannelCount][,];
        for (var c = 0; c < planes.Length; c++)
        {
            planes[c] = image.GetChannelArray(c);
        }
        return new Image(planes, BitDepth.Float32, image.MaxValue, image.MinValue, 0, meta);
    }

    // The least and greatest finite sample over the planes, the greatest at least the least.
    private static (float Max, float Min) Extent(float[][,] planes)
    {
        float max = float.NegativeInfinity, min = float.PositiveInfinity;
        foreach (var plane in planes)
        {
            foreach (var v in plane)
            {
                if (float.IsFinite(v))
                {
                    (max, min) = (MathF.Max(max, v), MathF.Min(min, v));
                }
            }
        }
        return float.IsFinite(max) ? (max, min) : (0, 0);
    }

    // The middle of some instants.
    private static DateTimeOffset Middle(IEnumerable<DateTimeOffset> instants)
    {
        var all = instants.ToArray();
        var ticks = all.Aggregate(0.0, (sum, t) => sum + t.UtcTicks) / all.Length;
        return new DateTimeOffset((long)Math.Round(ticks), TimeSpan.Zero);
    }
}
