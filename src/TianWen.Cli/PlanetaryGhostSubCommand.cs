using System;
using System.Collections.Generic;
using System.Collections.Immutable;
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
/// (an elliptical defocused copy of the planet beside a free round glow) and for coma (a flare) in its place, the non-round part read
/// band by band before and after the copy's non-round part is taken out, and, with <c>--halves</c>, the capture's two halves fitted
/// apart. <c>--inject</c> adds a ghost of known shape first; <c>--removed</c> writes each plane with the shell taken out.
/// </summary>
internal sealed class PlanetaryGhostSubCommand(IConsoleHost consoleHost, MasterPreviewRenderer previewRenderer)
{
    public Command Build()
    {
        var capturesArg = new Argument<string[]>("captures") { Description = "SER captures of a planet.", Arity = ArgumentArity.OneOrMore };
        var framesOpt = new Option<int?>("--frames") { Description = "Only the first frames." };
        var keepOpt = new Option<double>("--keep") { Description = "The share of the frames stacked, by the gradient.", DefaultValueFactory = _ => 0.05 };
        var injectOpt = new Option<string?>("--inject") { Description = "A ghost added before the fit, a,dx,dy,rho[,ratio,angle[,split]] (strength, shift in px, defocus semi-major axis in px, axis ratio, angle in degrees, its two halves' distance either side in px)." };
        var halvesOpt = new Option<bool>("--halves") { Description = "Also fit the capture's first and second halves of frames apart (the kill line: one copy, not a moving one)." };
        var panelOpt = new Option<string?>("--panel") { Description = "A PNG per capture, a row a plane: the stack and the stack less the shell, twenty times brighter, and the shell, 200 times about grey." };
        var removedOpt = new Option<string?>("--removed") { Description = "A folder each plane is written to as FITS (sky zero, peak one) with the shell taken out (the fitted copy's non-round part, beyond the planet), beside the shell itself." };
        var marginOpt = new Option<double>("--margin") { Description = "Pixels nearer the planet than this are left out of the fits.", DefaultValueFactory = _ => PlanetaryGhost.FitMargin };
        var stacksOpt = new Option<string?>("--stacks") { Description = "A folder each stack is kept in and read back from (a capture's, and its halves'), so a refit does not restack." };

        var command = new Command("planetary-ghost",
            "A ghost fitted beyond a planet as a defocused copy of it beside a free round glow, coma as a flare in its place, the residual read for a shell, and the copy taken out (R7a).")
        {
            Arguments = { capturesArg },
            Options = { framesOpt, keepOpt, injectOpt, halvesOpt, panelOpt, stacksOpt, removedOpt, marginOpt },
        };

        command.SetAction(async (parseResult, ct) =>
        {
            var inv = CultureInfo.InvariantCulture;
            double[]? inject = null;
            if (parseResult.GetValue(injectOpt) is { } text)
            {
                inject = [.. text.Split(',').Select(v => double.Parse(v, inv))];
                if (inject.Length is not (4 or 6 or 7))
                {
                    consoleHost.WriteError("--inject takes a,dx,dy,rho[,ratio,angle[,split]]");
                    return 1;
                }
            }
            var captures = parseResult.GetValue(capturesArg) ?? [];
            var stacks = parseResult.GetValue(stacksOpt);
            if (stacks is not null)
            {
                Directory.CreateDirectory(stacks);
            }
            var frames = parseResult.GetValue(framesOpt);
            var margin = parseResult.GetValue(marginOpt);
            string? Cached(string capture, string part) => stacks is null ? null
                : Path.Combine(stacks, string.Create(inv, $"{Path.GetFileNameWithoutExtension(capture)}.{part}.keep{parseResult.GetValue(keepOpt):0.###}{(frames is { } f ? $".frames{f}" : "")}.fits"));
            foreach (var capture in captures)
            {
                using var reader = SerReader.Open(capture);
                using var whole = new SerFrameStream(reader, ownsReader: false);
                var count = Math.Min(whole.FrameCount, parseResult.GetValue(framesOpt) ?? whole.FrameCount);
                var name = Path.GetFileName(capture);
                var planes = await StackPlanesAsync(new PlanetaryFrameWindow(whole, 0, count), parseResult.GetValue(keepOpt), Cached(capture, "stack"), ct);
                var panels = new List<float[]?[]>();
                for (var c = 0; c < planes.Count; c++)
                {
                    var (plane, width, height) = planes[c];
                    float[]? injected = null;
                    var before = plane.ToArray();
                    if (inject is { } g)
                    {
                        injected = new PlanetaryGhost.Source(plane, width, height).Ghost(g[0], g[1], g[2], g[3], g.Length > 4 ? g[4] : 1, g.Length > 4 ? g[5] : 0, g.Length > 6 ? g[6] : 0);
                        for (var i = 0; i < plane.Length; i++)
                        {
                            plane[i] += injected[i];
                        }
                    }
                    var label = string.Create(inv, $"{name}{(planes.Count > 1 ? $" plane {c}" : "")}{(inject is null ? "" : " with a ghost added")}");
                    var (row, shell) = Report(label, plane, width, height, margin, injected is null ? null : (injected, before), inv);
                    panels.Add(row);
                    if (parseResult.GetValue(removedOpt) is { } removed)
                    {
                        Directory.CreateDirectory(removed);
                        var stem = Path.Combine(removed, Path.GetFileNameWithoutExtension(capture) + (planes.Count > 1 ? string.Create(inv, $".plane{c}") : "") + (inject is null ? "" : ".injected"));
                        Write(stem + ".ghost-removed.fits", [.. plane.Select((v, i) => v - shell[i])], width, height);
                        Write(stem + ".shell.fits", shell, width, height);
                        consoleHost.WriteScrollable($"    wrote {stem}.ghost-removed.fits and the shell taken out, {stem}.shell.fits");
                    }
                }

                if (parseResult.GetValue(halvesOpt))
                {
                    foreach (var (first, length, half) in new[] { (0, count / 2, "first half"), (count / 2, count - (count / 2), "second half") })
                    {
                        var halfPlanes = await StackPlanesAsync(new PlanetaryFrameWindow(whole, first, length), parseResult.GetValue(keepOpt),
                            Cached(capture, half.Replace(' ', '-')), ct);
                        for (var c = 0; c < halfPlanes.Count; c++)
                        {
                            var (plane, width, height) = halfPlanes[c];
                            var source = new PlanetaryGhost.Source(plane, width, height, margin);
                            var fit = PlanetaryGhost.FitGhost(plane, source);
                            var shell = PlanetaryGhost.Quadrupoles(source.Ghost(fit.Strength, fit.ShiftX, fit.ShiftY, fit.Radius, fit.AxisRatio, fit.AngleDeg, fit.Separation), source, Bands);
                            consoleHost.WriteScrollable(string.Create(inv,
                                $"    {half}{(halfPlanes.Count > 1 ? $" plane {c}" : "")}: ghost {fit.Strength:0.0000} at ({fit.ShiftX:0.00}, {fit.ShiftY:0.00}) px, radius {fit.Radius:0.00} px, axis ratio {fit.AxisRatio:0.00} at {fit.AngleDeg:0.0} degrees, split {fit.Separation:0.00} px; its shell {Describe(shell, inv)}"));
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
                    consoleHost.WriteScrollable($"    wrote {path}: a row a plane, the stack and the stack less the shell twenty times brighter, the shell 200 times about grey");
                }
            }
            return 0;
        });
        return command;
    }

    // The bands of distance outside the planet the non-round part is read in, px.
    private static readonly (double From, double To)[] Bands = [(3, 6), (6, 10), (10, 15), (15, 20), (20, 30)];

    private static string Describe(ImmutableArray<Quadrupole> quadrupoles, CultureInfo inv) =>
        string.Join(" | ", quadrupoles.Select(q => string.Create(inv, $"{q.From:0}-{q.To:0}: {q.Amplitude * 1e4:0.0}@{q.AxisDeg:0}")));

    // One plane's fits, its non-round part read before and after the shell is taken out, its panel row and the shell.
    private (float[]?[] Row, float[] Shell) Report(string label, float[] plane, int width, int height, double margin, (float[] Ghost, float[] Before)? injected, CultureInfo inv)
    {
        var source = new PlanetaryGhost.Source(plane, width, height, margin);
        var glow = PlanetaryGhost.FitGlow(plane, source);
        var ghost = PlanetaryGhost.FitGhost(plane, source);
        var coma = PlanetaryGhost.FitComa(plane, source);
        var copy = source.Ghost(ghost.Strength, ghost.ShiftX, ghost.ShiftY, ghost.Radius, ghost.AxisRatio, ghost.AngleDeg, ghost.Separation);
        var shell = PlanetaryGhost.Shell(copy, source);
        var removed = plane.Select((v, i) => v - shell[i]).ToArray();
        var ghostModel = Sum(copy, source.Glow(ghost.Glow.AsSpan()), (float)ghost.Sky);
        var ghostResidual = plane.Select((v, i) => v - ghostModel[i]).ToArray();
        var rings = PlanetaryGhost.Rings(ghostResidual, source, margin);
        var worst = rings.Max(r => Math.Abs(r.Mean) / r.StandardError);
        consoleHost.WriteScrollable(string.Create(inv,
            $"{label}: ghost {ghost.Strength:0.0000} at ({ghost.ShiftX:0.00}, {ghost.ShiftY:0.00}) px, radius {ghost.Radius:0.00} px, axis ratio {ghost.AxisRatio:0.00} at {ghost.AngleDeg:0.0} degrees, split {ghost.Separation:0.00} px; rms {ghost.Rms:0.00000}, the glow alone {glow.Rms:0.00000}"));
        consoleHost.WriteScrollable(string.Create(inv,
            $"    coma in its place: flare {coma.Strength:0.0000}, {coma.Length:0.0} px at {coma.AngleDeg:0.0} degrees; rms {coma.Rms:0.00000} ({(ghost.Rms <= coma.Rms ? "the ghost" : "coma")} fits better)"));
        consoleHost.WriteScrollable(string.Create(inv,
            $"    non-round, px out: amplitude 1e-4 of the peak @ long axis, degrees from +x toward +y"));
        consoleHost.WriteScrollable($"      the stack:          {Describe(PlanetaryGhost.Quadrupoles(plane, source, Bands), inv)}");
        if (injected is { } added)
        {
            // Read through this plane's own source: the ghost moves the planet's edge, so a read through another source is another read.
            consoleHost.WriteScrollable($"      the injected shell: {Describe(PlanetaryGhost.Quadrupoles(added.Ghost, source, Bands), inv)}");
            consoleHost.WriteScrollable($"      before the ghost:   {Describe(PlanetaryGhost.Quadrupoles(added.Before, source, Bands), inv)}");
        }
        consoleHost.WriteScrollable($"      the copy's shell:   {Describe(PlanetaryGhost.Quadrupoles(copy, source, Bands), inv)}");
        consoleHost.WriteScrollable($"      the shell taken out: {Describe(PlanetaryGhost.Quadrupoles(removed, source, Bands), inv)}");
        consoleHost.WriteScrollable(string.Create(inv,
            $"    rings from {margin:0} to 40 px: the worst ring's mean over its standard error with the ghost and glow taken out {worst:0.0} ({(worst <= 3 ? "no shell" : "a SHELL left")}); rings {string.Join(" ", rings.Select(r => (r.Mean * 1e4).ToString("0.0", inv)))} (1e-4)"));
        float[] Brighter(IEnumerable<float> p) => [.. p.Select(v => 20 * v)];
        return ([Brighter(plane), Brighter(removed), [.. shell.Select(v => 0.5f + (200 * v))]], shell);
    }

    private static void Write(string path, float[] plane, int width, int height)
    {
        var channel = new float[height, width];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                channel[y, x] = plane[(y * width) + x];
            }
        }
        var image = Image.FromChannel(channel, maxValue: 1f, minValue: 0f);
        try
        {
            image.WriteToFitsFile(path);
        }
        finally
        {
            image.Release();
        }
    }

    // Each plane of the capture's master, normalised on its border and its peak; read back from `cache` when it is there, kept there when not.
    private static async Task<List<(float[] Plane, int Width, int Height)>> StackPlanesAsync(PlanetaryFrameWindow stream, double keep, string? cache, CancellationToken ct)
    {
        using (stream)
        {
            if (cache is not null && File.Exists(cache) && Image.TryReadFitsFile(cache, out var kept, out _))
            {
                try
                {
                    return Planes(kept);
                }
                finally
                {
                    kept.Release();
                }
            }
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
                if (cache is not null)
                {
                    result.Master.WriteToFitsFile(cache);
                }
                return Planes(result.Master);
            }
            finally
            {
                result.Master.Release();
            }
        }
    }

    private static List<(float[] Plane, int Width, int Height)> Planes(Image master)
    {
        var planes = new List<(float[], int, int)>();
        for (var c = 0; c < master.ChannelCount; c++)
        {
            planes.Add((PlanetaryGhost.Normalise(master.GetChannelSpan(c), master.Width, master.Height), master.Width, master.Height));
        }
        return planes;
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
