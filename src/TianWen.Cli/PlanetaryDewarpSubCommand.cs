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
/// <c>planetary dewarp</c> (docs/plans/planetary-restoration.md, R5 part 2): how much of a synthetic capture's true warp the
/// alignment points recover. Every frame's points are read as the stacker reads them and compared with the warp
/// <c>planetary degrade</c> applied, on the reference frame's geometry and on the median geometry, each frame's own reading and
/// pooled over the frames either side.
/// </summary>
internal sealed class PlanetaryDewarpSubCommand(IConsoleHost consoleHost)
{
    internal static PlanetaryPointEstimator ParseEstimator(string? name) => name?.ToLowerInvariant() switch
    {
        "sdf" or "square-difference" => PlanetaryPointEstimator.SquareDifference,
        "weighted" => PlanetaryPointEstimator.WeightedCorrelation,
        "correlation" or null => PlanetaryPointEstimator.Correlation,
        _ => throw new ArgumentException($"--estimator {name}: correlation, weighted or sdf"),
    };

    public Command Build()
    {
        var captureArg = new Argument<string>("capture") { Description = "A synthetic capture planetary degrade made with a warp." };
        var warpOpt = new Option<string?>("--warp") { Description = "Its true warp; by default the .warp beside it." };
        var framesOpt = new Option<int?>("--frames") { Description = "Only the capture's first frames." };
        var spacingOpt = new Option<int>("--ap-spacing") { Description = "The alignment points' spacing.", DefaultValueFactory = _ => 24 };
        var patchOpt = new Option<int>("--ap-patch") { Description = "The alignment points' patch, a power of two.", DefaultValueFactory = _ => 32 };
        var maxApOpt = new Option<int>("--max-ap") { Description = "The most alignment points (the stack's default, 64, caps a dense grid).", DefaultValueFactory = _ => new PlanetaryStackOptions().MaxAlignmentPoints };
        var correlationOpt = new Option<string>("--correlation") { Description = "whitened (phase correlation) or plain (cross-correlation).", DefaultValueFactory = _ => "plain" };
        var meshOpt = new Option<float>("--mesh-spacing") { Description = "The displacement mesh's node spacing, px.", DefaultValueFactory = _ => 24f };
        var influenceOpt = new Option<float>("--mesh-influence") { Description = "How far a point's displacement reaches into the mesh, px.", DefaultValueFactory = _ => 48f };
        var krigeRmsOpt = new Option<double?>("--krige-rms") { Description = "The twin's warp RMS a axis, px (planetary degrade --warp-rms): with --krige-length, how each point reads the warp and what the blend, a kriging and the best linear weights leave (#1081)." };
        var krigeLengthOpt = new Option<double?>("--krige-length") { Description = "The twin's warp correlation length, px (planetary degrade --warp-length)." };
        var estimatorOpt = new Option<string>("--estimator") { Description = "How each point's shift is read: correlation (windowed, the default), weighted (the correlation by its maximum-likelihood weight) or sdf (square difference, #1082).", DefaultValueFactory = _ => "correlation" };
        var remeasureOpt = new Option<bool>("--remeasure") { Description = "Match every frame against the reference dewarped by its own points (#1081's second pass, PlanetaryStackOptions.RemeasureAgainstStack)." };
        var gainOpt = new Option<float>("--mesh-gain") { Description = "A gain on every point's residual before the mesh blends them (PlanetaryStackOptions.MeshGain).", DefaultValueFactory = _ => 1f };
        var poolOpt = new Option<string>("--pool") { Description = "The frames either side each point's warp is pooled over (a Gaussian's sigma), a comma list.", DefaultValueFactory = _ => "0,1,2,4" };

        var command = new Command("dewarp",
            "How much of a synthetic capture's true warp the alignment points recover, on the reference and the median geometry, each frame's own and pooled (R5).")
        {
            Arguments = { captureArg },
            Options = { warpOpt, framesOpt, spacingOpt, patchOpt, maxApOpt, correlationOpt, meshOpt, influenceOpt, poolOpt, krigeRmsOpt, krigeLengthOpt, gainOpt, remeasureOpt, estimatorOpt },
        };

        command.SetAction(async (parseResult, ct) =>
        {
            var input = parseResult.GetValue(captureArg) ?? "";
            var warpPath = parseResult.GetValue(warpOpt) ?? SyntheticWarpFile.PathFor(input);
            if (!File.Exists(warpPath) || SyntheticWarpFile.Read(warpPath) is not { } truth)
            {
                consoleHost.WriteError($"{warpPath}: no warp file (planetary degrade writes one beside a capture made with --warp-rms)");
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
                MaxAlignmentPoints = parseResult.GetValue(maxApOpt),
                WhitenedCorrelation = correlation == "whitened",
                MeshNodeSpacing = parseResult.GetValue(meshOpt),
                MeshInfluence = parseResult.GetValue(influenceOpt),
                MeshGain = parseResult.GetValue(gainOpt),
                RemeasureAgainstStack = parseResult.GetValue(remeasureOpt),
                PointEstimator = ParseEstimator(parseResult.GetValue(estimatorOpt)),
            };
            var pools = (parseResult.GetValue(poolOpt) ?? "0").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(p => double.Parse(p, CultureInfo.InvariantCulture)).ToArray();

            WarpModel? model = parseResult.GetValue(krigeRmsOpt) is { } rms && parseResult.GetValue(krigeLengthOpt) is { } length ? new WarpModel(rms, length) : null;
            var report = await DewarpResidual.MeasureAsync(stream, truth, options, pools, model, ct);
            var inv = CultureInfo.InvariantCulture;
            consoleHost.WriteScrollable(string.Create(inv,
                $"{Path.GetFileName(input)}: {report.Frames} frames, {report.Points} points ({options.AlignmentPatchSize} px patches {options.AlignmentPointSpacing} px apart, {correlation}), the reference frame {report.ReferenceIndex}"));
            consoleHost.WriteScrollable(string.Create(inv,
                $"    the true warp left undewarped, px RMS a axis: at the points {report.UndewarpedX:0.000}, {report.UndewarpedY:0.000} on the reference geometry, {report.UndewarpedMedianX:0.000}, {report.UndewarpedMedianY:0.000} on the median; over the disk ({report.MeshPlaces} places) {report.MeshUndewarpedX:0.000}, {report.MeshUndewarpedY:0.000} and {report.MeshUndewarpedMedianX:0.000}, {report.MeshUndewarpedMedianY:0.000}"));
            consoleHost.WriteScrollable(string.Create(inv, $"    left after the dewarp: the points' reading, and the mesh the stack applies ({options.MeshNodeSpacing:0.#} px nodes, {options.MeshInfluence:0.#} px influence)"));
            static double Removed(double x, double y, double baseX, double baseY) => 1 - Math.Sqrt(((x * x) + (y * y)) / ((baseX * baseX) + (baseY * baseY)));
            foreach (var r in report.Readings)
            {
                var (baseX, baseY) = r.MedianGeometry ? (report.UndewarpedMedianX, report.UndewarpedMedianY) : (report.UndewarpedX, report.UndewarpedY);
                var (meshX, meshY) = r.MedianGeometry ? (report.MeshUndewarpedMedianX, report.MeshUndewarpedMedianY) : (report.MeshUndewarpedX, report.MeshUndewarpedY);
                consoleHost.WriteScrollable(string.Create(inv,
                    $"    {(r.MedianGeometry ? "median" : "reference")} geometry, {(r.PoolFrames > 0 ? $"pooled over {r.PoolFrames:0.#} frames" : "each frame's own")}: points {r.ResidualX:0.000}, {r.ResidualY:0.000} ({Removed(r.ResidualX, r.ResidualY, baseX, baseY):+0%;-0%}); mesh {r.MeshResidualX:0.000}, {r.MeshResidualY:0.000} ({Removed(r.MeshResidualX, r.MeshResidualY, meshX, meshY):+0%;-0%}); the points against the truth's own geometry {r.TruthResidualX:0.000}, {r.TruthResidualY:0.000}"));
            }
            if (report.Interpolation is { } i)
            {
                consoleHost.WriteScrollable(string.Create(inv,
                    $"    a point's reading, on the median geometry, against the truth at the point: slope {i.SlopeAtPoint:0.000}, error {i.ErrorAtPointX:0.000}, {i.ErrorAtPointY:0.000}; over its window (sigma {i.WindowSigmaPx:0.00} px): slope {i.SlopeInWindow:0.000}, error {i.ErrorInWindowX:0.000}, {i.ErrorInWindowY:0.000}"));
                // The reading's noise as an unbiased estimator (its error about its slope over that slope) against what no unbiased
                // estimator using the point's pixels can beat (#1082).
                var (unbiasedX, unbiasedY) = (i.ErrorInWindowX / i.SlopeInWindow, i.ErrorInWindowY / i.SlopeInWindow);
                consoleHost.WriteScrollable(string.Create(inv,
                    $"    the Cramer-Rao bound at the points {i.BoundX:0.000}, {i.BoundY:0.000} px; the reading's error over its window, unbiased, {unbiasedX:0.000}, {unbiasedY:0.000} ({unbiasedX / i.BoundX:0.0}, {unbiasedY / i.BoundY:0.0} times the bound)"));
                double Recovered(double x, double y) => 1 - Math.Sqrt(((x * x) + (y * y)) / ((i.UndewarpedX * i.UndewarpedX) + (i.UndewarpedY * i.UndewarpedY)));
                consoleHost.WriteScrollable(string.Create(inv,
                    $"    left at the places, odd frames (undewarped {i.UndewarpedX:0.000}, {i.UndewarpedY:0.000}): blend {i.BlendX:0.000}, {i.BlendY:0.000} ({Recovered(i.BlendX, i.BlendY):+0%;-0%}); scaled by {i.GainX:0.00}, {i.GainY:0.00} {i.ScaledBlendX:0.000}, {i.ScaledBlendY:0.000} ({Recovered(i.ScaledBlendX, i.ScaledBlendY):+0%;-0%}); kriging {i.KrigedX:0.000}, {i.KrigedY:0.000} ({Recovered(i.KrigedX, i.KrigedY):+0%;-0%}), its errors overlapping {i.KrigedOverlapX:0.000}, {i.KrigedOverlapY:0.000} ({Recovered(i.KrigedOverlapX, i.KrigedOverlapY):+0%;-0%}); the best linear weights {i.CeilingX:0.000}, {i.CeilingY:0.000} ({Recovered(i.CeilingX, i.CeilingY):+0%;-0%})"));
            }
            return 0;
        });
        return command;
    }
}
