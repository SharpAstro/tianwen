using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TianWen.Lib.Imaging.Enhancement;
using TianWen.Lib.Imaging.StarRemoval;
using TianWen.Lib.Stat;

namespace TianWen.Lib.Imaging;

/// <summary>
/// Mono masters on one grid (<see cref="MasterAlignment"/>) combined into a colour image, with a narrowband line's emission
/// added to the broadband channel it belongs in once its continuum is out (<see cref="ContinuumSubtractor"/>):
/// docs/plans/narrowband-colour.md, phase 2's palette mixer, the HaRGB case first.
/// </summary>
/// <remarks>
/// <para><b>Stars out first, back last.</b> A single continuum scale fits no star exactly (each star's line-to-broadband
/// ratio is its own colour, 9.6 percent spread on LDN 1622), so subtracting on the masters leaves a pit or a ring at every
/// star. On starless planes there is no star to leave, the emission is added to them, and the broadband stars
/// (<see cref="Stars"/>) go back on top. The scale is still measured on the planes WITH their stars: a red filter passes
/// the line too, so with the stars gone the only structure the two share is the emission, and a fit there would subtract
/// the line it is meant to keep (it read 1.01 on LDN 1622's starless pair).</para>
/// <para><b>One scale for every plane through the star remover.</b> A remover takes values in <c>[0, 1]</c>, and dividing each
/// plane by its own maximum puts each on its own scale, so a scale measured between two masters no longer holds between
/// their starless plates (1.8 percent apart on LDN 1622). <see cref="StarlessAsync"/> hands a set of planes that fits
/// <c>[0, 1]</c> over as it is, and divides a set above 1 by ONE divisor, the largest value among them, and multiplies it
/// back.</para>
/// </remarks>
public static class NarrowbandCombination
{
    /// <summary>
    /// Every plane with its stars removed by <paramref name="remover"/>, each back on its own scale, absent pixels filled with
    /// the plane's median for the remover and absent again after. A set of planes that fits <c>[0, 1]</c> goes to the remover
    /// as it is, as the enhance has always handed its remover a plate; only a set above 1 (a deblurred one, a master in
    /// counts) is divided, all by one divisor, the largest finite value among them.
    /// </summary>
    public static Task<Image[]> StarlessAsync(IReadOnlyList<Image> planes, IStarRemover remover, CancellationToken cancellationToken = default)
        => StarlessAsync(planes, remover, EnhanceOptions.Default, progress: null, cancellationToken);

    /// <summary><see cref="StarlessAsync(IReadOnlyList{Image}, IStarRemover, CancellationToken)"/> with the remover run under
    /// <paramref name="options"/> (its backend and tuning) and reporting to <paramref name="progress"/>: how the enhance
    /// program runs it.</summary>
    public static async Task<Image[]> StarlessAsync(IReadOnlyList<Image> planes, IStarRemover remover, EnhanceOptions options,
        IProgress<float>? progress, CancellationToken cancellationToken = default)
        => (await ThroughOneScaleAsync(planes, input => remover.EnhanceAsync(input, options, progress, cancellationToken), headroom: 1f,
            declaresUnitScale: false, cancellationToken)).Planes;

    /// <summary>
    /// Every plane split into its starless plate and its stars (the plane less the plate) on one scale
    /// (<see cref="StarlessAsync"/>), the remover's own output with no core protected and no dig filled, only held at or under
    /// its plane (<see cref="AtOrUnderPlane"/>), so the stars are never negative.
    /// </summary>
    public static Task<(Image[] Starless, Image[] Stars)> SplitStarsAsync(
        IReadOnlyList<Image> planes, IStarRemover remover, CancellationToken cancellationToken = default)
        => SplitStarsAsync(planes, remover, holdAtOrUnderPlane: true, cancellationToken);

    /// <summary>
    /// <see cref="SplitStarsAsync(IReadOnlyList{Image}, IStarRemover, CancellationToken)"/>; with
    /// <paramref name="holdAtOrUnderPlane"/> false the plate is left as the remover wrote it, so a measurement reads the
    /// remover's own overshoot (the stars layer may then hold negative values).
    /// </summary>
    public static async Task<(Image[] Starless, Image[] Stars)> SplitStarsAsync(
        IReadOnlyList<Image> planes, IStarRemover remover, bool holdAtOrUnderPlane, CancellationToken cancellationToken = default)
    {
        var (starless, stars, _, _) = await SplitStarsAsync(planes, remover, protectCores: null, fillDigs: false, catalogStars: null,
            holdAtOrUnderPlane, cancellationToken);
        return (starless, stars);
    }

    /// <summary>
    /// <see cref="SplitStarsAsync(IReadOnlyList{Image}, IStarRemover, CancellationToken)"/> with the two steps that finish a
    /// plate before its stars are read off it: <c>image remove-stars</c>, and the recipe's star split.
    /// <para>With <paramref name="protectCores"/> each plate takes its plane back in the plane's extended cores
    /// (<see cref="ExtendedCoreMask"/>, #1658), so no galaxy nucleus or nebula knot the remover took ends up in the stars. Also
    /// each plane's cores; null without <paramref name="protectCores"/>.</para>
    /// <para>With <paramref name="fillDigs"/>, and only for a remover that takes it (<see cref="IStarRemover.TakesDigFill"/>:
    /// TianWen's own, never RC-Astro's), each plate then has the holes the model dug under its stars filled
    /// (<see cref="DigFill"/>, #2204). Also the share of each plate's pixels the step could change; null when it did not run.</para>
    /// <para>Whatever ran, each plate is then held at or under its plane (<see cref="AtOrUnderPlane"/>, #2433).</para>
    /// <para>The stars are the plane less the FINISHED plate, so plate plus stars is the plane whatever ran, and the stars
    /// are never negative.</para>
    /// </summary>
    public static Task<(Image[] Starless, Image[] Stars, ExtendedCoreMask[]? Cores, double[]? DigFillShare)> SplitStarsAsync(
        IReadOnlyList<Image> planes, IStarRemover remover, ExtendedCoreMask.Options? protectCores, bool fillDigs,
        CancellationToken cancellationToken = default)
        => SplitStarsAsync(planes, remover, protectCores, fillDigs, catalogStars: null, cancellationToken);

    /// <summary>
    /// <see cref="SplitStarsAsync(IReadOnlyList{Image}, IStarRemover, ExtendedCoreMask.Options?, bool, CancellationToken)"/>
    /// with the frame's catalogued stars (<see cref="CatalogFrameStars"/>): those the planes show inside a protected core
    /// stay removed instead of coming back with the core (<see cref="CoreStars"/>, #2242). All planes are on one grid, so
    /// one list serves them all; null is the split without it.
    /// </summary>
    public static Task<(Image[] Starless, Image[] Stars, ExtendedCoreMask[]? Cores, double[]? DigFillShare)> SplitStarsAsync(
        IReadOnlyList<Image> planes, IStarRemover remover, ExtendedCoreMask.Options? protectCores, bool fillDigs,
        IReadOnlyList<FrameStar>? catalogStars, CancellationToken cancellationToken = default)
        => SplitStarsAsync(planes, remover, protectCores, fillDigs, catalogStars, holdAtOrUnderPlane: true, cancellationToken);

    /// <summary>
    /// The split with its last step switchable: with <paramref name="holdAtOrUnderPlane"/> false each plate is left as the
    /// cores and the dig fill (or neither) made it, so a measurement reads the remover's own overshoot, and the stars layer
    /// may hold negative values (#2433).
    /// </summary>
    public static Task<(Image[] Starless, Image[] Stars, ExtendedCoreMask[]? Cores, double[]? DigFillShare)> SplitStarsAsync(
        IReadOnlyList<Image> planes, IStarRemover remover, ExtendedCoreMask.Options? protectCores, bool fillDigs,
        IReadOnlyList<FrameStar>? catalogStars, bool holdAtOrUnderPlane, CancellationToken cancellationToken = default)
        => SplitStarsAsync(planes, remover, protectCores, fillDigs, catalogStars, holdAtOrUnderPlane, EnhanceOptions.Default, progress: null,
            cancellationToken);

    /// <summary>
    /// The one star removal, for <c>image remove-stars</c> and the enhance program alike (the owner's choice, 2026-11-26: the
    /// guarded split, its cores protected and its plate held at or under its plane): the split above with the remover run under
    /// <paramref name="options"/> and reporting to <paramref name="progress"/>.
    /// </summary>
    public static async Task<(Image[] Starless, Image[] Stars, ExtendedCoreMask[]? Cores, double[]? DigFillShare)> SplitStarsAsync(
        IReadOnlyList<Image> planes, IStarRemover remover, ExtendedCoreMask.Options? protectCores, bool fillDigs,
        IReadOnlyList<FrameStar>? catalogStars, bool holdAtOrUnderPlane, EnhanceOptions options, IProgress<float>? progress,
        CancellationToken cancellationToken = default)
    {
        var starless = await StarlessAsync(planes, remover, options, progress, cancellationToken);
        var cores = protectCores is null ? null : new ExtendedCoreMask[planes.Count];
        var filled = fillDigs && remover.TakesDigFill ? new double[planes.Count] : null;
        var stars = new Image[planes.Count];
        for (var i = 0; i < planes.Count; i++)
        {
            if (cores is not null)
            {
                cores[i] = ExtendedCoreMask.Find(planes[i], protectCores, catalogStars);
                if (cores[i].Cores.Length > 0)
                {
                    var plate = starless[i];
                    starless[i] = cores[i].Apply(planes[i], plate);
                    plate.Release();
                }
            }
            // Cores first, digs second, the fill read against the ORIGINAL plane: the order the step was scored in (the
            // whole masters went through the model with core protection, then through the dig fill against their inputs).
            // It is also the order that keeps the two apart: inside a protected core the plate IS the plane, so the fill
            // sees nothing taken there and leaves the nucleus alone, where filling first would work on a nucleus the model
            // dug into and the core's feathered edge would then blend that work back in. The one place inside a core where
            // the plate is NOT the plane is the footprint of a catalogued star (catalogStars, #2242): there the remover's
            // plate stands, and the fill treats it as it treats a removed star anywhere, fading out where the sky around it
            // cannot be trusted.
            if (filled is not null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var plate = starless[i];
                (starless[i], filled[i]) = DigFill.Apply(planes[i], plate);
                if (!ReferenceEquals(starless[i], plate))
                {
                    plate.Release();
                }
            }
            // Last, after whatever ran: the plate is held at or under its plane, so the stars are never negative (#2433).
            if (holdAtOrUnderPlane)
            {
                var plate = starless[i];
                starless[i] = AtOrUnderPlane(plate, planes[i]);
                plate.Release();
            }
            stars[i] = Stars(planes[i], starless[i]);
        }
        return (starless, stars, cores, filled);
    }

    /// <summary>
    /// <c>image fill-digs</c>, the split's last two steps on a plate the remover wrote: the dig fill (<see cref="DigFill.Apply"/>)
    /// and then, unless <paramref name="holdAtOrUnderPlane"/> is false, the hold at or under the plane (<see cref="AtOrUnderPlane"/>,
    /// #2433). On the plate of <c>image remove-stars --no-protect-cores --no-fill-digs --keep-overshoot</c> it gives the plate of
    /// <c>image remove-stars --no-protect-cores</c> bit for bit, so the step alone and the one command agree. The caller keeps
    /// <paramref name="plate"/>; what comes back may be that same image when nothing was filled and nothing held.
    /// </summary>
    public static (Image Plate, double Share) FillDigsAlone(Image plane, Image plate, bool holdAtOrUnderPlane = true)
    {
        var (filled, share) = DigFill.Apply(plane, plate);
        if (!holdAtOrUnderPlane)
        {
            return (filled, share);
        }
        var held = AtOrUnderPlane(filled, plane);
        if (!ReferenceEquals(filled, plate))
        {
            filled.Release();
        }
        return (held, share);
    }

    /// <summary>
    /// <paramref name="broadband"/> with <paramref name="line"/>'s emission in it: the line's continuum taken out against the
    /// broadband at <paramref name="continuumScale"/> (<see cref="ContinuumSubtractor.Subtract"/>), then added at
    /// <paramref name="weight"/> times what a line value is worth in the broadband (<see cref="LineToBroadband"/>, which
    /// reads the exposures and the fitted scales off the two). <c>image add-line</c>, and the recipe's H-alpha step; also the
    /// weight it applied.
    /// </summary>
    public static (Image WithLine, double AppliedWeight) AddLineFrom(Image broadband, Image line, double continuumScale, double weight)
    {
        var pure = ContinuumSubtractor.Subtract(line, broadband, continuumScale);
        var applied = weight * LineToBroadband(line.ImageMeta, broadband.ImageMeta);
        var withLine = AddLine(broadband, pure, applied);
        pure.Release();
        return (withLine, applied);
    }

    /// <summary>How far below the ceiling of <c>[0, 1]</c> a plane's peak is put before a deblurrer sees it: BlurX lifted
    /// LDN 1622's brightest star to 3.4 times its input peak, and its output stops at 1, so at the peak's own scale 197 pixels
    /// of green came back clipped at 1 and every bright star lost flux. At 4 none did, and the result agreed with 8's to
    /// 0.7 percent on star pixels, the sky to a thousandth of its noise.</summary>
    public const float DefaultDeblurHeadroom = 4f;

    /// <summary>
    /// Every plane deblurred by <paramref name="deblurrer"/> (RC-Astro BlurXTerminator), each back on its own scale: all
    /// divided by one divisor, <paramref name="headroom"/> times the largest finite value among them, so a sharpened
    /// star's peak stays below the deblurrer's ceiling; absent pixels filled for it and absent again after. Also how many
    /// output pixels reached that ceiling anyway, which should be none.
    /// </summary>
    /// <remarks>
    /// Run each mono channel through it before stars are removed, as BlurX is meant to be used: it brings the channels'
    /// stars to nearly one width (LDN 1622: 1.97 to 2.24 px before, 1.41 to 1.46 after) where blurring them to the widest
    /// (<see cref="PsfMatch"/>) gives up the detail the luminance is for.
    /// </remarks>
    public static async Task<(Image[] Planes, long AtCeiling)> DeblurAsync(
        IReadOnlyList<Image> planes, IImageDeblurrer deblurrer, float headroom = DefaultDeblurHeadroom, CancellationToken cancellationToken = default)
    {
        var (deblurred, atCeiling, _) = await DeblurAsync(planes, deblurrer, headroom, EnhanceOptions.Default, progress: null, cancellationToken);
        return (deblurred, atCeiling);
    }

    /// <summary>
    /// <see cref="DeblurAsync(IReadOnlyList{Image}, IImageDeblurrer, float, CancellationToken)"/> with the deblurrer run under
    /// <paramref name="options"/> and reporting to <paramref name="progress"/>: how the enhance program runs it, so the enhance
    /// and <c>image deblur</c> are one deblur (the owner's choice, 2026-11-26: keep the lifted star's light). Also whether the
    /// deblurrer handed every plane back untouched (present but unlicensed), so the caller can say it did nothing.
    /// </summary>
    public static Task<(Image[] Planes, long AtCeiling, bool PassedThrough)> DeblurAsync(
        IReadOnlyList<Image> planes, IImageDeblurrer deblurrer, float headroom, EnhanceOptions options, IProgress<float>? progress,
        CancellationToken cancellationToken = default)
        => ThroughOneScaleAsync(planes, input => deblurrer.EnhanceAsync(input, options, progress, cancellationToken), headroom,
            declaresUnitScale: true, cancellationToken);

    /// <summary>
    /// Every plane denoised by <paramref name="denoiser"/> (RC-Astro NoiseXTerminator where licensed) under
    /// <paramref name="options"/> (its strength), each back on its own scale, absent pixels absent, in the gated passes the
    /// enhance runs (<see cref="DenoisePassesAsync"/>). Hand it STARLESS planes: a star is never denoised, as the enhance
    /// pipeline's split program never does (its denoise runs on the starless plate).
    /// </summary>
    public static Task<Image[]> DenoiseAsync(
        IReadOnlyList<Image> planes, IDenoiseEnhancer denoiser, EnhanceOptions options, CancellationToken cancellationToken = default)
        => DenoisePassesAsync(planes, denoiser, DenoiseVariant.Default, options, progress: null, logger: null, cancellationToken);

    /// <summary>
    /// The one denoise, for the single step and the enhance program alike (the owner's choice, 2026-11-26): every plane through
    /// <paramref name="denoiser"/> on one scale in passes, each pass one denoiser call on the last one's output
    /// (NoiseXTerminator at <c>--it 1</c>, never its own iterations, which left the Sculptor's residual unchanged at 2 and 3 for
    /// twice and three times the time). Pass 1 always runs. A further pass is kept for a plane only while it takes at least
    /// <see cref="SharpenPipeline.MinCoarseReduction"/> off that plane's coarse sky noise (<see cref="MultiScaleNoise"/>, the 16
    /// and 32 px bands), measured on the same sky pixels before and after; the first that does not is discarded and ends the
    /// plane's passes, up to <see cref="SharpenPipeline.MaxDenoisePasses"/>. A stated <see cref="EnhanceTuning.DenoiseIterations"/>
    /// is that many passes, ungated. A single-pixel noise estimate cannot gate this: one pass on the Sculptor took 70 % off at
    /// 1 px and only 37 % at 32 px, so the blotches a second pass removes are invisible to it.
    /// </summary>
    public static async Task<Image[]> DenoisePassesAsync(
        IReadOnlyList<Image> planes, IDenoiseEnhancer denoiser, DenoiseVariant variant, EnhanceOptions options, IProgress<float>? progress,
        ILogger? logger, CancellationToken cancellationToken = default)
    {
        Task<Image> Pass(Image input) => denoiser.EnhanceAsync(input, variant, options, progress, cancellationToken);

        var current = (await ThroughOneScaleAsync(planes, Pass, headroom: 1f, declaresUnitScale: false, cancellationToken)).Planes;
        var stated = options.Tuning?.DenoiseIterations;
        var passes = Math.Clamp(stated ?? SharpenPipeline.MaxDenoisePasses, 1, SharpenPipeline.MaxDenoisePasses);
        var noise = new MultiScaleNoise?[current.Length];
        var going = new List<int>(current.Length);
        for (var i = 0; i < current.Length; i++)
        {
            noise[i] = stated is null ? MultiScaleNoise.Measure(current[i]) : null;
            // A plane whose sky cannot be measured cannot be gated, and is not denoised again on a guess.
            if (stated is not null || noise[i] is not null)
            {
                going.Add(i);
            }
        }

        for (var pass = 2; pass <= passes && going.Count > 0; pass++)
        {
            var (next, _, passedThrough) = await ThroughOneScaleAsync([.. going.Select(i => current[i])], Pass, headroom: 1f,
                declaresUnitScale: false, cancellationToken);
            if (passedThrough)
            {
                // A denoiser that hands its input back changed nothing, and a further pass would change nothing either.
                foreach (var image in next)
                {
                    image.Release();
                }
                break;
            }

            var stillGoing = new List<int>(going.Count);
            for (var k = 0; k < going.Count; k++)
            {
                var i = going[k];
                if (stated is null && noise[i] is { } measured)
                {
                    var after = MultiScaleNoise.Measure(next[k], onSkyOf: measured);
                    var reduction = after is null ? 0f : 1f - (after.Coarse / measured.Coarse);
                    logger?.LogInformation("Denoise pass {Pass} took {Reduction:P0} off the coarse sky noise ({Before:E2} to {After:E2}); {Verdict}",
                        pass, reduction, measured.Coarse, after?.Coarse ?? float.NaN,
                        reduction >= SharpenPipeline.MinCoarseReduction ? "kept" : "discarded, the passes stop");
                    if (after is null || reduction < SharpenPipeline.MinCoarseReduction)
                    {
                        next[k].Release();
                        continue;
                    }

                    noise[i] = after;
                }

                current[i].Release();
                current[i] = next[k];
                stillGoing.Add(i);
            }

            going = stillGoing;
        }

        return current;
    }

    /// <summary>
    /// Three mono planes denoised together as one colour image, so the denoiser sees their colour, and handed back as
    /// three, each with its own metadata.
    /// </summary>
    public static async Task<Image[]> DenoiseColourAsync(
        Image red, Image green, Image blue, IDenoiseEnhancer denoiser, EnhanceOptions options, CancellationToken cancellationToken = default)
    {
        var colour = Rgb(red, green, blue);
        var denoised = (await DenoiseAsync([colour], denoiser, options, cancellationToken))[0];
        colour.Release();
        Image[] sources = [red, green, blue];
        var result = new Image[3];
        for (var c = 0; c < 3; c++)
        {
            var src = denoised.GetChannelSpan(c);
            var plane = new float[red.Height, red.Width];
            for (var y = 0; y < red.Height; y++)
            {
                for (var x = 0; x < red.Width; x++)
                {
                    plane[y, x] = src[(y * red.Width) + x];
                }
            }
            result[c] = new Image([plane], BitDepth.Float32, sources[c].MaxValue, sources[c].MinValue, sources[c].Pedestal, sources[c].ImageMeta);
        }
        denoised.Release();
        return result;
    }

    // Every plane through one enhancer on ONE scale: a scale measured between two planes then still holds between their
    // results, which a divisor per plane breaks (1.8 percent on LDN 1622's starless pair). `declaresUnitScale` is for the
    // routine that can lift a highlight past 2.0 (the deblur): its result says it is on its input's unit scale (#2504). The
    // others carry what their input says and nothing more, so a file nothing lifted keeps its bytes. `enhance` is the one
    // call of the enhancer, with whatever options and progress its caller runs it under; PassedThrough says the enhancer
    // handed back every input it was given (a deblurrer present but unlicensed), so the caller can say it did nothing.
    private static async Task<(Image[] Planes, long AtCeiling, bool PassedThrough)> ThroughOneScaleAsync(
        IReadOnlyList<Image> planes, Func<Image, Task<Image>> enhance, float headroom, bool declaresUnitScale,
        CancellationToken cancellationToken)
    {
        var peak = 0f;
        foreach (var plane in planes)
        {
            for (var c = 0; c < plane.ChannelCount; c++)
            {
                foreach (var v in plane.GetChannelSpan(c))
                {
                    if (float.IsFinite(v) && v > peak)
                    {
                        peak = v;
                    }
                }
            }
        }
        // The deblur goes in with headroom below its model's ceiling, since a sharpened star lifts above its input's peak. The
        // others (star removal, denoise) go in as they are where they fit the model's [0, 1], as the enhance has always handed
        // its models a plate: NoiseXTerminator sets its automatic strength from the absolute noise it is handed, so a plate
        // divided up to a peak of 1 would be denoised as if it were that much noisier. Only a plate that does not fit (a
        // deblurred one, above 1; a master in counts) is divided, by its peak, still one divisor for every plane.
        var divisor = peak <= 0f ? 1f : headroom > 1f ? peak * headroom : MathF.Max(peak, 1f);

        var result = new Image[planes.Count];
        long atCeiling = 0;
        var passedThrough = 0;
        for (var i = 0; i < planes.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var plane = planes[i];
            var (channels, width, height) = plane.Shape;
            var scaled = new float[channels][,];
            for (var c = 0; c < channels; c++)
            {
                var src = plane.GetChannelSpan(c);
                var fill = FiniteMedian(src);
                var dst = new float[height, width];
                for (var y = 0; y < height; y++)
                {
                    for (var x = 0; x < width; x++)
                    {
                        var v = src[(y * width) + x];
                        dst[y, x] = (float.IsFinite(v) ? v : fill) / divisor;
                    }
                }
                scaled[c] = dst;
            }
            var input = new Image(scaled, BitDepth.Float32, 1f / headroom, 0f, 0f, plane.ImageMeta);
            var output = await enhance(input);
            if (ReferenceEquals(output, input))
            {
                passedThrough++;
            }
            input.Release();

            // Labelled with the peak and floor it HOLDS, not the input's: a deblurred star stands 3.4 times above the
            // input's peak, and a label below the data is a scale every later step reads wrong.
            var back = new float[channels][,];
            var max = float.NegativeInfinity;
            var min = float.PositiveInfinity;
            for (var c = 0; c < channels; c++)
            {
                var src = plane.GetChannelSpan(c);
                var enhanced = output.GetChannelSpan(c);
                var dst = new float[height, width];
                for (var y = 0; y < height; y++)
                {
                    for (var x = 0; x < width; x++)
                    {
                        var at = (y * width) + x;
                        if (enhanced[at] >= CeilingTolerance)
                        {
                            atCeiling++;
                        }
                        var v = float.IsFinite(src[at]) ? enhanced[at] * divisor : float.NaN;
                        dst[y, x] = v;
                        if (float.IsFinite(v))
                        {
                            max = Math.Max(max, v);
                            min = Math.Min(min, v);
                        }
                    }
                }
                back[c] = dst;
            }
            output.Release();
            // On the input's scale, so on a unit scale when the input was, and the deblur says so: a deblurred star stands
            // above 2.0, where the peak alone would call the result ADU (#2504). Said in memory, so the combine, which
            // composes the deblurred planes before anything is written, gets it as well as a written file. This is the one
            // deblur: the enhance pipeline's DeblurStep runs it too, so the combine, image deblur, the viewer and image sharpen
            // all get a lifted result that says its scale.
            var meta = declaresUnitScale && plane.IsUnitReferred && !plane.ImageMeta.UnitReferred
                ? plane.ImageMeta with { UnitReferred = true }
                : plane.ImageMeta;
            var stepped = float.IsFinite(max)
                ? new Image(back, BitDepth.Float32, max, min, plane.Pedestal, meta)
                : new Image(back, BitDepth.Float32, plane.MaxValue, plane.MinValue, plane.Pedestal, meta);
            // How much quieter the step left the plane, as the enhance program records it (ImageMeta.CompoundNoiseReduction),
            // so a plate denoised here is not stretched as that much cleaner data by every auto stretch after.
            var noiseReduction = ImageMeta.CompoundNoiseReduction(plane.EstimateNoiseProfile(), stepped.EstimateNoiseProfile(),
                plane.ImageMeta.NoiseReduction);
            result[i] = noiseReduction == plane.ImageMeta.NoiseReduction
                ? stepped
                : new Image(back, BitDepth.Float32, stepped.MaxValue, stepped.MinValue, stepped.Pedestal,
                    meta with { NoiseReduction = noiseReduction });
        }
        return (result, atCeiling, planes.Count > 0 && passedThrough == planes.Count);
    }

    // An enhancer's [0, 1] ceiling, less a float's rounding.
    private const float CeilingTolerance = 0.9999f;

    /// <summary>The stars a remover took out: the plane less its starless plate, absent where either is.</summary>
    public static Image Stars(Image plane, Image starless) => Combine(plane, starless, static (a, b) => a - b);

    /// <summary>
    /// <paramref name="plate"/> held at or under <paramref name="plane"/>, pixel by pixel and channel by channel (the plate's
    /// own metadata kept, absent where either is), so the stars read off it (<see cref="Stars"/>) are never negative. A stars
    /// layer cannot hold negative light: where a plate stands over its plane, the plane less the plate is negative (#2433).
    /// This guarantees the stars are never negative and plate plus stars is the plane; it does not make a screen blend of the
    /// stars onto the plate give the plane back (that is short of it by plate times stars). On the three held-out whole masters
    /// TianWen's own plate stood over its input on 4 to 16 % of the pixels, mostly by a few hundredths to a few tenths of the
    /// noise; StarXTerminator's plate stood over its input on 0 to 0.6 % of the pixels. The clamp lowered a frame's mean by 0.006 to 0.026 of the noise.
    /// </summary>
    public static Image AtOrUnderPlane(Image plate, Image plane) => Combine(plate, plane, static (p, input) => MathF.Min(p, input));

    /// <summary>A starless plane with its stars (<see cref="Stars"/>) put back.</summary>
    public static Image WithStars(Image starless, Image stars) => Combine(starless, stars, static (a, b) => a + b);

    /// <summary>
    /// What a line master's values are worth in a broadband channel's: the ratio of their exposures, times the ratio of
    /// the scales each was put on since it was taken (<see cref="ImageMeta.FluxScale"/>, a linear fit's slope). Both filters
    /// pass the line at much the same transmission (a red filter and an H-alpha one both near their peak at 656 nm); a
    /// filter curve would refine it. The exposure ratio is one when either exposure is unknown.
    /// </summary>
    public static double LineToBroadband(in ImageMeta line, in ImageMeta broadband)
    {
        var exposure = line.ExposureDuration > TimeSpan.Zero && broadband.ExposureDuration > TimeSpan.Zero
            ? broadband.ExposureDuration.TotalSeconds / line.ExposureDuration.TotalSeconds
            : 1.0;
        return exposure * (broadband.FluxScale ?? 1.0) / (line.FluxScale ?? 1.0);
    }

    /// <summary>
    /// <paramref name="broadband"/> with <paramref name="pureLine"/>'s emission added, <c>broadband + weight (line - median
    /// line)</c>, so the line's background adds nothing; absent where either is.
    /// </summary>
    public static Image AddLine(Image broadband, Image pureLine, double weight)
    {
        var median = FiniteMedian(pureLine.GetChannelSpan(0));
        return Combine(broadband, pureLine, (a, b) => (float)(a + (weight * (b - median))));
    }

    /// <summary>One colour image from three mono masters on one grid, the red master's metadata, the largest of their
    /// full scales.</summary>
    public static Image Rgb(Image red, Image green, Image blue)
    {
        if (red.Width != green.Width || red.Width != blue.Width || red.Height != green.Height || red.Height != blue.Height)
        {
            throw new ArgumentException("the three masters must be on one grid (MasterAlignment, tianwen image align)");
        }
        var planes = new float[3][,];
        var source = new[] { red, green, blue };
        for (var c = 0; c < 3; c++)
        {
            var src = source[c].GetChannelSpan(0);
            var plane = new float[red.Height, red.Width];
            for (var y = 0; y < red.Height; y++)
            {
                for (var x = 0; x < red.Width; x++)
                {
                    plane[y, x] = src[(y * red.Width) + x];
                }
            }
            planes[c] = plane;
        }
        var meta = red.ImageMeta with { SensorType = SensorType.Color, Filter = Filter.None };
        return new Image(planes, BitDepth.Float32, Math.Max(red.MaxValue, Math.Max(green.MaxValue, blue.MaxValue)), 0f, red.Pedestal, meta);
    }

    private static Image Combine(Image a, Image b, Func<float, float, float> op)
    {
        if (a.Width != b.Width || a.Height != b.Height)
        {
            throw new ArgumentException($"{a.Width}x{a.Height} against {b.Width}x{b.Height}: put them on one grid first");
        }
        var (channels, width, height) = a.Shape;
        var planes = new float[channels][,];
        for (var c = 0; c < channels; c++)
        {
            var x = a.GetChannelSpan(c);
            var y = b.GetChannelSpan(Math.Min(c, b.ChannelCount - 1));
            var plane = new float[height, width];
            for (var py = 0; py < height; py++)
            {
                for (var px = 0; px < width; px++)
                {
                    var at = (py * width) + px;
                    plane[py, px] = float.IsFinite(x[at]) && float.IsFinite(y[at]) ? op(x[at], y[at]) : float.NaN;
                }
            }
            planes[c] = plane;
        }
        // Labelled with the range the result HOLDS, not a's: the stars put back on a plate stand above the plate's peak (a
        // deblur's lifted ones near 3, #2504), and a label below the data is a scale every later step reads wrong.
        var (min, max) = Image.ObservedRange(planes);
        return float.IsNaN(max)
            ? new Image(planes, BitDepth.Float32, a.MaxValue, a.MinValue, a.Pedestal, a.ImageMeta)
            : new Image(planes, BitDepth.Float32, max, min, a.Pedestal, a.ImageMeta);
    }

    private static float FiniteMedian(ReadOnlySpan<float> values)
    {
        var finite = new float[values.Length];
        var n = 0;
        foreach (var v in values)
        {
            if (float.IsFinite(v))
            {
                finite[n++] = v;
            }
        }
        return n > 0 ? StatisticsHelper.MedianFast(finite.AsSpan(0, n)) : 0f;
    }
}
