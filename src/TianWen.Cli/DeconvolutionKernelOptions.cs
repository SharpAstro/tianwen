using System.CommandLine;
using System.Diagnostics.CodeAnalysis;
using TianWen.Lib.Imaging.Enhancement;

namespace TianWen.Cli;

/// <summary>
/// The three options that state the blur TianWen's own deconvolver removes (<see cref="DeconvolutionKernel"/>), one set
/// for <c>image sharpen</c> and <c>stack --enhance</c>, read through the one parser the hosted enhance endpoint uses.
/// </summary>
internal sealed class DeconvolutionKernelOptions
{
    public Option<string?> Kernel { get; } = new("--deconv-kernel")
    {
        Description = "The blur TianWen's own deconvolver removes, as a Moffat FWHM in the frame's native pixels: one value for every " +
                      "channel or three (red,green,blue), e.g. 0.77,0.91,0.98. It is the blur's EXCESS over the sharp frame, never the " +
                      "stars' width. The deconvolver serves only with --ai-backend tianwen and this option, since nothing yet finds a " +
                      "kernel in a single frame (#741).",
    };

    public Option<double?> Beta { get; } = new("--deconv-beta")
    {
        Description = $"The Moffat beta of --deconv-kernel; default {DeconvolutionKernel.DefaultBeta}.",
    };

    public Option<double?> Resample { get; } = new("--deconv-resample")
    {
        Description = $"The scale the frame is deconvolved at and brought back from (cubic spline either way, the kernel scaled with " +
                      $"it), at least 1; default {DeconvolutionKernel.DefaultResample}, the operating point E3.4d was measured at.",
    };

    public void AddTo(Command command)
    {
        command.Options.Add(Kernel);
        command.Options.Add(Beta);
        command.Options.Add(Resample);
    }

    public bool TryRead(ParseResult parseResult, out DeconvolutionKernel? kernel, [NotNullWhen(false)] out string? error)
        => DeconvolutionKernel.TryParse(parseResult.GetValue(Kernel), parseResult.GetValue(Beta), parseResult.GetValue(Resample), out kernel, out error);
}
