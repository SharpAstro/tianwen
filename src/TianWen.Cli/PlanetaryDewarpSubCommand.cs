using System;
using System.CommandLine;
using System.Globalization;
using System.IO;
using System.Linq;
using TianWen.Lib.Imaging.Planetary;
using SharpAstro.Ser;
using Console.Lib;

namespace TianWen.Cli;

/// <summary>
/// <c>planetary-dewarp</c> (docs/plans/planetary-restoration.md, R5 part 2): how much of a synthetic capture's true warp the
/// alignment points recover. Every frame's points are read as the stacker reads them and compared with the warp
/// <c>planetary-degrade</c> applied, on the reference frame's geometry and on the median geometry, each frame's own reading and
/// pooled over the frames either side.
/// </summary>
internal sealed class PlanetaryDewarpSubCommand(IConsoleHost consoleHost)
{
    public Command Build()
    {
        var captureArg = new Argument<string>("capture") { Description = "A synthetic capture planetary-degrade made with a warp." };
        var warpOpt = new Option<string?>("--warp") { Description = "Its true warp; by default the .warp beside it." };
        var framesOpt = new Option<int?>("--frames") { Description = "Only the capture's first frames." };
        var spacingOpt = new Option<int>("--ap-spacing") { Description = "The alignment points' spacing.", DefaultValueFactory = _ => 24 };
        var patchOpt = new Option<int>("--ap-patch") { Description = "The alignment points' patch, a power of two.", DefaultValueFactory = _ => 32 };
        var correlationOpt = new Option<string>("--correlation") { Description = "whitened (phase correlation) or plain (cross-correlation).", DefaultValueFactory = _ => "plain" };
        var poolOpt = new Option<string>("--pool") { Description = "The frames either side each point's warp is pooled over (a Gaussian's sigma), a comma list.", DefaultValueFactory = _ => "0,1,2,4" };

        var command = new Command("planetary-dewarp",
            "How much of a synthetic capture's true warp the alignment points recover, on the reference and the median geometry, each frame's own and pooled (R5).")
        {
            Arguments = { captureArg },
            Options = { warpOpt, framesOpt, spacingOpt, patchOpt, correlationOpt, poolOpt },
        };

        command.SetAction(async (parseResult, ct) =>
        {
            var input = parseResult.GetValue(captureArg) ?? "";
            var warpPath = parseResult.GetValue(warpOpt) ?? SyntheticWarpFile.PathFor(input);
            if (!File.Exists(warpPath) || SyntheticWarpFile.Read(warpPath) is not { } truth)
            {
                consoleHost.WriteError($"{warpPath}: no warp file (planetary-degrade writes one beside a capture made with --warp-rms)");
                return 1;
            }
            using var reader = SerReader.Open(input);
            using var whole = new SerFrameStream(reader, ownsReader: false);
            var frames = Math.Min(Math.Min(whole.FrameCount, truth.Length), parseResult.GetValue(framesOpt) ?? whole.FrameCount);
            using var stream = new PlanetaryFrameWindow(whole, 0, frames);
            var correlation = parseResult.GetValue(correlationOpt)?.ToLowerInvariant();
            if (correlation is not ("plain" or "whitened"))
            {
                consoleHost.WriteError($"--correlation {correlation}: plain or whitened");
                return 1;
            }
            var options = new PlanetaryStackOptions
            {
                AlignmentPointSpacing = parseResult.GetValue(spacingOpt),
                AlignmentPatchSize = parseResult.GetValue(patchOpt),
                WhitenedCorrelation = correlation == "whitened",
            };
            var pools = (parseResult.GetValue(poolOpt) ?? "0").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(p => double.Parse(p, CultureInfo.InvariantCulture)).ToArray();

            var report = await DewarpResidual.MeasureAsync(stream, truth, options, pools, ct);
            var inv = CultureInfo.InvariantCulture;
            consoleHost.WriteScrollable(string.Create(inv,
                $"{Path.GetFileName(input)}: {report.Frames} frames, {report.Points} points ({options.AlignmentPatchSize} px patches {options.AlignmentPointSpacing} px apart, {correlation}), the reference frame {report.ReferenceIndex}"));
            consoleHost.WriteScrollable(string.Create(inv,
                $"    the true warp left undewarped, px RMS a axis: {report.UndewarpedX:0.000}, {report.UndewarpedY:0.000} on the reference geometry; {report.UndewarpedMedianX:0.000}, {report.UndewarpedMedianY:0.000} on the median geometry"));
            foreach (var r in report.Readings)
            {
                var (baseX, baseY) = r.MedianGeometry ? (report.UndewarpedMedianX, report.UndewarpedMedianY) : (report.UndewarpedX, report.UndewarpedY);
                var removed = 1 - Math.Sqrt(((r.ResidualX * r.ResidualX) + (r.ResidualY * r.ResidualY)) / ((baseX * baseX) + (baseY * baseY)));
                consoleHost.WriteScrollable(string.Create(inv,
                    $"    {(r.MedianGeometry ? "median" : "reference")} geometry, {(r.PoolFrames > 0 ? $"pooled over {r.PoolFrames:0.#} frames" : "each frame's own")}: left {r.ResidualX:0.000}, {r.ResidualY:0.000} ({removed:+0%;-0%} of the warp taken out)"));
            }
            return 0;
        });
        return command;
    }
}
