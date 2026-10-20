using System.CommandLine;
using TianWen.AI.Imaging;
using TianWen.Lib.Imaging.Enhancement;

namespace TianWen.Cli;

/// <summary>
/// The one <c>--gradient</c> option <c>image sharpen</c> and <c>stack --enhance</c> share, read into
/// <see cref="EnhanceOptions.Gradient"/>. <c>image flatten</c> names the same choice <c>--backend</c>, since choosing
/// the extractor is all that verb does.
/// </summary>
internal static class GradientBackendOption
{
    public static Option<GradientBackend> Create(string implies) => new("--gradient")
    {
        Description = "The background extractor the gradient step runs: 'auto' (default) is GraXpert's BGE model when its weights are " +
                      "installed (read from GraXpert's own cache), the classical robust fit otherwise; 'classical' always the classical fit, " +
                      "the one for a frame a single galaxy fills (GraXpert's model takes part of its halo as background and leaves a dark " +
                      "ring); 'graxpert' always GraXpert, failing with the paths searched where its weights are not installed." + implies,
        DefaultValueFactory = _ => GradientBackend.Auto,
    };

    /// <summary>The extractor a run asking for <paramref name="backend"/> gets, by name, for the run's own log line.</summary>
    public static string NameFor(IGradientCorrector corrector, GradientBackend backend)
        => corrector is FallbackGradientCorrector fallback ? fallback.For(backend).Name : corrector.Name;
}
