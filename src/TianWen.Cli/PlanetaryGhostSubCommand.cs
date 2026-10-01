using System;
using System.Collections.Generic;
using System.CommandLine;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Planetary;
using TianWen.Lib.Imaging.Stacking;
using Console.Lib;
using SharpAstro.Ser;

namespace TianWen.Cli;

/// <summary>
/// <c>planetary-ghost</c> (docs/plans/planetary-restoration.md, R7a): each capture stacked, each plane of its master fitted for a ghost
/// (a defocused copy of the planet beside a scatter glow) and for coma (a flare) in its place, the residual read ring by ring for a
/// shell, and, with <c>--halves</c>, the capture's two halves fitted apart. <c>--inject</c> adds a ghost of known shape first.
/// </summary>
internal sealed class PlanetaryGhostSubCommand(IConsoleHost consoleHost, MasterPreviewRenderer previewRenderer)
{
    public Command Build()
    {
        var capturesArg = new Argument<string[]>("captures") { Description = "SER captures of a planet.", Arity = ArgumentArity.OneOrMore };
        var framesOpt = new Option<int?>("--frames") { Description = "Only the first frames." };
        var keepOpt = new Option<double>("--keep") { Description = "The share of the frames stacked, by the gradient.", DefaultValueFactory = _ => 0.05 };
        var injectOpt = new Option<string?>("--inject") { Description = "A ghost added before the fit, a,dx,dy,rho (strength, shift in px, defocus radius in px)." };
        var halvesOpt = new Option<bool>("--halves") { Description = "Also fit the capture's first and second halves of frames apart (the kill line: one copy, not a moving one)." };
        var panelOpt = new Option<string?>("--panel") { Description = "A PNG per capture, the planes twenty times brighter: the stack, the stack less the fitted ghost, the ghost." };

        var command = new Command("planetary-ghost",
            "A ghost fitted beyond a planet as a defocused copy of it beside a scatter glow, coma as a flare in its place, and the residual read for a shell (R7a).")
        {
            Arguments = { capturesArg },
            Options = { framesOpt, keepOpt, injectOpt, halvesOpt, panelOpt },
        };

        command.SetAction(async (parseResult, ct) =>
        {
            var inv = CultureInfo.InvariantCulture;
            double[]? inject = null;
            if (parseResult.GetValue(injectOpt) is { } text)
            {
                inject = [.. text.Split(',').Select(v => double.Parse(v, inv))];
                if (inject.Length != 4)
                {
                    consoleHost.WriteError("--inject takes a,dx,dy,rho");
                    return 1;
                }
            }
            var captures = parseResult.GetValue(capturesArg) ?? [];
            foreach (var capture in captures)
            {
                using var reader = SerReader.Open(capture);
                using var whole = new SerFrameStream(reader, ownsReader: false);
                var count = Math.Min(whole.FrameCount, parseResult.GetValue(framesOpt) ?? whole.FrameCount);
                var name = Path.GetFileName(capture);
                var planes = await StackPlanesAsync(new PlanetaryFrameWindow(whole, 0, count), parseResult.GetValue(keepOpt), ct);
                var panels = new List<float[]?[]>();
                for (var c = 0; c < planes.Count; c++)
                {
                    var (plane, width, height) = planes[c];
                    if (inject is { } g)
                    {
                        var ghost = new PlanetaryGhost.Source(plane, width, height).Ghost(g[0], g[1], g[2], g[3]);
                        for (var i = 0; i < plane.Length; i++)
                        {
                            plane[i] += ghost[i];
                        }
                    }
                    var label = string.Create(inv, $"{name}{(planes.Count > 1 ? $" plane {c}" : "")}{(inject is null ? "" : " with a ghost added")}");
                    panels.Add(Report(label, plane, width, height, inv));
                }

                if (parseResult.GetValue(halvesOpt))
                {
                    foreach (var (first, length, half) in new[] { (0, count / 2, "first half"), (count / 2, count - (count / 2), "second half") })
                    {
                        var halfPlanes = await StackPlanesAsync(new PlanetaryFrameWindow(whole, first, length), parseResult.GetValue(keepOpt), ct);
                        for (var c = 0; c < halfPlanes.Count; c++)
                        {
                            var (plane, width, height) = halfPlanes[c];
                            var fit = PlanetaryGhost.FitGhost(plane, new PlanetaryGhost.Source(plane, width, height));
                            consoleHost.WriteScrollable(string.Create(inv,
                                $"    {half}{(halfPlanes.Count > 1 ? $" plane {c}" : "")}: ghost {fit.Strength:0.0000} at ({fit.ShiftX:0.00}, {fit.ShiftY:0.00}) px, radius {fit.Radius:0.00} px"));
                        }
                    }
                }

                if (parseResult.GetValue(panelOpt) is { } panelPath && planes.Count > 0)
                {
                    var (_, width, height) = planes[0];
                    var path = captures.Length == 1 ? panelPath : Path.ChangeExtension(panelPath, null) + "-" + Path.GetFileNameWithoutExtension(capture) + ".png";
                    var size = Math.Max(width, height);
                    var disk = new MetricDisk(width / 2.0, height / 2.0, size / 3.2);
                    var rows = panels.Select(r => r.Select(p => p is null ? null : Square(p, width, height, size)).ToArray()).ToArray();
                    var panel = PlanetaryInversesSubCommand.Panel(rows, size, disk);
                    try
                    {
                        await previewRenderer.RenderPlanetaryAsync(panel, path, gamma: 1, ct: ct);
                    }
                    finally
                    {
                        panel.Release();
                    }
                    consoleHost.WriteScrollable($"    wrote {path}: the stack, the stack less the fitted ghost, the ghost, each twenty times brighter, a row a plane");
                }
            }
            return 0;
        });
        return command;
    }

    // One plane's two fits, its residual read for a shell, and its panel row (twenty times brighter: the stack, less the ghost, the ghost).
    private float[]?[] Report(string label, float[] plane, int width, int height, CultureInfo inv)
    {
        var source = new PlanetaryGhost.Source(plane, width, height);
        var ghost = PlanetaryGhost.FitGhost(plane, source);
        var coma = PlanetaryGhost.FitComa(plane, source);
        var ghostPlane = source.Ghost(ghost.Strength, ghost.ShiftX, ghost.ShiftY, ghost.Radius);
        var ghostModel = Sum(ghostPlane, source.Glow(ghost.Glow, ghost.GlowExponent), (float)ghost.Sky);
        var comaModel = Sum(source.Flare(coma.Strength, coma.Length, coma.AngleDeg), source.Glow(coma.Glow, coma.GlowExponent), (float)coma.Sky);
        var ghostResidual = plane.Select((v, i) => v - ghostModel[i]).ToArray();
        var comaResidual = plane.Select((v, i) => v - comaModel[i]).ToArray();
        var rings = PlanetaryGhost.Rings(ghostResidual, source);
        var worst = rings.Max(r => Math.Abs(r.Mean) / r.StandardError);
        var unmodelled = PlanetaryGhost.Rings(plane.Select((v, i) => v - (float)ghost.Sky).ToArray(), source).Max(r => Math.Abs(r.Mean) / r.StandardError);
        consoleHost.WriteScrollable(string.Create(inv,
            $"{label}: ghost {ghost.Strength:0.0000} at ({ghost.ShiftX:0.00}, {ghost.ShiftY:0.00}) px, radius {ghost.Radius:0.00} px; glow {ghost.Glow:0.0000} at q {ghost.GlowExponent:0.00}; sky {ghost.Sky:0.00000}; rms {ghost.Rms:0.00000}"));
        consoleHost.WriteScrollable(string.Create(inv,
            $"    coma in its place: flare {coma.Strength:0.0000}, {coma.Length:0.0} px at {coma.AngleDeg:0.0} degrees; glow {coma.Glow:0.0000} at q {coma.GlowExponent:0.00}; rms {coma.Rms:0.00000}"));
        consoleHost.WriteScrollable(string.Create(inv,
            $"    residual rms, 3 to 9 px out / past 9 px: the ghost {PlanetaryGhost.Rms(ghostResidual, source, 3, 9):0.00000} / {PlanetaryGhost.Rms(ghostResidual, source, 9, double.PositiveInfinity):0.00000}, coma {PlanetaryGhost.Rms(comaResidual, source, 3, 9):0.00000} / {PlanetaryGhost.Rms(comaResidual, source, 9, double.PositiveInfinity):0.00000} ({(PlanetaryGhost.Rms(ghostResidual, source, 3, 9) <= PlanetaryGhost.Rms(comaResidual, source, 3, 9) && PlanetaryGhost.Rms(ghostResidual, source, 9, double.PositiveInfinity) <= PlanetaryGhost.Rms(comaResidual, source, 9, double.PositiveInfinity) ? "the ghost" : "coma, or neither alone")} fits better)"));
        consoleHost.WriteScrollable(string.Create(inv,
            $"    a shell, 3 to 40 px out: the worst ring's mean over its standard error, the sky alone taken out {unmodelled:0.0}, the ghost and glow taken out {worst:0.0} ({(worst <= 3 ? "no shell" : "a SHELL left")}); rings {string.Join(" ", rings.Select(r => (r.Mean * 1e4).ToString("0.0", inv)))} (1e-4 of the peak)"));
        float[] Brighter(IEnumerable<float> p) => [.. p.Select(v => 20 * v)];
        return [Brighter(plane), Brighter(plane.Select((v, i) => v - ghostPlane[i])), Brighter(ghostPlane)];
    }

    // Each plane of the capture's master, normalised on its border and its peak.
    private static async Task<List<(float[] Plane, int Width, int Height)>> StackPlanesAsync(PlanetaryFrameWindow stream, double keep, CancellationToken ct)
    {
        using (stream)
        {
            var options = new PlanetaryStackOptions
            {
                KeepFraction = keep,
                WhitenedCorrelation = false,
                Interpolation = WarpInterpolation.Lanczos3,
                QualityEstimator = new GradientEnergyEstimator(),
            };
            var result = await new LuckyImagingStacker().StackGlobalAsync(stream, options, ct);
            try
            {
                var master = result.Master;
                var planes = new List<(float[], int, int)>();
                for (var c = 0; c < master.ChannelCount; c++)
                {
                    planes.Add((PlanetaryGhost.Normalise(master.GetChannelSpan(c), master.Width, master.Height), master.Width, master.Height));
                }
                return planes;
            }
            finally
            {
                result.Master.Release();
            }
        }
    }

    private static float[] Sum(float[] a, float[] b, float constant) => [.. a.Select((v, i) => v + b[i] + constant)];

    // A plane put in the middle of a square, for the panel.
    private static float[] Square(float[] plane, int width, int height, int size)
    {
        var square = new float[size * size];
        var (ox, oy) = ((size - width) / 2, (size - height) / 2);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                square[((y + oy) * size) + x + ox] = plane[(y * width) + x];
            }
        }
        return square;
    }
}
