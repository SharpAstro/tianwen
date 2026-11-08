using System.CommandLine;
using System.Globalization;
using System.IO;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Stacking;

namespace TianWen.Cli;

internal sealed partial class ImageSubCommand
{
    // -------- tianwen image channel-offsets ----------------------------

    /// <summary>
    /// Where red and blue sit against green on one frame's stars (<see cref="Image.ChannelOffsets"/>, #1993): the same reading the
    /// stacker's measure pass takes, with the stars found as it finds them (its own detection defaults), so the number printed is
    /// the one a stack would have aligned that frame by. Nothing but the call lives here.
    /// </summary>
    private Command BuildChannelOffsetsCommand()
    {
        var inputArg = new Argument<string>("input")
        {
            Description = "A frame to read: a raw Bayer mosaic (read by photosite colour) or a debayered three-plane image. "
                + "A debayered image reads the offsets its demosaic left, which are smaller than the mosaic's.",
        };
        var cmd = new Command("channel-offsets", "Read each colour's centroid offset from green on a frame's stars, as the stacker's colour alignment does: "
            + "red and blue against green in pixels, with the star count and each colour's scatter.")
        {
            Arguments = { inputArg },
        };
        cmd.SetAction(async (parseResult, ct) =>
        {
            var input = parseResult.Required(inputArg);
            if (!File.Exists(input))
            {
                consoleHost.WriteError($"Input not found: {input}");
                return 1;
            }
            if (!Image.TryReadFitsFile(input, out var image, out _))
            {
                consoleHost.WriteError($"Failed to read FITS file: {input}");
                return 1;
            }

            var defaults = new StackingOptions(string.Empty, string.Empty);
            var (stars, debayered) = await FrameRegistration.DetectAsync(
                image, defaults.CentroidDebayerAlg, defaults.SnrMin, defaults.MinStars, cancellationToken: ct);
            var reading = image.ChannelOffsets(stars);
            if (!ReferenceEquals(debayered, image))
            {
                debayered.Release();
            }
            image.Release();

            if (!reading.IsMeasured)
            {
                consoleHost.WriteError($"[channel-offsets] {Path.GetFileName(input)}: not measured ({stars.Count} stars offered; "
                    + "a colour needs enough bright, uncrowded, unsaturated stars in all three colours, or the image is not colour)");
                return 1;
            }

            consoleHost.WriteScrollable(string.Create(CultureInfo.InvariantCulture,
                $"[channel-offsets] {Path.GetFileName(input)}: {reading.StarsUsed} of {reading.StarsOffered} stars, offsets from {reading.Reference}"));
            foreach (var (name, offset) in new[] { ("red", reading.Red), ("green", reading.Green), ("blue", reading.Blue) })
            {
                consoleHost.WriteScrollable(string.Create(CultureInfo.InvariantCulture,
                    $"  {name,-5} ({offset.Dx:+0.000;-0.000;+0.000}, {offset.Dy:+0.000;-0.000;+0.000}) px, "
                    + $"length {offset.Length:F3} px, spread ({offset.SpreadX:F3}, {offset.SpreadY:F3}) px, error ({offset.ErrorX:F3}, {offset.ErrorY:F3}) px"));
            }
            return 0;
        });
        return cmd;
    }
}
