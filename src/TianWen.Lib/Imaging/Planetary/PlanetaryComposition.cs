using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Stat;

namespace TianWen.Lib.Imaging.Planetary;

/// <summary>
/// A mono planetary stack ready to compose (#1278): one plane, the planet it shows, its instant (the capture's middle) and the
/// filter it was taken through, Red, Green, Blue or Luminance (an IR-pass or L stack). <see cref="PlanetaryComposition.Ingest"/>
/// labels one, and the labels travel in its FITS (<c>OBJECT</c>, <c>DATE-OBS</c> with <c>EXPTIME</c> 0, <c>FILTER</c>), so every
/// later step reads them back with <see cref="PlanetaryComposition.FromIngested"/>.
/// </summary>
public sealed record PlanetaryMonoStack(string Name, Image Image, CatalogIndex Planet, DateTimeOffset Instant, Filter Filter)
{
    /// <summary>
    /// What each pixel of this stack is worth in a join (#1349, fix C; <see cref="PlanetaryDerotation.JoinWeights"/>), row-major, on top
    /// of its noise weight: given by a de-rotation that weighs its stacks by how squarely each saw the planet, written beside the stack as
    /// <c>&lt;name&gt;.weight.fits</c> for the join's verb. Null weighs every pixel alike, as every join before fix C did.
    /// </summary>
    public float[]? JoinWeight { get; init; }
}

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

/// <summary>How a compose's de-rotation relights the samples it carries to the reference's instant (#1349, the handover circle).</summary>
public enum PlanetaryTurnRelight
{
    /// <summary>
    /// Only what lies above the stack's own sky is relit, by the ratio of the stack's own limb model through its core blur, the globe's alone
    /// (<see cref="PlanetaryLimbFit.CoreBlurredModel"/>), between target and source: the sky does not take the planet's lighting, and a sample
    /// carried toward the limb darkens as the stack's own blurred limb does. Where a stack's limb does not fit, Minnaert's ratio above its sky.
    /// </summary>
    Blurred,

    /// <summary>
    /// Every sample, its sky included, times Minnaert's ratio between target and source: every compose before 2026-10-12. It relit the sky
    /// with the planet and carried none of the limb's blur, and the limb view's switch between turned and unturned samples drew a circle.
    /// </summary>
    Minnaert,
}

/// <summary>How a join weighs each de-rotated stack's pixels by how it saw them (#1349), or not.</summary>
public enum PlanetaryViewingWeights
{
    /// <summary>Every pixel alike, as every join before #1349 fix C.</summary>
    None,

    /// <summary>
    /// Fix C: by how squarely the stack saw each pixel's source, nothing past the de-rotation's source limit (<see cref="PlanetaryDerotation.JoinWeights"/>).
    /// The limb then goes to the stacks the turn carried there from further in, with none of the real limb's blur.
    /// </summary>
    Source,

    /// <summary>
    /// <see cref="Source"/> inside the disk, and the limb left to the stacks that saw it there: by the pixel's radius on the target's disk, each
    /// stack's own unturned pixel takes over from the turned samples (<see cref="PlanetaryDerotation.JoinWeights"/>' <c>leaveLimb</c>).
    /// </summary>
    Limb,
}

/// <summary>How a filter's stacks are joined band by band (#1349 arm B), or not.</summary>
public enum PlanetaryBandWeights
{
    /// <summary>Not by band: each stack weighed by its noise alone in every band (the join before #1349).</summary>
    None,

    /// <summary>
    /// Each a trous band weighed by its transfer against the filter's sharpest stack over its noise on the sky, a matched filter. The joined
    /// band's transfer is the weights' mean of the stacks' transfers, below the sharpest's.
    /// </summary>
    Matched,

    /// <summary>
    /// The matched join with each band rescaled by sum(w) / sum(w T), a multi-frame Wiener at the stack level: the sharpest stack's own
    /// transfer, with the noise averaged down.
    /// </summary>
    Wiener,
}

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
            return (null, $"{name}: {body} has no rotation model to de-rotate by (Mars, Jupiter and Saturn have)");
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

    /// <summary>Where a stack's join weight (<see cref="PlanetaryMonoStack.JoinWeight"/>, #1349 fix C) lies beside its FITS: <c>&lt;name&gt;.weight.fits</c>.</summary>
    public static string JoinWeightPath(string stackPath)
        => System.IO.Path.Combine(System.IO.Path.GetDirectoryName(stackPath) ?? ".", System.IO.Path.GetFileNameWithoutExtension(stackPath) + ".weight.fits");

    /// <summary>
    /// A stack written as a compose step writes it: its FITS at <paramref name="path"/>, and its join weight beside it
    /// (<see cref="JoinWeightPath"/>) when it has one, labelled as no stack so a glob that takes it in by mistake is refused.
    /// </summary>
    public static void WriteStack(PlanetaryMonoStack stack, string path)
    {
        ArgumentNullException.ThrowIfNull(stack);
        stack.Image.WriteToFitsFile(path);
        if (stack.JoinWeight is { } joinWeight)
        {
            var (width, height) = (stack.Image.Width, stack.Image.Height);
            var plane = new float[height, width];
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    plane[y, x] = joinWeight[(y * width) + x];
                }
            }
            new Image([plane], BitDepth.Float32, 1, 0, 0, stack.Image.ImageMeta with { ObjectName = "", Filter = Filter.None, LimbEdge = null }).WriteToFitsFile(JoinWeightPath(path));
        }
        else
        {
            // A weight an earlier run left beside this path is not this stack's.
            System.IO.File.Delete(JoinWeightPath(path));
        }
    }

    /// <summary>Whether <paramref name="path"/> is a join weight (<see cref="JoinWeightPath"/>) rather than a stack, so a glob over a step's folder skips it.</summary>
    public static bool IsJoinWeightPath(string path) => path.EndsWith(".weight.fits", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A stack a compose step wrote (<see cref="WriteStack"/>), named <paramref name="name"/>, read back with its labels and the join
    /// weight beside it when there is one; null with the reason in words.
    /// </summary>
    public static (PlanetaryMonoStack? Stack, string? Refusal) ReadStack(string name, string path)
    {
        if (!Image.TryReadFitsFile(path, out var image) || FromIngested(name, image) is not { } stack)
        {
            return (null, $"{path}: not a stack a compose step wrote (one plane, with OBJECT, DATE-OBS and FILTER)");
        }
        var weightPath = JoinWeightPath(path);
        if (!System.IO.File.Exists(weightPath))
        {
            return (stack, null);
        }
        return Image.TryReadFitsFile(weightPath, out var weight) && weight.Width == image.Width && weight.Height == image.Height
            ? (stack with { JoinWeight = weight.GetChannelSpan(0).ToArray() }, null)
            : (null, $"{weightPath}: not the join weight of {path} (one plane of its size)");
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
        _ => null as Filter?,
    };

    /// <summary>The composing role of a filter by its effective wavelength (<see cref="PlanetaryCaptureName.WavelengthNm"/>): 650 red, 530 green, 460 blue, an IR-pass's 750 or an L's 550 the luminance.</summary>
    public static Filter? RoleOf(double? wavelengthNm) => wavelengthNm switch
    {
        650 => Filter.Red,
        530 => Filter.Green,
        460 => Filter.Blue,
        750 or 550 => Filter.Luminance,
        _ => null as Filter?,
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
    /// a rigid move, so the limb, the rings and the sky move with the disk, onto the reference's grid (each stack is cropped to where its
    /// own frames reached, #1300, so stacks of one night differ in size). Refuses, in words, a set of more than one planet, a limb that
    /// does not fit, or radii further apart than <see cref="MaxRadiusDisagreement"/>.
    /// </summary>
    public static (PlanetaryRegistration? Registration, string? Refusal) Register(IReadOnlyList<PlanetaryMonoStack> stacks, Action<string>? say = null)
    {
        ArgumentNullException.ThrowIfNull(stacks);
        if (stacks.Count == 0)
        {
            return (null, "no stacks to register");
        }
        var planet = stacks[0].Planet;
        if (stacks.FirstOrDefault(s => s.Planet != planet) is { } otherPlanet)
        {
            return (null, $"{otherPlanet.Name} is {otherPlanet.Planet}, where {stacks[0].Name} is {planet}");
        }
        var (fits, fitOptions, unfitted) = FitEach(stacks);
        if (fits is null || fitOptions is null)
        {
            return (null, $"{stacks[unfitted].Name}: its limb did not fit");
        }
        var reference = ReferenceIndex(stacks);
        var referenceFit = fits[reference];
        // How sharp each stack is, read here, where nothing has turned it yet (#1349).
        var edges = LimbEdges(stacks, fits, fitOptions[reference], reference);
        // Every stack onto the reference's grid: each is cropped to where its own frames reached (#1300), so stacks of one night differ
        // in size, and its disk's place is its own limb fit's. Where a moved stack does not reach it reads NaN, which its FITS carries to
        // the join, so the join averages only the stacks that reach a pixel (#1361); averaged in as zero, a strip of unreached rows read
        // half the sky on two stacks and turned a look's black point grey.
        var (width, height) = (stacks[reference].Image.Width, stacks[reference].Image.Height);
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
                $"{stacks[i].Name} ({stacks[i].Filter.ShortName}, {stacks[i].Instant:HH:mm:ss} UTC): disk R {fits[i].EquatorialRadius:0.00} px, the limb's edge at 0.1 cycles a pixel {edges[i]:0.000}, moved ({moveX:+0.00;-0.00;0.00}, {moveY:+0.00;-0.00;0.00}) px{(i == reference ? ", the reference" : "")}"));
            var image = stacks[i].Image;
            var plane = i == reference ? image.GetChannelArray(0) : PlanetaryChannelAlignment.Moved(image.GetChannelArray(0), dx, dy, width, height, float.NaN);
            var (max, min) = Extent([plane]);
            moved.Add(stacks[i] with
            {
                Image = new Image([plane], BitDepth.Float32, max, min, 0, image.ImageMeta with { LimbEdge = double.IsFinite(edges[i]) ? edges[i] : null }),
            });
        }
        return (new PlanetaryRegistration(moved.MoveToImmutable(), reference, referenceFit), null);
    }

    /// <summary>
    /// Step 3: a registered set (<see cref="Register"/>, its stacks on one disk) carried to the reference's instant through the spheroid
    /// (<see cref="PlanetaryDerotation.Derotate"/>), the disk the reference's limb fit, refitted here as the registration fitted it. Its
    /// north is decided by agreement: of the filters with two stacks, the pair the planet turned most between, the earlier carried to the
    /// later both ways round (<see cref="PlanetaryDerotation.AgreementBothWays"/>), so stacks of one filter, which show the same detail,
    /// decide it. Under <see cref="PlanetaryDerotation.LeastTurnToTellNorthDeg"/> the fit's north is kept. With
    /// <paramref name="viewingWeights"/> each stack also carries its join weight (<see cref="PlanetaryMonoStack.JoinWeight"/>,
    /// <see cref="PlanetaryDerotation.JoinWeights"/>, #1349), a stack already at the reference's instant included. <paramref name="relight"/> is
    /// how the samples carried are relit (<see cref="PlanetaryTurnRelight"/>). <paramref name="ringFeatherSigmas"/> fades the turn in beside
    /// Saturn's rings over each stack's core blur (<see cref="RingFeather"/>, #1537), an option for a nearly zonal night: it keeps a spot
    /// beside the band where it was.
    /// </summary>
    public static (PlanetaryCompositionDerotation? Derotation, string? Refusal) Derotate(IReadOnlyList<PlanetaryMonoStack> registered, Action<string>? say = null,
        PlanetaryViewingWeights viewingWeights = PlanetaryViewingWeights.None, PlanetaryTurnRelight relight = PlanetaryTurnRelight.Blurred,
        (double Inner, double Outer)? ringFeatherSigmas = null)
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
            var from = PhysicalEphemeris.Compute(planet, stack.Instant);
            var weight = viewingWeights is PlanetaryViewingWeights.None ? null
                : PlanetaryDerotation.JoinWeights(stack.Image.Width, stack.Image.Height, from, target, placement, k, leaveLimb: viewingWeights is PlanetaryViewingWeights.Limb);
            if (stack.Instant == instant)
            {
                carried.Add(stack with { JoinWeight = weight });
                continue;
            }
            var derotated = relight is PlanetaryTurnRelight.Minnaert
                ? PlanetaryDerotation.Derotate(stack.Image, from, target, placement, k).Image
                : TurnRelit(stack, from, target, placement, k, ringFeatherSigmas, say);
            say?.Invoke(string.Create(inv,
                $"{stack.Name}: carried {(instant - stack.Instant).TotalMinutes:+0.0;-0.0} minutes, the central meridian {Math.IEEERemainder(target.CentralMeridianIII - from.CentralMeridianIII, 360):+0.00;-0.00} degrees"));
            // Carried, it shows the planet at the reference's instant, and its header says so for the join's verb to read back.
            carried.Add(stack with { Image = WithMeta(derotated, stack.Image.ImageMeta with { ExposureStartTime = instant }), Instant = instant, JoinWeight = weight });
        }
        return (new PlanetaryCompositionDerotation(carried.MoveToImmutable(), instant, placement, north, decidedOn), null);
    }

    // A stack carried as PlanetaryDerotation.Derotate carries it, relit by PlanetaryTurnRelight.Blurred (#1349, the handover circle): its own
    // limb fitted at its own instant, its sky taken off before the relight and put back after, and the ratio its own core-blurred globe model
    // makes between target and source. The rings are left out of the model: the pixels they touch are never turned, and a ring model misplaced
    // against the stack relit the globe beside the band (a 1.09 % step at the band's inner edge, against 0.70 % without them).
    private static Image TurnRelit(PlanetaryMonoStack stack, in PlanetAspect from, in PlanetAspect target, in DiskPlacement placement, double k,
        (double Inner, double Outer)? ringFeatherSigmas, Action<string>? say)
    {
        var image = stack.Image;
        var (width, height) = (image.Width, image.Height);
        var own = PlanetaryLimbFit.FitAt(image, stack.Planet, stack.Instant);
        // #1537: the turn faded in beside the rings over the stack's own core blur, when asked.
        RingFeather? feather = own is (var blurred, _, _) && ringFeatherSigmas is { } sigmas ? RingFeather.OfBlur(blurred.PsfSigma, sigmas.Inner, sigmas.Outer) : null;
        var field = new DerotationTarget(target, placement, width, height, k, feather).FieldFrom(from, placement);
        var disk = own is { } fitted ? fitted.Disk : new MetricDisk(placement.CenterX, placement.CenterY, placement.EquatorialRadius);
        var sky = (float)(PlanetaryMetrics.SkyLevel(image.GetChannelSpan(0), width, height, disk) ?? 0);
        var model = own is (var fit, _, var options)
            ? PlanetaryLimbFit.CoreBlurredModel(fit with { RingLevels = null, RingSlopes = null }, options with { Rings = null }, width, height)
            : null;
        var inv = CultureInfo.InvariantCulture;
        say?.Invoke(own is (var f, _, _)
            ? string.Create(inv, $"{stack.Name}: relit above its sky {sky:0.00000} by its own limb, R {f.EquatorialRadius:0.00} px, k {f.LimbDarkening:0.000}, core {f.PsfSigma:0.00} px")
            : string.Create(inv, $"{stack.Name}: relit above its sky {sky:0.00000} by Minnaert's law, its limb unfitted"));
        return PlanetaryDerotation.Resample(image, field, sky, model);
    }

    /// <summary>
    /// Step 4: a de-rotated set (<see cref="Derotate"/>, every stack at one instant on one disk) joined: each filter's stacks averaged, red,
    /// green and blue as one colour master at that instant, the luminance's (IR or L) averaged the same way beside it, null for none. A
    /// filter's stacks are first put on one scale by the disk, each weighted by its noise on it (#1337, <see cref="JoinFilter"/>), so a burst
    /// taken at other settings joins at the night's level. Refuses, in words, a set with no red, green or blue, stacks at more than one
    /// instant, or a filter of several stacks whose disk does not fit or has no level above its sky. With <paramref name="bandWeights"/>
    /// each filter's stacks are weighed band by band (<see cref="JoinFilter"/>, #1349 arm B).
    /// </summary>
    public static (PlanetaryComposed? Composed, string? Refusal) Join(IReadOnlyList<PlanetaryMonoStack> derotated, Action<string>? say = null,
        PlanetaryBandWeights bandWeights = PlanetaryBandWeights.None)
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
        // The disk every stack lies on, read once off the reference, where a filter has stacks to scale.
        MetricDisk? disk = null;
        if (derotated.GroupBy(s => s.Filter).Any(g => g.Count() > 1))
        {
            var reference = derotated[ReferenceIndex(derotated)];
            if (PlanetaryLimbFit.FitAt(reference.Image, reference.Planet, reference.Instant) is not { } fitted)
            {
                return (null, $"{reference.Name}: its limb did not fit, so its filters' stacks cannot be put on one scale");
            }
            disk = fitted.Disk;
        }
        var planes = new float[3][,];
        Filter[] colours = [Filter.Red, Filter.Green, Filter.Blue];
        for (var c = 0; c < 3; c++)
        {
            var (mean, refusal) = JoinFilter(derotated, colours[c], disk, say, bandWeights);
            if (refusal is not null)
            {
                return (null, refusal);
            }
            if (mean is null)
            {
                return (null, $"no {colours[c].DisplayName} stack: a colour master needs red, green and blue");
            }
            planes[c] = mean;
        }
        var first = derotated[0].Image;
        var meta = first.ImageMeta with { Filter = Filter.None, SensorType = SensorType.Color, LimbEdge = null };
        var (max, min) = Extent(planes);
        var master = new Image(planes, BitDepth.Float32, max, min, 0, meta);
        Image? luminance = null;
        var (l, luminanceRefusal) = JoinFilter(derotated, Filter.Luminance, disk, say, bandWeights);
        if (luminanceRefusal is not null)
        {
            return (null, luminanceRefusal);
        }
        if (l is not null)
        {
            var (lMax, lMin) = Extent([l]);
            luminance = new Image([l], BitDepth.Float32, lMax, lMin, 0, first.ImageMeta with { Filter = Filter.Luminance, LimbEdge = null });
        }
        return (new PlanetaryComposed(master, luminance), null);
    }

    /// <summary>
    /// Step 5, optional: the luminance's detail carried into each colour plane by the deep-sky LRGB step, unchanged
    /// (<see cref="LuminanceDetail.Apply"/>, what <c>image lrgb</c> runs): each plane's colour kept at the scale of
    /// <paramref name="colourSigma"/> and the luminance's finer detail added at each plane's own scale (<see cref="LuminanceDetail.ScaleFor"/>).
    /// An IR stack's detail is sharper than the colours' (the seeing is better at its wavelength), and its belts' contrast is its own, so
    /// it is an option, never the default (#1278). Null with the reason in words for a master that is not three planes, a luminance not one
    /// plane on its grid, or a scale that cannot be measured.
    /// </summary>
    public static (Image? Master, double[] Scales, string? Refusal) WithLuminance(Image master, Image luminance, float colourSigma = LuminanceDetail.DefaultColourSigma)
    {
        ArgumentNullException.ThrowIfNull(master);
        ArgumentNullException.ThrowIfNull(luminance);
        if (master.ChannelCount != 3)
        {
            return (null, [], $"a colour master has three planes, this one {master.ChannelCount}");
        }
        if (luminance.ChannelCount != 1 || luminance.Width != master.Width || luminance.Height != master.Height)
        {
            return (null, [], $"the luminance is {luminance.ChannelCount} planes of {luminance.Width}x{luminance.Height}, where the master is {master.Width}x{master.Height}");
        }
        var planes = new float[3][,];
        var scales = new double[3];
        string[] names = ["red", "green", "blue"];
        for (var c = 0; c < 3; c++)
        {
            var channel = Image.FromChannel(master.GetChannelArray(c));
            scales[c] = LuminanceDetail.ScaleFor(channel, luminance);
            if (!double.IsFinite(scales[c]) || scales[c] <= 0)
            {
                return (null, scales, $"the luminance's scale against {names[c]} could not be measured");
            }
            planes[c] = LuminanceDetail.Apply(channel, luminance, scales[c], colourSigma).GetChannelArray(0);
        }
        var (max, min) = Extent(planes);
        return (new Image(planes, BitDepth.Float32, max, min, 0, master.ImageMeta), scales, null);
    }

    /// <summary>
    /// What <see cref="WithLuminance"/> did, read on the planet's disk inside 0.9 radii (Saturn's rings left out): each channel's scale
    /// against the ratio of the luminance's disk mean to the channel's; how far the disk's mean colour moved (the larger chromaticity
    /// difference, <see cref="LinearRgb.ChromaDistance"/>); and how the detailed master's luminance (the mean of its planes) correlates
    /// with the luminance in a trous bands 1 to 4; and which of the two is the sharper: the RMS of the colours' own luminance (the mean of
    /// their planes) and of the luminance in each band, each over its own disk mean, so a luminance softer than the colours shows as less.
    /// </summary>
    public static (double[] DiskRatios, double ChromaShift, double[] BandCorrelations, double[] ColourBandRms, double[] LuminanceBandRms) ReadLuminance(
        Image rgb, Image luminance, Image detailed, in MetricDisk disk)
    {
        ArgumentNullException.ThrowIfNull(rgb);
        ArgumentNullException.ThrowIfNull(luminance);
        ArgumentNullException.ThrowIfNull(detailed);
        var (width, height) = (rgb.Width, rgb.Height);
        var inside = new bool[width * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                inside[(y * width) + x] = disk.RadiiAt(x, y) < 0.9 && !disk.RingTouched(x, y);
            }
        }
        double MeanInside(ReadOnlySpan<float> plane)
        {
            double sum = 0;
            var count = 0;
            for (var i = 0; i < plane.Length; i++)
            {
                if (inside[i])
                {
                    (sum, count) = (sum + plane[i], count + 1);
                }
            }
            return count > 0 ? sum / count : double.NaN;
        }
        var lMean = MeanInside(luminance.GetChannelSpan(0));
        var ratios = new double[3];
        for (var c = 0; c < 3; c++)
        {
            ratios[c] = lMean / MeanInside(rgb.GetChannelSpan(c));
        }
        var before = new LinearRgb(MeanInside(rgb.GetChannelSpan(0)), MeanInside(rgb.GetChannelSpan(1)), MeanInside(rgb.GetChannelSpan(2)));
        var after = new LinearRgb(MeanInside(detailed.GetChannelSpan(0)), MeanInside(detailed.GetChannelSpan(1)), MeanInside(detailed.GetChannelSpan(2)));

        // The detailed master's luminance as the mean of its planes, against the luminance, band by band.
        var mean = PlanetaryLimbFit.Luminance(detailed);
        const int bands = 4;
        var ours = ATrousWaveletTransform.Decompose(mean, width, height, bands);
        var theirs = ATrousWaveletTransform.Decompose(luminance.GetChannelSpan(0), width, height, bands);
        var colourMean = PlanetaryLimbFit.Luminance(rgb);
        var colours = ATrousWaveletTransform.Decompose(colourMean, width, height, bands);
        var (colourLevel, luminanceLevel) = (MeanInside(colourMean), lMean);
        var (colourRms, luminanceRms) = (new double[bands], new double[bands]);
        var correlations = new double[bands];
        for (var b = 0; b < bands; b++)
        {
            var a = ours.Detail(b);
            var l = theirs.Detail(b);
            double sa = 0, sl = 0, saa = 0, sll = 0, sal = 0;
            var n = 0;
            for (var i = 0; i < inside.Length; i++)
            {
                if (inside[i])
                {
                    (sa, sl, saa, sll, sal, n) = (sa + a[i], sl + l[i], saa + (a[i] * a[i]), sll + (l[i] * l[i]), sal + (a[i] * l[i]), n + 1);
                }
            }
            var covariance = (sal / n) - (sa / n * (sl / n));
            var spread = Math.Sqrt(((saa / n) - (sa / n * (sa / n))) * ((sll / n) - (sl / n * (sl / n))));
            correlations[b] = spread > 0 ? covariance / spread : double.NaN;
            colourRms[b] = RmsInside(colours.Detail(b)) / colourLevel;
            luminanceRms[b] = RmsInside(l) / luminanceLevel;
        }
        return (ratios, before.ChromaDistance(after), correlations, colourRms, luminanceRms);

        double RmsInside(ReadOnlySpan<float> plane)
        {
            double sum = 0;
            var count = 0;
            for (var i = 0; i < plane.Length; i++)
            {
                if (inside[i])
                {
                    (sum, count) = (sum + (plane[i] * plane[i]), count + 1);
                }
            }
            return count > 0 ? Math.Sqrt(sum / count) : double.NaN;
        }
    }

    /// <summary>
    /// A luminance from a colour master's three planes (#1330), the planetary twin of <see cref="SyntheticLuminance"/>: each plane put on
    /// green's scale by the disk (its mean above its sky inside 0.9 radii, clear of Saturn's rings, against green's: a planet has no
    /// stars to read a scale from), weighted by the inverse of its noise on that scale (read over 4 px blocks of the sky past
    /// <see cref="SkyNoiseRadii"/>, never over the disk, whose structure a noisy plane's reading takes in), and combined by
    /// <see cref="SyntheticLuminance.Combine"/>. With <paramref name="onto"/>,
    /// another master of the same capture, the luminance is placed on that master's disk by the two limb fits (<see cref="Register"/>,
    /// <paramref name="onto"/>'s green the reference), so a Bayer drizzle's luminance can detail the demosaic's colours. Also returns the
    /// luminance's own noise, read on the sky as the planes' is (before any placement, which resamples it), and its effective wavelength,
    /// the weights' mean of <paramref name="wavelengthsNm"/>, which it is sharpened at. Null with the
    /// reason in words for a master that is not three planes, a limb that does not fit, or a placement <see cref="Register"/> refuses.
    /// </summary>
    public static (Image? Luminance, SyntheticLuminance.Channel[] Parts, double Noise, double WavelengthNm, string? Refusal) Luminance(Image colour, CatalogIndex planet,
        DateTimeOffset instant, IReadOnlyList<double> wavelengthsNm, Image? onto = null, Action<string>? say = null)
    {
        ArgumentNullException.ThrowIfNull(colour);
        ArgumentNullException.ThrowIfNull(wavelengthsNm);
        if (colour.ChannelCount != 3 || wavelengthsNm.Count != 3)
        {
            return (null, [], double.NaN, double.NaN, $"a colour master has three planes and three wavelengths, this one {colour.ChannelCount} and {wavelengthsNm.Count}");
        }
        if (PlanetaryLimbFit.FitAt(colour, planet, instant) is not { } limb)
        {
            return (null, [], double.NaN, double.NaN, "the master's limb did not fit");
        }
        var disk = limb.Disk;
        var (width, height) = (colour.Width, colour.Height);
        var planes = new Image[3];
        var levels = new double[3];
        for (var c = 0; c < 3; c++)
        {
            var plane = colour.GetChannelArray(c);
            var (max, min) = Extent([plane]);
            planes[c] = new Image([plane], BitDepth.Float32, max, min, 0, colour.ImageMeta with { SensorType = SensorType.Monochrome });
            var span = planes[c].GetChannelSpan(0);
            double sum = 0;
            var count = 0;
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    if (disk.RadiiAt(x, y) < 0.9 && !disk.RingTouched(x, y))
                    {
                        (sum, count) = (sum + span[(y * width) + x], count + 1);
                    }
                }
            }
            levels[c] = count > 0 && PlanetaryMetrics.SkyLevel(span, width, height, disk) is { } sky ? (sum / count) - sky : double.NaN;
            if (!(levels[c] > 0))
            {
                return (null, [], double.NaN, double.NaN, $"plane {c}: no level above its sky on the disk to scale it by");
            }
        }
        // Green the reference, as the deep-sky luminance puts it: each plane's scale is green's level over its own.
        var parts = new SyntheticLuminance.Channel[3];
        var inverseVariance = new double[3];
        double total = 0;
        for (var c = 0; c < 3; c++)
        {
            var scale = levels[1] / levels[c];
            var noise = SkyBlockNoise(planes[c], disk);
            if (!double.IsFinite(noise))
            {
                say?.Invoke($"plane {c}: no sky past {SkyNoiseRadii} radii to read its noise on; read over the whole plane, the planet's structure in it");
                noise = SyntheticLuminance.BlockNoise(planes[c]);
            }
            var scaledNoise = scale * noise;
            inverseVariance[c] = 1.0 / (scaledNoise * scaledNoise);
            total += inverseVariance[c];
            parts[c] = new SyntheticLuminance.Channel(scale, PixelNoise.FiniteMedian(planes[c].GetChannelSpan(0)), noise, scaledNoise, 0, 0);
        }
        double wavelength = 0;
        for (var c = 0; c < 3; c++)
        {
            parts[c] = parts[c] with { Weight = inverseVariance[c] / total };
            wavelength += parts[c].Weight * wavelengthsNm[c];
        }
        var luminance = SyntheticLuminance.Combine(planes, parts, reference: 1);
        var luminanceNoise = SkyBlockNoise(luminance, disk);
        if (!double.IsFinite(luminanceNoise))
        {
            luminanceNoise = SyntheticLuminance.BlockNoise(luminance);
        }
        if (onto is null)
        {
            return (luminance, parts, luminanceNoise, wavelength, null);
        }
        if (onto.ChannelCount != 3)
        {
            return (null, parts, luminanceNoise, wavelength, $"the master to place it on has {onto.ChannelCount} planes, where a colour master has three");
        }
        var green = onto.GetChannelArray(1);
        var (gMax, gMin) = Extent([green]);
        PlanetaryMonoStack[] pair =
        [
            new PlanetaryMonoStack("the master's green", new Image([green], BitDepth.Float32, gMax, gMin, 0, onto.ImageMeta with { Filter = Filter.Green }), planet, instant, Filter.Green),
            new PlanetaryMonoStack("the luminance", luminance, planet, instant, Filter.Luminance),
        ];
        var (registration, refusal) = Register(pair, say);
        return registration is null
            ? (null, parts, luminanceNoise, wavelength, refusal)
            : (registration.Stacks[1].Image, parts, luminanceNoise, wavelength, null);
    }

    /// <summary>
    /// The recipe: <see cref="Register"/>, <see cref="Derotate"/> and <see cref="Join"/> on ingested stacks, in order, and with
    /// <paramref name="withLuminance"/> the luminance carried in (<see cref="WithLuminance"/>), with nothing between them a file would not
    /// carry, so the steps' verbs run one by one give the same master to the bit. <paramref name="viewingWeights"/> weighs each stack's
    /// pixels by how it saw them (#1349, <see cref="PlanetaryViewingWeights"/>, <see cref="Derotate"/>), <paramref name="bandWeights"/> each filter's stacks band
    /// by band (#1349 arm B, <see cref="Join"/>): options, measured before either is the default. <paramref name="relight"/> is how the turn
    /// relights what it carries (<see cref="PlanetaryTurnRelight"/>), and <paramref name="ringFeatherSigmas"/> how it fades in beside Saturn's
    /// rings (<see cref="Derotate"/>).
    /// </summary>
    public static (PlanetaryComposed? Composed, PlanetaryCompositionDerotation? Derotation, string? Refusal) Run(IReadOnlyList<PlanetaryMonoStack> ingested,
        Action<string>? say = null, bool withLuminance = false, PlanetaryViewingWeights viewingWeights = PlanetaryViewingWeights.None,
        PlanetaryBandWeights bandWeights = PlanetaryBandWeights.None, PlanetaryTurnRelight relight = PlanetaryTurnRelight.Blurred,
        (double Inner, double Outer)? ringFeatherSigmas = null)
    {
        var (registration, refusal) = Register(ingested, say);
        if (registration is null)
        {
            return (null, null, refusal);
        }
        var (derotation, derotateRefusal) = Derotate(registration.Stacks, say, viewingWeights, relight, ringFeatherSigmas);
        if (derotation is null)
        {
            return (null, null, derotateRefusal);
        }
        var (composed, joinRefusal) = Join(derotation.Stacks, say, bandWeights);
        if (composed is null || !withLuminance)
        {
            return (composed, derotation, joinRefusal);
        }
        if (composed.Luminance is not { } luminance)
        {
            return (null, derotation, "no IR or L stack to carry in as the luminance");
        }
        var (detailed, scales, luminanceRefusal) = WithLuminance(composed.Master, luminance);
        if (detailed is null)
        {
            return (null, derotation, luminanceRefusal);
        }
        say?.Invoke(string.Create(CultureInfo.InvariantCulture,
            $"the luminance carried in at scales red {scales[0]:0.000}, green {scales[1]:0.000}, blue {scales[2]:0.000}"));
        return (composed with { Master = detailed }, derotation, null);
    }

    /// <summary>The radii, rings counted (<see cref="MetricDisk.ClearRadiiAt"/>), past which <see cref="Luminance"/> reads a plane's noise: past the halo, as the metrics' sky.</summary>
    public const double SkyNoiseRadii = 1.3;

    // A plane's noise per pixel over 4 px blocks of the sky past SkyNoiseRadii, the deep-sky luminance's block noise
    // (SyntheticLuminance.BlockNoise) kept off the planet. Over the whole plane its second pass, which leaves out a block whose peak stands
    // far above the noise, left out the disk's structured blocks of a quiet plane and kept a noisy one's, so the noisier the plane the more
    // of its structure it read as noise (on a rendered master with noise of 0.002, 0.004 and 0.008, green read 0.0022 and blue 0.0117).
    // NaN with no two sky blocks side by side.
    private static double SkyBlockNoise(Image plane, in MetricDisk disk)
    {
        var (width, height) = (plane.Width, plane.Height);
        var source = plane.GetChannelSpan(0);
        var sky = new float[source.Length];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = (y * width) + x;
                sky[i] = disk.ClearRadiiAt(x, y) > SkyNoiseRadii ? source[i] : float.NaN;
            }
        }
        return PixelNoise.FromBlocks(sky, width, SyntheticLuminance.NoiseBlockPx);
    }

    // A stack's limb, fitted at its own instant (Saturn's rings in the model).
    private static LimbFit? FitOf(PlanetaryMonoStack stack) => PlanetaryLimbFit.FitAt(stack.Image, stack.Planet, stack.Instant)?.Fit;

    // Every stack's limb fitted at its own instant, with the options it was fitted with; both null, and the stack's index, when one does not fit.
    private static (LimbFit[]? Fits, LimbFitOptions[]? Options, int Unfitted) FitEach(IReadOnlyList<PlanetaryMonoStack> stacks)
    {
        var (fits, options) = (new LimbFit[stacks.Count], new LimbFitOptions[stacks.Count]);
        for (var i = 0; i < stacks.Count; i++)
        {
            if (PlanetaryLimbFit.FitAt(stacks[i].Image, stacks[i].Planet, stacks[i].Instant) is not (var fit, _, var fitOptions))
            {
                return (null, null, i);
            }
            (fits[i], options[i]) = (fit, fitOptions);
        }
        return (fits, options, -1);
    }

    // How sharp each stack is (#1349, ImageMeta.LimbEdge), read before any turn: a turn carries the disk's inside to the limb with none of
    // the limb's blur, so read after it the stack carried furthest would read sharpest. Every stack is read against the reference's model at
    // its own centre, one camera at one scale as the registration takes them: each against its own fit, a copy at another gain on another
    // sky read 1.05 against 1.16, its fit slid along the valley radius, k and blur share (#1498).
    private static double[] LimbEdges(IReadOnlyList<PlanetaryMonoStack> stacks, LimbFit[] fits, LimbFitOptions referenceOptions, int reference)
    {
        var aspect = PhysicalEphemeris.Compute(stacks[reference].Planet, stacks[reference].Instant);
        var edges = new double[stacks.Count];
        for (var i = 0; i < stacks.Count; i++)
        {
            var image = stacks[i].Image;
            edges[i] = PlanetaryFinestBand.StackEdge(image.GetChannelSpan(0), image.Width, image.Height,
                fits[reference] with { CenterX = fits[i].CenterX, CenterY = fits[i].CenterY }, referenceOptions, aspect).TransferAt(0.1);
        }
        return edges;
    }

    /// <summary>
    /// <paramref name="stacks"/>, each with how sharp it is (<see cref="ImageMeta.LimbEdge"/>) read as <see cref="Register"/> reads it, against
    /// the reference's model (<see cref="ReferenceIndex"/>), with nothing moved: for a join of stacks already on one disk that no registration
    /// read (#1349). Null where a limb does not fit.
    /// </summary>
    internal static ImmutableArray<PlanetaryMonoStack>? WithLimbEdges(IReadOnlyList<PlanetaryMonoStack> stacks)
    {
        if (FitEach(stacks) is not ({ } fits, { } options, _))
        {
            return null;
        }
        var reference = ReferenceIndex(stacks);
        var edges = LimbEdges(stacks, fits, options[reference], reference);
        return [.. stacks.Select((stack, i) => stack with
        {
            Image = WithMeta(stack.Image, stack.Image.ImageMeta with { LimbEdge = double.IsFinite(edges[i]) ? edges[i] : null }),
        })];
    }


    /// <summary>
    /// A filter's stacks joined into one plane (#1337): each put on the scale of the filter's median-level stack by the disk, its level above
    /// its own sky (the mean inside 0.9 radii, rings left out, less <see cref="PlanetaryMetrics.SkyLevel"/>) taken to that stack's and its sky
    /// to that stack's sky, then averaged weighted by its inverse noise variance on that scale (<see cref="SkyBlockNoise"/>), over the stacks
    /// that reach each pixel (a moved stack reads NaN where it does not, #1361). A pixel no stack reaches reads 0, as a stack reads where no
    /// folded frame reached it (#1319). One stack is taken as it is. Null for a filter with no stack; a refusal in words for a stack with no
    /// level above its sky. The owner's 2026-10-07 Saturn ran its first burst at another gain, exposure and black level: averaged as they
    /// were, its stacks entered each filter at about three quarters of the others' level and on another sky.
    /// </summary>
    internal static (float[,]? Plane, string? Refusal) JoinFilter(IReadOnlyList<PlanetaryMonoStack> stacks, Filter filter, MetricDisk? disk,
        Action<string>? say = null, PlanetaryBandWeights bandWeights = PlanetaryBandWeights.None)
    {
        var of = stacks.Where(s => s.Filter == filter).ToArray();
        if (of.Length == 0)
        {
            return (null, null);
        }
        var (width, height) = (of[0].Image.Width, of[0].Image.Height);
        var (scales, skies, weights) = (new double[of.Length], new double[of.Length], new double[of.Length]);
        Array.Fill(scales, 1.0);
        Array.Fill(weights, 1.0);
        var referenceSky = 0.0;
        if (of.Length > 1)
        {
            if (disk is not { } onDisk)
            {
                return (null, $"{filter.DisplayName}: {of.Length} stacks and no disk to put them on one scale by");
            }
            var levels = new double[of.Length];
            for (var s = 0; s < of.Length; s++)
            {
                var plane = of[s].Image.GetChannelSpan(0);
                double sum = 0;
                var count = 0;
                for (var y = 0; y < height; y++)
                {
                    for (var x = 0; x < width; x++)
                    {
                        var v = plane[(y * width) + x];
                        if (onDisk.RadiiAt(x, y) < 0.9 && !onDisk.RingTouched(x, y) && !float.IsNaN(v))
                        {
                            (sum, count) = (sum + v, count + 1);
                        }
                    }
                }
                skies[s] = PlanetaryMetrics.SkyLevel(plane, width, height, onDisk) ?? 0;
                levels[s] = count > 0 ? (sum / count) - skies[s] : double.NaN;
                if (!(levels[s] > 0))
                {
                    return (null, $"{of[s].Name}: no level above its sky on the disk to put it on its filter's scale by");
                }
            }
            // The median-level stack sets the scale, so a burst at other settings is the one moved, whichever filter.
            var reference = Enumerable.Range(0, of.Length).OrderBy(s => levels[s]).ThenBy(s => s).ElementAt((of.Length - 1) / 2);
            referenceSky = skies[reference];
            for (var s = 0; s < of.Length; s++)
            {
                scales[s] = levels[reference] / levels[s];
                var noise = SkyBlockNoise(of[s].Image, onDisk) * scales[s];
                weights[s] = double.IsFinite(noise) && noise > 0 ? 1 / (noise * noise) : double.NaN;
            }
            if (weights.Any(w => !double.IsFinite(w)))
            {
                say?.Invoke($"{filter.DisplayName}: a stack has no sky past {SkyNoiseRadii} radii to read its noise on; its stacks weighted alike");
                Array.Fill(weights, 1.0);
            }
            var total = weights.Sum();
            var inv = CultureInfo.InvariantCulture;
            for (var s = 0; s < of.Length; s++)
            {
                say?.Invoke(string.Create(inv,
                    $"{of[s].Name} ({filter.ShortName}): scale {scales[s]:0.0000} onto {of[reference].Name}, sky {skies[s]:0.00000}, weight {weights[s] / total:P1}"));
            }
        }

        // A join weight (#1349 fix C) is every stack's or none's: one stack weighed by its view among others weighed alike would drown them.
        var viewed = of.Count(s => s.JoinWeight is not null);
        if (viewed != 0 && viewed != of.Length)
        {
            return (null, $"{filter.DisplayName}: {viewed} of its {of.Length} stacks carry a join weight; a join weighs every stack by how it saw the planet, or none");
        }
        if (bandWeights is not PlanetaryBandWeights.None && of.Length > 1 && disk is { } bandDisk)
        {
            if (of.FirstOrDefault(s => s.Image.ImageMeta.LimbEdge is null) is { } unread)
            {
                return (null, $"{unread.Name}: no limb edge read at its registration (LIMBEDGE), which a join band by band picks {filter.DisplayName}'s sharpest stack by");
            }
            return (JoinByBands(of, bandDisk, scales, skies, weights, referenceSky, width, height, filter, bandWeights is PlanetaryBandWeights.Wiener, say), null);
        }
        var weighted = new double[height, width];
        var reached = new double[height, width];
        for (var s = 0; s < of.Length; s++)
        {
            var plane = of[s].Image.GetChannelSpan(0);
            var (scale, sky, weight, join) = (scales[s], skies[s], weights[s], of[s].JoinWeight);
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var v = plane[(y * width) + x];
                    if (!float.IsNaN(v))
                    {
                        // One stack is taken as it is: its scale 1 and no sky moved, so not a bit changes.
                        var onScale = of.Length > 1 ? referenceSky + ((v - sky) * scale) : v;
                        // Weighed by how squarely the stack saw the pixel's source (fix C), the floor handing a pixel no stack saw to the
                        // plain average; with no join weight, weighed as every join before it, to the bit.
                        var w = join is null ? weight : weight * (join[(y * width) + x] + JoinWeightFloor);
                        weighted[y, x] += w * onScale;
                        reached[y, x] += w;
                    }
                }
            }
        }
        var joined = new float[height, width];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                joined[y, x] = reached[y, x] > 0 ? (float)(weighted[y, x] / reached[y, x]) : 0f;
            }
        }
        return (joined, null);
    }

    /// <summary>
    /// What a pixel no stack saw squarely is worth in a join weighted by view (#1349 fix C), beside a face-on stack's one: enough that a
    /// pixel past every stack's source limit takes the stacks' plain average, too little to matter where any stack saw it.
    /// </summary>
    public const float JoinWeightFloor = 1e-3f;

    /// <summary>The a trous bands a join weighed band by band reads (#1349 arm B), finest first; the rest is the residual.</summary>
    public const int BandWeightScales = 5;

    // #1349 arm B: a filter's stacks (already on one scale) joined band by band. Each stack's a trous band is weighed by its transfer
    // against the filter's sharpest stack (the most band 2 above its own noise on the disk), read as <band, sharpest's band> over the
    // sharpest's own energy less its noise, over the band's noise on the sky squared: a matched filter, so a band the stack holds as well
    // as the sharpest weighs by its noise alone, as the plain join does, and one it holds at half weighs half. The residual keeps the
    // plain join's noise weights; every pixel is read over the stacks that reach it, each by its join weight where it has one (fix C). With
    // `wiener`, each band's weighted mean is rescaled by sum(w) / sum(w T) over the filter's stacks: the matched mean's transfer is
    // sum(w T) / sum(w), below the sharpest's, and the rescale gives a multi-frame Wiener its sharpest stack's own transfer. One factor
    // for the frame, so a pixel only the blurred stacks reach is never divided by their small transfer.
    private static float[,] JoinByBands(PlanetaryMonoStack[] of, MetricDisk disk, double[] scales, double[] skies, double[] noiseWeights, double referenceSky,
        int width, int height, Filter filter, bool wiener, Action<string>? say)
    {
        var (n, size) = (of.Length, width * height);
        var masks = new float[n][];
        var bands = new WaveletDecomposition[n];
        for (var s = 0; s < n; s++)
        {
            var plane = of[s].Image.GetChannelSpan(0);
            var (onScale, mask, join) = (new float[size], new float[size], of[s].JoinWeight);
            for (var i = 0; i < size; i++)
            {
                var v = plane[i];
                // A pixel the stack does not reach is filled with the sky for the transform alone; its mask keeps it out of the join.
                (onScale[i], mask[i]) = float.IsNaN(v)
                    ? ((float)referenceSky, 0f)
                    : ((float)(referenceSky + ((v - skies[s]) * scales[s])), join is null ? 1f : join[i] + JoinWeightFloor);
            }
            (masks[s], bands[s]) = (mask, ATrousWaveletTransform.Decompose(onScale, width, height, BandWeightScales));
        }
        var (sky, inside) = (new List<int>(), new List<int>());
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                if (disk.ClearRadiiAt(x, y) > SkyNoiseRadii)
                {
                    sky.Add((y * width) + x);
                }
                else if (disk.RadiiAt(x, y) < 0.9 && !disk.RingTouched(x, y))
                {
                    inside.Add((y * width) + x);
                }
            }
        }
        var noise = new double[n, BandWeightScales];
        for (var s = 0; s < n; s++)
        {
            for (var j = 0; j < BandWeightScales; j++)
            {
                noise[s, j] = RobustSpread(bands[s].Detail(j), sky, masks[s]);
            }
        }
        // The limb's edge each stack showed before any turn picks the sharpest (#1349, ImageMeta.LimbEdge): band 2's energy above its noise
        // on the disk picked the first burst of 2026-10-07 in every filter, a gain scaled 1.40 times onto the others, its noise read as detail.
        var sharpest = Enumerable.Range(0, n).OrderByDescending(s => of[s].Image.ImageMeta.LimbEdge ?? double.NegativeInfinity).ThenBy(s => s).First();
        var weight = new double[n, BandWeightScales];
        var rescale = new double[BandWeightScales];
        var inv = CultureInfo.InvariantCulture;
        var transfers = new double[n];
        for (var j = 0; j < BandWeightScales; j++)
        {
            var reference = bands[sharpest].Detail(j);
            var (own, count) = (0.0, 0);
            foreach (var i in inside)
            {
                if (masks[sharpest][i] > 0)
                {
                    (own, count) = (own + ((double)reference[i] * reference[i]), count + 1);
                }
            }
            var signal = own - (count * noise[sharpest, j] * noise[sharpest, j]);
            for (var s = 0; s < n; s++)
            {
                double transfer;
                if (s == sharpest)
                {
                    transfer = 1;
                }
                else
                {
                    var band = bands[s].Detail(j);
                    var shared = 0.0;
                    foreach (var i in inside)
                    {
                        if (masks[s][i] > 0 && masks[sharpest][i] > 0)
                        {
                            shared += (double)band[i] * reference[i];
                        }
                    }
                    transfer = signal > 0 ? shared / signal : 1;
                }
                var sigma = noise[s, j];
                // A stack whose band has no noise to read is weighed by its noise alone, as holding the band as the sharpest does.
                var held = double.IsFinite(sigma) && sigma > 0;
                weight[s, j] = held ? Math.Max(transfer, 0) / (sigma * sigma) : noiseWeights[s];
                transfers[s] = held ? Math.Max(transfer, 0) : 1;
            }
            var total = Enumerable.Range(0, n).Sum(s => weight[s, j]);
            var carried = Enumerable.Range(0, n).Sum(s => weight[s, j] * transfers[s]);
            rescale[j] = wiener && carried > 0 ? total / carried : 1;
            say?.Invoke(string.Create(inv,
                $"{filter.DisplayName} band {j + 1}: {string.Join(", ", Enumerable.Range(0, n).Select(s => $"{of[s].Name} {weight[s, j] / total:P1}"))}{(j == 0 ? $" (the sharpest {of[sharpest].Name}, its limb's edge {of[sharpest].Image.ImageMeta.LimbEdge:0.000})" : "")}{(wiener ? $", rescaled x{rescale[j]:0.000}" : "")}"));
        }
        var joined = new float[height, width];
        ParallelFor.Run(height, y =>
        {
            for (var x = 0; x < width; x++)
            {
                var i = (y * width) + x;
                var (value, reached) = (0.0, false);
                for (var j = 0; j <= BandWeightScales; j++)
                {
                    var (sum, total) = (0.0, 0.0);
                    for (var s = 0; s < n; s++)
                    {
                        if (masks[s][i] > 0)
                        {
                            var w = (j < BandWeightScales ? weight[s, j] : noiseWeights[s]) * masks[s][i];
                            sum += w * (j < BandWeightScales ? bands[s].Detail(j)[i] : bands[s].Residual[i]);
                            total += w;
                        }
                    }
                    if (total > 0)
                    {
                        (value, reached) = (value + ((j < BandWeightScales ? rescale[j] : 1) * sum / total), true);
                    }
                }
                joined[y, x] = reached ? (float)value : 0f;
            }
        });
        return joined;
    }

    // The robust spread (1.4826 MAD) of a band over the listed pixels a stack reaches.
    private static double RobustSpread(ReadOnlySpan<float> band, List<int> pixels, float[] mask)
    {
        var values = new List<double>(pixels.Count);
        foreach (var i in pixels)
        {
            if (mask[i] > 0 && float.IsFinite(band[i]))
            {
                values.Add(band[i]);
            }
        }
        if (values.Count < 2)
        {
            return double.NaN;
        }
        var median = StatisticsHelper.MedianFast([.. values]);
        var deviations = new double[values.Count];
        for (var k = 0; k < deviations.Length; k++)
        {
            deviations[k] = Math.Abs(values[k] - median);
        }
        return 1.4826 * StatisticsHelper.MedianFast(deviations);
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
