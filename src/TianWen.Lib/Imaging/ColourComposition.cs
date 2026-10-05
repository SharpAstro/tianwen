using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Imaging.Enhancement;

namespace TianWen.Lib.Imaging;

/// <summary>A broadband channel of a mono colour set, in the order the masters are given.</summary>
public enum ColourChannel
{
    Red = 0,
    Green = 1,
    Blue = 2,
}

/// <summary>What <see cref="ColourComposition.RunAsync"/> does, step by step; each switch is one step's verb run by
/// itself.</summary>
public sealed record ColourCompositionOptions
{
    /// <summary>The colour PixInsight's LinearFit puts the others on (<c>image linear-fit</c>); null leaves them.</summary>
    public ColourChannel? LinearFitTo { get; init; } = ColourChannel.Green;

    /// <summary>BlurX on every master before its stars come out (<c>image deblur</c>).</summary>
    public bool Deblur { get; init; }

    /// <summary>The blur match, the fallback without a deblurrer (<c>image match-psf</c>), for the colour only.</summary>
    public bool MatchPsf { get; init; }

    /// <summary>Stars out before the line goes in, and back last (<c>image remove-stars</c>, <c>image add-stars</c>).</summary>
    public bool Starless { get; init; }

    /// <summary>The continuum scale against red as given (<c>image continuum</c>); null measures it.</summary>
    public double? LineScale { get; init; }

    /// <summary>How much of the pure line goes into red, in red's own units (<c>image add-line</c>).</summary>
    public double LineWeight { get; init; } = 1.0;

    /// <summary>Make the synthetic luminance (<c>image luminance</c>); <see cref="Lrgb"/> makes it too.</summary>
    public bool Luminance { get; init; }

    /// <summary>Give every channel the luminance's detail (<c>image lrgb</c>).</summary>
    public bool Lrgb { get; init; }

    /// <summary>The blur the channels keep their own colour above.</summary>
    public float ColourSigma { get; init; } = LuminanceDetail.DefaultColourSigma;

    /// <summary>Denoise the starless planes (<c>image denoise</c>); needs <see cref="Starless"/>.</summary>
    public bool Denoise { get; init; }

    /// <summary>The denoise for the planes that carry the detail: the luminance, or the colour without one.</summary>
    public EnhanceOptions DetailDenoise { get; init; } = EnhanceOptions.Default;

    /// <summary>The lighter denoise for the colour when a luminance carries the detail.</summary>
    public EnhanceOptions ColourDenoise { get; init; } = DefaultColourDenoise;

    /// <summary>NoiseXTerminator at half strength.</summary>
    public static readonly EnhanceOptions DefaultColourDenoise = new(Tuning: new EnhanceTuning(DenoiseStrength: 0.5f));
}

/// <summary>The enhancers a composition calls on: where a role is absent, or does not serve, its step says so.</summary>
public sealed record ColourCompositionRoles(IStarRemover? StarRemover, IImageDeblurrer? Deblurrer, IDenoiseEnhancer? Denoiser);

/// <summary>
/// Red, green and blue mono masters (and an H-alpha one) on one grid made one colour image the way a PixInsight mono
/// workflow makes it: linear fit, deblur, stars out, the line in, the luminance, its detail in every channel, stars back.
/// docs/plans/narrowband-colour.md.
/// </summary>
/// <remarks>
/// <para><b>Every step is a verb of its own, and this only runs them in order.</b> Each step here is ONE call into the
/// routine its verb calls (<see cref="LinearFit"/>, <see cref="NarrowbandCombination.DeblurAsync"/>,
/// <see cref="PsfMatch"/>, <see cref="ContinuumSubtractor"/>, <see cref="NarrowbandCombination.StarlessAsync"/>,
/// <see cref="NarrowbandCombination.AddLine"/>, <see cref="SyntheticLuminance"/>, <see cref="NarrowbandCombination.DenoiseAsync"/>,
/// <see cref="LuminanceDetail"/>, <see cref="NarrowbandCombination.WithStars"/>), with nothing between them a file would
/// not carry: a line's worth in a fitted channel travels as <see cref="ImageMeta.FluxScale"/>, and the detail scale is
/// measured from the planes it is applied to. So running the verbs one by one gives the same image, which
/// <c>ColourCompositionTests</c> pins.</para>
/// </remarks>
public static class ColourComposition
{
    /// <summary>The colour image as three planes, linear, and the luminance where one was asked for (its stars in).</summary>
    public sealed record Result(Image Red, Image Green, Image Blue, Image? Luminance);

    /// <summary>Why these masters and options cannot run, or null when they can.</summary>
    public static string? Validate(IReadOnlyList<Image> masters, ColourCompositionOptions options, ColourCompositionRoles roles)
    {
        if (masters.Count is not (3 or 4))
        {
            return "red, green and blue, and an optional H-alpha: three or four masters";
        }
        foreach (var m in masters)
        {
            if (m.Width != masters[0].Width || m.Height != masters[0].Height)
            {
                return "the masters are not on one grid: put them there first (tianwen image align)";
            }
        }
        if (options.Denoise && !options.Starless)
        {
            return "--denoise needs --starless: the stars are never denoised";
        }
        if (options.Starless && roles.StarRemover is null)
        {
            return "--starless needs a star remover (RC-Astro StarXTerminator)";
        }
        return null;
    }

    /// <summary>
    /// The composition of <paramref name="masters"/> (red, green, blue, and H-alpha when there is a fourth), saying each
    /// step to <paramref name="say"/>. Throws <see cref="InvalidOperationException"/> with a reason a user can act on when a
    /// step cannot measure what it needs.
    /// </summary>
    public static async Task<Result> RunAsync(
        IReadOnlyList<Image> masters, ColourCompositionOptions options, ColourCompositionRoles roles, Action<string> say,
        CancellationToken cancellationToken = default)
    {
        if (Validate(masters, options, roles) is { } problem)
        {
            throw new InvalidOperationException(problem);
        }
        var hasLine = masters.Count == 4;
        var planes = new List<Image>(masters);

        // One scale for the broadband channels (LinearFit). H-alpha is left as it is: its sky and its emission are not a
        // broadband channel's, and the continuum scale is what relates it to red.
        if (options.LinearFitTo is { } fitTo)
        {
            var reference = (int)fitTo;
            for (var i = 0; i < 3; i++)
            {
                if (i == reference)
                {
                    continue;
                }
                var fit = LinearFit.Measure(planes[i], planes[reference]);
                if (!double.IsFinite(fit.Slope) || fit.Slope <= 0)
                {
                    throw new InvalidOperationException($"no linear fit of {Name(i)} onto {Name(reference)} ({fit.Pixels} pixels)");
                }
                planes[i] = LinearFit.Apply(planes[i], fit);
                say(Say($"linear fit {Name(i)} onto {Name(reference)}: {fit.Offset:G5} + {fit.Slope:G5} x {Name(i)} ({fit.Pixels} pixels, mean absolute deviation {fit.MeanAbsoluteDeviation:G4})"));
            }
        }

        // Stars at one width. BlurX does it without giving up detail; the blur match gives it up, so its planes serve the
        // colour and the measurements while the luminance is made from the unblurred ones.
        IReadOnlyList<Image> sharp = planes;
        var colour = sharp;
        var colourBlurred = false;
        var matchPsf = options.MatchPsf;
        if (options.Deblur && IEnhancerAvailability.Serves(roles.Deblurrer, 1, EnhanceOptions.Default))
        {
            say(Say($"deblurring {planes.Count} masters ({roles.Deblurrer.Name}), the brightest star at 1/{NarrowbandCombination.DefaultDeblurHeadroom:G3} of the ceiling"));
            var before = new double[planes.Count];
            for (var i = 0; i < planes.Count; i++)
            {
                before[i] = MasterAlignment.MedianFwhm(await MasterAlignment.FindStarsAsync(planes[i], cancellationToken));
            }
            var (deblurred, atCeiling) = await NarrowbandCombination.DeblurAsync(planes, roles.Deblurrer, cancellationToken: cancellationToken);
            sharp = deblurred;
            colour = sharp;
            for (var i = 0; i < sharp.Count; i++)
            {
                var after = MasterAlignment.MedianFwhm(await MasterAlignment.FindStarsAsync(sharp[i], cancellationToken));
                say(Say($"{Name(i)}: FWHM {before[i]:F2} px, deblurred to {after:F2}"));
            }
            if (atCeiling > 0)
            {
                say($"{atCeiling} deblurred pixels reached the deblurrer's ceiling and are clipped.");
            }
        }
        else
        {
            if (options.Deblur)
            {
                say("--deblur: no deblurrer serves here (RC-Astro BlurXTerminator, installed and licensed); matching the star widths by blurring instead.");
                matchPsf = true;
            }
            if (matchPsf)
            {
                var (matched, report, target) = await PsfMatch.ToWidestAsync(planes, cancellationToken);
                colour = matched;
                colourBlurred = true;
                for (var i = 0; i < report.Length; i++)
                {
                    say(Say($"{Name(i)}: FWHM {report[i].FwhmBefore:F2} px, blurred by sigma {report[i].Sigma:F2} to {report[i].FwhmAfter:F2} (target {target:F2}), for the colour only"));
                }
            }
        }

        // The continuum scale on the masters WITH their stars, which are what define it, at one width. One given against
        // red as it was goes onto the scales the two are on now.
        var k = 0.0;
        if (hasLine)
        {
            k = options.LineScale is { } given
                ? given * (colour[3].ImageMeta.FluxScale ?? 1.0) / (colour[0].ImageMeta.FluxScale ?? 1.0)
                : ContinuumSubtractor.FlattestResidualScale(colour[3], colour[0]).K;
            if (!double.IsFinite(k))
            {
                throw new InvalidOperationException("no continuum scale could be measured between H-alpha and red; pass --ha-scale");
            }
        }

        var colourParts = await ComposeAsync(colour, report: true);
        var final = colourParts.Full;
        Image? luminanceOut = null;
        if (options.Luminance || options.Lrgb)
        {
            // The luminance from planes never blurred, composed as the colour was; its star scales from the colour planes,
            // whose stars are at one width, and its noise from the unblurred ones, which a blur would flatter.
            var sharpParts = colourBlurred ? await ComposeAsync(sharp, report: false) : colourParts;
            var (luminance, parts, noise) = await SyntheticLuminance.BuildAsync(sharpParts.Full, colourBlurred ? colourParts.Full : null,
                reference: 1, cancellationToken);
            for (var i = 0; i < parts.Length; i++)
            {
                say(Say($"luminance {Name(i)}: scale {parts[i].Scale:G4} onto green ({parts[i].Stars} stars), noise {parts[i].ScaledNoise:G4} on green's scale over {SyntheticLuminance.NoiseBlockPx} px blocks, weight {parts[i].Weight:P1}"));
            }
            var best = Math.Min(parts[0].ScaledNoise, Math.Min(parts[1].ScaledNoise, parts[2].ScaledNoise));
            say(Say($"luminance noise {noise:G4} against the best single channel's {best:G4}: {best / noise:F2}x its signal to noise"));
            luminanceOut = luminance;

            // The luminance's own stars out, as StarX is run on a luminance; denoised starless, its stars never.
            Image? starlessLuminance = null;
            if (colourParts.Stars is not null && roles.StarRemover is { } remover)
            {
                say($"removing stars from the luminance ({remover.Name})");
                var (starlessLuminances, luminanceStars) = await NarrowbandCombination.SplitStarsAsync([luminance], remover, cancellationToken);
                starlessLuminance = starlessLuminances[0];
                if (options.Denoise)
                {
                    var denoised = await DenoisedAsync(starlessLuminance, options.DetailDenoise, "luminance");
                    luminanceOut = NarrowbandCombination.WithStars(denoised, luminanceStars[0]);
                    starlessLuminance = denoised;
                }
            }

            if (options.Lrgb)
            {
                if (colourParts.Stars is not { } colourStars || starlessLuminance is null)
                {
                    say("--lrgb without --starless: the stars take the detail too and will show coloured rims.");
                    final = [Detail(colourParts.Full[0], luminance, 0), Detail(colourParts.Full[1], luminance, 1), Detail(colourParts.Full[2], luminance, 2)];
                }
                else
                {
                    // On the starless channels, where the colour noise is; the stars go back with their own colour.
                    var colourStarless = options.Denoise ? await DenoisedColourAsync(colourParts.Starless, options.ColourDenoise) : colourParts.Starless;
                    final = [NarrowbandCombination.WithStars(Detail(colourStarless[0], starlessLuminance, 0), colourStars[0]),
                        NarrowbandCombination.WithStars(Detail(colourStarless[1], starlessLuminance, 1), colourStars[1]),
                        NarrowbandCombination.WithStars(Detail(colourStarless[2], starlessLuminance, 2), colourStars[2])];
                }
                say(Say($"every channel's detail below {options.ColourSigma:G3} px taken from the luminance{(colourParts.Stars is null ? "" : ", the stars kept as they were")}"));
            }
        }
        if (options.Denoise && !options.Lrgb && colourParts.Stars is { } starsOnly)
        {
            // No luminance carries the detail: the colour channels carry it, at the detail strength.
            var denoised = await DenoisedColourAsync(colourParts.Starless, options.DetailDenoise);
            final = [NarrowbandCombination.WithStars(denoised[0], starsOnly[0]), NarrowbandCombination.WithStars(denoised[1], starsOnly[1]),
                NarrowbandCombination.WithStars(denoised[2], starsOnly[2])];
        }
        return new Result(final[0], final[1], final[2], luminanceOut);

        // Red (with the H-alpha's emission), green and blue from planes on one grid: with Starless the emission goes into the
        // starless red and each channel's own stars back on top. Starless is the three channels before the stars go back
        // (the full ones without Starless); Stars is null without it.
        async Task<(Image[] Full, Image[] Starless, Image[]? Stars)> ComposeAsync(IReadOnlyList<Image> source, bool report)
        {
            IReadOnlyList<Image> split = source;
            Image[]? layerStars = null;
            if (options.Starless && roles.StarRemover is { } remover)
            {
                say($"removing stars from {source.Count} masters ({remover.Name})");
                var (starless, stars) = await NarrowbandCombination.SplitStarsAsync(source, remover, cancellationToken);
                layerStars = stars;
                split = starless;
            }

            var withLine = split[0];
            if (hasLine)
            {
                (withLine, var weight) = NarrowbandCombination.AddLineFrom(split[0], split[3], k, options.LineWeight);
                if (report)
                {
                    say(Say($"H-alpha continuum k {k:G5}; its emission added to red at {weight:G4} (weight {options.LineWeight:G3} x the line's worth in red: exposure ratio and fitted scales)"));
                }
            }
            Image[] channels = [withLine, split[1], split[2]];
            Image[] full = layerStars is null
                ? channels
                : [NarrowbandCombination.WithStars(channels[0], layerStars[0]), NarrowbandCombination.WithStars(channels[1], layerStars[1]),
                    NarrowbandCombination.WithStars(channels[2], layerStars[2])];
            return (full, channels, layerStars);
        }

        // A channel with the luminance's detail, at the scale measured between the two (LuminanceDetail.Transfer).
        Image Detail(Image channel, Image luminance, int index)
        {
            var (detailed, scale) = LuminanceDetail.Transfer(channel, luminance, options.ColourSigma);
            say(Say($"{Name(index)} takes the luminance's detail at 1/{scale:G4}"));
            return detailed;
        }

        // A starless plane denoised, or handed back as it was where no denoiser serves a mono plane (the in-house one is
        // colour only), said either way with its noise over blocks before and after.
        async Task<Image> DenoisedAsync(Image starless, EnhanceOptions denoise, string what)
        {
            if (!IEnhancerAvailability.Serves(roles.Denoiser, 1, denoise))
            {
                say($"--denoise: no denoiser serves a mono plane here; the {what} is left as it is.");
                return starless;
            }
            var denoised = (await NarrowbandCombination.DenoiseAsync([starless], roles.Denoiser, denoise, cancellationToken))[0];
            say(Say($"denoised the starless {what} ({roles.Denoiser.Name}, strength {DescribeStrength(denoise)}): noise {SyntheticLuminance.BlockNoise(starless):G4} -> {SyntheticLuminance.BlockNoise(denoised):G4} over {SyntheticLuminance.NoiseBlockPx} px blocks"));
            return denoised;
        }

        // The starless red, green and blue denoised together as one colour image.
        async Task<Image[]> DenoisedColourAsync(Image[] starless, EnhanceOptions denoise)
        {
            if (!IEnhancerAvailability.Serves(roles.Denoiser, 3, denoise))
            {
                say("--denoise: no denoiser serves a colour image here; the colour is left as it is.");
                return starless;
            }
            var denoised = await NarrowbandCombination.DenoiseColourAsync(starless[0], starless[1], starless[2], roles.Denoiser, denoise, cancellationToken);
            for (var i = 0; i < 3; i++)
            {
                say(Say($"denoised the starless {Name(i)} ({roles.Denoiser.Name}, strength {DescribeStrength(denoise)}): noise {SyntheticLuminance.BlockNoise(starless[i]):G4} -> {SyntheticLuminance.BlockNoise(denoised[i]):G4}"));
            }
            return denoised;
        }
    }

    /// <summary>A denoise's strength as said: its number, or the denoiser's own.</summary>
    public static string DescribeStrength(EnhanceOptions options)
        => options.Tuning?.DenoiseStrength is { } s ? s.ToString("G3", CultureInfo.InvariantCulture) : "the denoiser's own";

    private static string Name(int index) => index switch
    {
        0 => "red",
        1 => "green",
        2 => "blue",
        _ => "H-alpha",
    };

    private static string Say(FormattableString message) => message.ToString(CultureInfo.InvariantCulture);
}
