using System.Collections.Immutable;
using System.Linq;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Enhancement;

namespace TianWen.UI.Abstractions;

/// <summary>One backend the Enhance popover offers, and the program it would run on the frame on show.</summary>
/// <param name="Backend">The backend a run with this choice is started with.</param>
/// <param name="Label">What the choice reads as.</param>
/// <param name="Steps">The canonical program's steps, in order, by their display names.</param>
public sealed record EnhanceChoice(EnhanceBackend Backend, string Label, ImmutableArray<string> Steps);

/// <summary>
/// Which backends serve a frame, asked of the pipeline as an enhance asks it (<see cref="SharpenPipeline.CapabilitiesFor"/>,
/// <see cref="IEnhancerAvailability.Serves"/>), never by whether a role is registered.
/// </summary>
public static class EnhanceChoices
{
    /// <summary>
    /// Auto always (it is what an enhance with no choice runs). RC-Astro only where forcing it changes what serves, that is
    /// where the binary is present but its licence check says no: licensed, Auto already is RC-Astro. TianWen's own models
    /// where one of the roles it owns (the denoise and the whole-frame deblur) serves under it.
    /// </summary>
    /// <remarks>Asks the roles, which may probe a licence (a process), so it runs off the render thread.</remarks>
    public static ImmutableArray<EnhanceChoice> For(SharpenPipeline pipeline, Image input)
    {
        var auto = pipeline.CapabilitiesFor(input, new EnhanceOptions(EnhanceBackend.Auto));
        var rc = pipeline.CapabilitiesFor(input, new EnhanceOptions(EnhanceBackend.ForceRcAstro));
        var tianwen = pipeline.CapabilitiesFor(input, new EnhanceOptions(EnhanceBackend.TianWen));

        var choices = ImmutableArray.CreateBuilder<EnhanceChoice>(3);
        choices.Add(Choice(EnhanceBackend.Auto, "Auto", auto));
        if (rc != auto)
        {
            choices.Add(Choice(EnhanceBackend.ForceRcAstro, "RC-Astro", rc));
        }
        if (tianwen.Denoise || tianwen.Deblur)
        {
            choices.Add(Choice(EnhanceBackend.TianWen, "TianWen", tianwen));
        }
        return choices.ToImmutable();
    }

    private static EnhanceChoice Choice(EnhanceBackend backend, string label, EnhanceCapabilities capabilities)
        => new(backend, label, [.. LinearEnhanceProgram.For(capabilities).ToSteps().Select(SharpenPipeline.StepDisplayName)]);
}
