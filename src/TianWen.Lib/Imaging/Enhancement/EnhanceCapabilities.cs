using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace TianWen.Lib.Imaging.Enhancement;

/// <summary>
/// An enhancer that can say, at ENHANCE time, whether it will serve an input: a deferred RC-Astro role
/// answers by its licence, the in-house denoiser by the channel count and whether its weights are on
/// disk, a passthrough never. An enhancer that does not implement this serves whatever it is handed.
/// </summary>
/// <remarks>
/// Asked by <see cref="SharpenPipeline.CapabilitiesFor"/> right before a program is built, which is
/// the first enhance, so a deferred role's licence probe still never runs at DI build. It takes the
/// CHANNEL COUNT rather than the image because the pipeline debayers a 1-channel mosaic before any step
/// sees it: the question is what the role will be handed, not what the caller holds.
/// </remarks>
public interface IEnhancerAvailability
{
    /// <summary>Whether this enhancer will serve an input of <paramref name="channelCount"/> channels
    /// under <paramref name="options"/>.</summary>
    bool CanServe(int channelCount, EnhanceOptions options);

    /// <summary>
    /// Why the backend this role prefers under <paramref name="options"/> did not serve, in words for a status
    /// line ("RC-Astro StarXTerminator (sxt): its licence check exited 77"), or null when it served or nothing
    /// was refused. A role that fell back to another backend says so here too, since the fallback changes what
    /// an enhance does (no star split without StarXTerminator) while every step still runs.
    /// </summary>
    string? Refusal(EnhanceOptions options) => null;

    /// <summary>
    /// The name of what would serve under <paramref name="options"/>, where that follows from them: a deferred
    /// RC-Astro role is RC-Astro under one backend and the in-house model under another, and an enhance step's key
    /// names the one its own settings would run (#2475). Null for an enhancer that is one thing whatever the options,
    /// whose <see cref="IImageEnhancer.Name"/> says it.
    /// </summary>
    string? NameUnder(EnhanceOptions options) => null;

    /// <summary>
    /// Whose model would serve under <paramref name="options"/>, where the enhancer can say: RC-Astro's or TianWen's
    /// own, with the version of the tool that runs it. Null for an enhancer that is neither or cannot say (a classical
    /// routine, a stand-in), and where nothing would serve. Said beside a result (<see cref="EnhancerOrigin"/>); a
    /// step's key holds the name, not this.
    /// </summary>
    EnhancerOrigin? OriginUnder(EnhanceOptions options) => null;

    /// <summary>
    /// <see cref="NameUnder"/> and <see cref="OriginUnder"/> from ONE resolution, or null for an enhancer that is one
    /// thing whatever the options. A deferred role resolves its backend by asking whether RC-Astro's product is
    /// licensed, which can cost a subprocess and can answer differently a moment later, so a caller that wants both
    /// asks this once: the name and the origin it gets then describe the same backend.
    /// </summary>
    EnhancerAnswer? AnswerUnder(EnhanceOptions options) => null;

    /// <summary>
    /// Whether <paramref name="role"/> is registered AND serves an input of <paramref name="channelCount"/>
    /// channels under <paramref name="options"/>: the ONE rule every caller asks. A registration is not
    /// an answer, because a deferred RC-Astro role is registered on every host and serves only where the
    /// product is installed and licensed; asking whether it is null reads it as present on a machine
    /// where its first use throws.
    /// </summary>
    public static bool Serves([NotNullWhen(true)] IImageEnhancer? role, int channelCount, EnhanceOptions options)
        => role is not null && (role is not IEnhancerAvailability availability || availability.CanServe(channelCount, options));

    /// <summary>
    /// <see cref="Serves"/> for a gate asked before the input exists (a CLI flag, a group about to be
    /// read): whether <paramref name="role"/> serves a mono or a colour input, the two shapes a role is
    /// handed. A role that serves only one of them still fails clearly at the input it declines.
    /// </summary>
    public static bool ServesAny([NotNullWhen(true)] IImageEnhancer? role, EnhanceOptions options)
        => Serves(role, 1, options) || Serves(role, 3, options);

    /// <summary>
    /// <see cref="Serves"/> for a rule that must hold whatever the input turns out to be: whether
    /// <paramref name="role"/> serves a mono AND a colour input, the two shapes a role is handed. What follows from a
    /// role being live on every image can then be said before the image is read (an enhance step's key, #2475).
    /// </summary>
    public static bool ServesEvery([NotNullWhen(true)] IImageEnhancer? role, EnhanceOptions options)
        => Serves(role, 1, options) && Serves(role, 3, options);

    /// <summary>
    /// The one word for whose model <paramref name="role"/> is under <paramref name="options"/>
    /// (<see cref="EnhancerOrigin.TagOf"/>: rc or tw), for a step one enhancer serves. Empty for no enhancer and for
    /// one that does not say.
    /// </summary>
    public static string TagOf(IImageEnhancer? role, EnhanceOptions options)
        => EnhancerOrigin.TagOf([(role as IEnhancerAvailability)?.OriginUnder(options)]);

    /// <summary>
    /// A one-enhancer step's name (as its key holds it) and its origin word from ONE resolution
    /// (<see cref="AnswerUnder"/>): a deferred RC-Astro role costs a licence probe to resolve and a refusal can flip
    /// between two asks, so the key text and the word never come from two. Empty name and word for no enhancer.
    /// </summary>
    public static (string Name, string Tag) ServedOf(IImageEnhancer? role, EnhanceOptions options)
    {
        if (role is null)
        {
            return (string.Empty, string.Empty);
        }

        if (role is IEnhancerAvailability availability && availability.AnswerUnder(options) is { } answer)
        {
            return (answer.Name, EnhancerOrigin.TagOf([answer.Origin]));
        }

        return (role.Name, TagOf(role, options));
    }
}

/// <summary>What one resolution of a role answers (<see cref="IEnhancerAvailability.AnswerUnder"/>): the name of what
/// would serve, and whose model it is.</summary>
/// <param name="Name">The enhancer's name as a step's key holds it.</param>
/// <param name="Origin">Whose model it is, or null where it does not say.</param>
public sealed record EnhancerAnswer(string Name, EnhancerOrigin? Origin);

/// <summary>
/// Whose model serves a role of an enhance, for a person reading a result: the short word a line and a file name can
/// carry, and the version of the external tool that runs it where one does.
/// </summary>
/// <param name="Family"><see cref="RcAstro"/> or <see cref="TianWen"/>.</param>
/// <param name="ToolVersion">The version of the tool that runs the model (RC-Astro's CLI, "2.6.11"); null for a model
/// run in process, and where the tool does not say. It is no part of a role's name or of a step's key: two CLI versions
/// that run one model gave the same pixels to rounding.</param>
public sealed record EnhancerOrigin(string Family, string? ToolVersion = null)
{
    /// <summary>An RC-Astro product (BlurXTerminator, StarXTerminator, NoiseXTerminator).</summary>
    public const string RcAstro = "rc";

    /// <summary>One of TianWen's own models.</summary>
    public const string TianWen = "tw";

    /// <summary>Roles of one enhance served by both: what <see cref="TagOf"/> answers then.</summary>
    public const string Mixed = "mix";

    /// <summary>
    /// The one word for a set of roles: <see cref="RcAstro"/> where every role that says whose it is says RC-Astro,
    /// <see cref="TianWen"/> where every one says TianWen, <see cref="Mixed"/> where both are among them, and empty
    /// where none says (a gradient correction alone, stand-ins). A role with no origin, the gradient corrector among
    /// them, does not count either way.
    /// </summary>
    public static string TagOf(IEnumerable<EnhancerOrigin?> origins)
    {
        var (rc, tw) = (false, false);
        foreach (var origin in origins)
        {
            rc |= origin?.Family is RcAstro;
            tw |= origin?.Family is TianWen;
        }

        return rc && tw ? Mixed : rc ? RcAstro : tw ? TianWen : string.Empty;
    }
}

/// <summary>One role of an enhance as a program asks it: its word in a line ("deblur", "stars"), the name of what
/// serves it ("left out" where no step asks it, "none" where nothing is registered), and whose model that is.</summary>
public readonly record struct EnhanceRole(string Role, string Served, EnhancerOrigin? Origin)
{
    /// <summary>What <see cref="Served"/> says of a role no step of the program asks.</summary>
    public const string LeftOut = "left out";

    /// <summary>True when a step of the program asks this role.</summary>
    public bool Asked => !string.Equals(Served, LeftOut, StringComparison.Ordinal);
}

/// <summary>
/// Which roles can serve one input under one set of options, answered by
/// <see cref="SharpenPipeline.CapabilitiesFor"/>. The canonical program is built from it
/// (<see cref="LinearEnhanceProgram.For(EnhanceCapabilities)"/>), so no program asks for a role that
/// nothing serves: without a star remover the program is whole-frame, and a role gains a backend (an
/// in-house model, an RC-Astro licence) by registration alone, with no program edited.
/// </summary>
/// <param name="Deblur">A whole-frame deblurrer (RC-Astro BlurXTerminator, licensed).</param>
/// <param name="GradientCorrection">A gradient corrector (GraXpert, else the classical fit, so in
/// practice always).</param>
/// <param name="StarRemoval">A star remover (RC-Astro StarXTerminator today).</param>
/// <param name="StellarSharpen">A stellar sharpener (none today).</param>
/// <param name="Deconvolve">A starless-plate deconvolver (RC-Astro today; the in-house one at E7).</param>
/// <param name="Denoise">A denoiser for this input (RC-Astro NoiseXTerminator, else the in-house N2N
/// model for 3-channel input).</param>
public readonly record struct EnhanceCapabilities(
    bool Deblur,
    bool GradientCorrection,
    bool StarRemoval,
    bool StellarSharpen,
    bool Deconvolve,
    bool Denoise)
{
    /// <summary>Every role present: the split program's two named shapes,
    /// <see cref="SharpenRequest.Canonical"/> (no deblur) and <see cref="SharpenRequest.DeblurFirst"/>.</summary>
    public static EnhanceCapabilities AllRoles(bool deblur) => new(deblur, true, true, true, true, true);
}
