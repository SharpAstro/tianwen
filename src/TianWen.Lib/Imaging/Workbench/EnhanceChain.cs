using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text.Json.Nodes;
using TianWen.Lib.Imaging.Enhancement;

namespace TianWen.Lib.Imaging.Workbench;

/// <summary>
/// The enhance laid out as a chain of the single steps (the enhance retrofit, 2026-11-26): the project images whose last
/// result is the one-step enhance's, each step kept in the store on its own, so changing one setting runs that step and the
/// ones after it, not the whole enhance.
/// </summary>
/// <remarks>
/// <para><b>Read off the enhance's own program</b> (<see cref="EnhanceOperation.ProgramAsync"/> over
/// <see cref="LinearEnhanceProgram"/>, the one place the step order is written): each of its steps becomes the single step
/// that runs the same routine, and nothing here lists steps of its own.</para>
/// <list type="bullet">
/// <item><c>&lt;name&gt;-starless</c> starts where the enhance starts and runs its plate steps: white-balance, deblur,
/// gradient, remove-stars and denoise, those the program has.</item>
/// <item><c>&lt;name&gt;-stars</c> starts from the stars that remove-stars took out and runs SCNR, where the program does.</item>
/// <item><c>&lt;name&gt;</c> starts from the starless plate and puts the stars back (add-stars): the enhance's result.</item>
/// </list>
/// <para>The whole-frame program (no star remover) is one image, <c>&lt;name&gt;</c>, whose edits are its steps.</para>
/// <para>A program or a setting no single step can follow is refused in words, never laid out as something else: the
/// stellar sharpen and the starless deconvolution have no single step, nor do the screen split, a blend under one, a stated
/// backend or the deblurrer's and denoiser's own tunings beyond the denoise strength.</para>
/// </remarks>
public static class EnhanceChain
{
    /// <summary>The name of the image that runs the plate steps, for a chain called <paramref name="name"/>.</summary>
    public static string StarlessOf(string name) => name + "-starless";

    /// <summary>The name of the image that works on the stars, for a chain called <paramref name="name"/>.</summary>
    public static string StarsOf(string name) => name + "-stars";

    /// <summary>
    /// The images of the chain called <paramref name="name"/> for <paramref name="program"/> (made for
    /// <paramref name="source"/> under <paramref name="settings"/>), starting from <paramref name="start"/>'s file or image;
    /// or why there is none.
    /// </summary>
    /// <param name="program">The enhance's program for this input and these settings (<see cref="EnhanceOperation.ProgramAsync"/>).</param>
    /// <param name="settings">The enhance's settings the program was made under.</param>
    /// <param name="source">The input the enhance starts from: whether it is colour and how its colour is balanced decide
    /// whether SCNR runs, as they decide it in the enhance.</param>
    /// <param name="name">What the chain's result is called.</param>
    /// <param name="start">Where the chain starts: <see cref="ProjectImage.Path"/>, or <see cref="ProjectImage.From"/> and
    /// <see cref="ProjectImage.Result"/>; its name and edits are not read.</param>
    public static (ImmutableArray<ProjectImage> Images, string? Refusal) ImagesFor(
        LinearEnhanceProgram program, EnhanceSettings settings, Image source, string name, ProjectImage start)
    {
        if (Unfollowable(settings) is { } setting)
        {
            return ([], $"no single step takes the enhance's {setting}");
        }

        var plate = new List<ProjectEdit>();
        var stars = new List<ProjectEdit>();
        var split = false;
        var recombine = false;
        foreach (var step in program.ToSteps())
        {
            switch (step)
            {
                case WhiteBalanceStep:
                    plate.Add(Edit(WhiteBalanceOperation.Id, settings.WhiteBalance.IsDefaultOrEmpty ? null : new JsonObject
                    {
                        ["whiteBalance"] = new JsonArray([.. settings.WhiteBalance.Select(static gain => JsonValue.Create(gain))]),
                    }));
                    break;
                case DeblurStep { Blend: 1f }:
                    plate.Add(Edit(DeblurOperation.Id));
                    break;
                case GradientCorrectionStep:
                    plate.Add(Edit(GradientOperation.Id, settings.GradientBackend is GradientBackend.Auto
                        ? null
                        : new JsonObject { ["backend"] = settings.GradientBackend.ToString().ToLowerInvariant() }));
                    break;
                case RemoveStarsStep { SplitMode: RecombineMode.Additive }:
                    // The enhance's split has no plate solution to ask a catalogue with, so its cores keep their stars.
                    plate.Add(Edit(RemoveStarsOperation.Id, new JsonObject { ["catalogue"] = "none" }));
                    split = true;
                    break;
                case DenoiseStarlessStep { Blend: 1f, Variant: DenoiseVariant.Default } or DenoiseFrameStep { Blend: 1f, Variant: DenoiseVariant.Default }:
                    plate.Add(Edit(DenoiseOperation.Id, settings.DenoiseStrength is { } strength ? new JsonObject { ["strength"] = strength } : null));
                    break;
                case ScnrStarsStep scnr:
                    // The enhance runs SCNR only on colour balanced in its pixels, and skips it otherwise; the step refuses
                    // such a plate, so the chain leaves it out where the enhance would skip it.
                    var balanced = program.WhiteBalance is not null || Image.ScnrRefusal(source.ImageMeta) is null;
                    if (source.ChannelCount >= 3 && balanced)
                    {
                        stars.Add(Edit(ScnrOperation.Id, new JsonObject
                        {
                            ["mode"] = scnr.Mode.ToString().ToLowerInvariant(),
                            ["amount"] = scnr.Amount,
                        }));
                    }
                    break;
                case RecombineStep { Mode: RecombineMode.Additive }:
                    recombine = true;
                    break;
                default:
                    return ([], $"the enhance's {step.GetType().Name} has no single step that runs it as the enhance does");
            }
        }

        if (!split)
        {
            return ([Starting(start, name, plate)], null);
        }

        if (!recombine)
        {
            return ([], "the enhance's program does not put the stars back, so it has no one result to lay out");
        }

        var starless = Starting(start, StarlessOf(name), plate);
        var starsImage = new ProjectImage { Name = StarsOf(name), From = starless.Name, Result = RemoveStarsOperation.Stars, Edits = [.. stars] };
        var result = new ProjectImage { Name = name, From = starless.Name, Edits = [Edit(AddStarsOperation.Id, null, starsImage.Name)] };
        return ([starless, starsImage, result], null);
    }

    // The enhance's settings a single step has no setting for, by name; null when every one is at what the steps run.
    private static string? Unfollowable(EnhanceSettings settings) => settings switch
    {
        { Backend: not EnhanceBackend.Auto } => "backend",
        { Mode: not RecombineMode.Additive } => "mode (the screen split)",
        { DeblurSharpen: not null } => "deblurSharpen",
        { DenoiseIterations: not null } => "denoiseIterations",
        { DenoiseVariant: not DenoiseVariant.Default } => "denoiseVariant",
        { DenoiseBlend: not 1f } => "denoiseBlend",
        { DeconvKernel: not null } => "deconvKernel",
        _ => null,
    };

    private static ProjectImage Starting(ProjectImage start, string name, List<ProjectEdit> edits)
        => new() { Name = name, Path = start.Path, From = start.From, Result = start.Result, Edits = [.. edits] };

    private static ProjectEdit Edit(string operation, JsonObject? settings = null, params string[] inputs)
        => new() { Operation = operation, Settings = settings, Inputs = [.. inputs] };
}
