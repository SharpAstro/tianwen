using System;
using System.Collections.Generic;
using System.CommandLine;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Console.Lib;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Devices;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Planetary;

namespace TianWen.Cli;

/// <summary>
/// <c>tianwen planetary-live &lt;capture.ser&gt;</c>: how the live stack keeps up with a capture, measured as it runs. The capture is
/// replayed into the node's own ring (<see cref="LiveCameraFrameStream"/>, sized as <see cref="PlanetaryCapture"/> sizes it) at its
/// own frame rate, read off its timestamps, while the node's own loop (<see cref="LiveStackLoop"/>) stacks to the newest frame, for
/// each recipe asked: the masters' interval, the frames the stack folds a second against the capture's, how far behind the newest
/// frame each master is, the window's rebuilds, the frames each master holds, and, given a synthetic capture's truth, the last
/// master's fidelity
/// (docs/plans/planetary-restoration.md, "The live stack, given the batch stack's learnings").
/// </summary>
internal sealed class PlanetaryLiveSubCommand(IConsoleHost consoleHost, ITimeProvider timeProvider)
{
    private static readonly (string Name, RollingWindowOptions Options)[] Recipes =
    [
        ("legacy", RollingWindowOptions.Legacy),
        ("gradient", RollingWindowOptions.Legacy with { QualityEstimator = new GradientEnergyEstimator() }),
        ("plain", RollingWindowOptions.Legacy with { QualityEstimator = new GradientEnergyEstimator(), WhitenedCorrelation = false }),
        ("pipeline", RollingWindowOptions.Legacy with { QualityEstimator = new GradientEnergyEstimator(), WhitenedCorrelation = false, Interpolation = WarpInterpolation.Lanczos3Clamped }),
    ];

    public Command Build()
    {
        var captureArg = new Argument<string>("capture") { Description = "A SER capture, replayed as a live camera." };
        var recipeOpt = new Option<string>("--recipe") { Description = "legacy, gradient, plain (the gradient and plain correlation), pipeline (and Lanczos-3), defaults (the rolling stack's as built), or all.", DefaultValueFactory = _ => "all" };
        var secondsOpt = new Option<double>("--seconds") { Description = "How long each recipe's replay runs, seconds of wall time (the capture's end stops it sooner).", DefaultValueFactory = _ => 90 };
        var rateOpt = new Option<double?>("--rate") { Description = "Frames a second to replay at; the capture's own, from its timestamps, when not given." };
        var truthOpt = new Option<string?>("--truth") { Description = "A synthetic capture's truth (planetary-degrade's .truth.fits): the last master of each recipe scored against it." };
        var planetOpt = new Option<string?>("--planet") { Description = "jupiter or saturn, for the truth's scoring; read off the file's name when not given." };

        var command = new Command("planetary-live", "Measure how the live rolling stack keeps up with a capture replayed at its own rate, recipe by recipe.")
        {
            Arguments = { captureArg },
            Options = { recipeOpt, secondsOpt, rateOpt, truthOpt, planetOpt },
        };

        command.SetAction(async (parseResult, ct) =>
        {
            var inv = CultureInfo.InvariantCulture;
            var path = parseResult.GetValue(captureArg) ?? "";
            if (!File.Exists(path))
            {
                consoleHost.WriteError($"{path}: no such capture");
                return 1;
            }
            var recipeName = (parseResult.GetValue(recipeOpt) ?? "all").ToLowerInvariant();
            (string Name, RollingWindowOptions Options)[] recipes = recipeName switch
            {
                "all" => Recipes,
                "defaults" => [("defaults", new RollingWindowOptions())],
                _ => [.. Recipes.Where(r => r.Name == recipeName)],
            };
            if (recipes.Length == 0)
            {
                consoleHost.WriteError($"--recipe {recipeName}: legacy, gradient, plain, pipeline, defaults or all");
                return 1;
            }
            var truthPath = parseResult.GetValue(truthOpt);
            CatalogIndex planet = parseResult.GetValue(planetOpt)?.ToLowerInvariant() switch
            {
                "saturn" => CatalogIndex.Saturn,
                "jupiter" => CatalogIndex.Jupiter,
                _ => PlanetaryCaptureName.Planet(path) ?? CatalogIndex.Jupiter,
            };

            using var source = SerFrameStream.Open(path);
            double rate;
            if (parseResult.GetValue(rateOpt) is { } asked)
            {
                rate = asked;
            }
            else if (source.TimestampOf(0) is { } first && source.TimestampOf(source.FrameCount - 1) is { } last && last > first)
            {
                rate = (source.FrameCount - 1) / (last - first).TotalSeconds;
            }
            else
            {
                consoleHost.WriteError($"{path}: no frame times to read its rate from; give --rate");
                return 1;
            }
            var seconds = parseResult.GetValue(secondsOpt);
            consoleHost.WriteScrollable(string.Create(inv,
                $"{Path.GetFileName(path)}: {source.FrameCount} frames {source.Width}x{source.Height} {source.Layout}, replayed at {rate:0.0} frames a second for up to {seconds:0} s a recipe"));

            foreach (var (name, options) in recipes)
            {
                ct.ThrowIfCancellationRequested();
                var run = await ReplayAsync(source, options, rate, TimeSpan.FromSeconds(seconds), ct);
                try
                {
                    Report(name, run, rate);
                    if (truthPath is not null && run.Last is { } master)
                    {
                        PlanetaryMasterScore.AgainstTruth(consoleHost, master, truthPath, planet, $"{name}'s last master");
                    }
                }
                finally
                {
                    run.Last?.Release();
                }
            }
            return 0;
        });
        return command;
    }

    // One master as it was published: when, the frame it was stacked to, the newest frame the ring held then, the rebuilds and folds
    // so far, and the frames its sum holds.
    private readonly record struct Published(TimeSpan At, int Built, int Newest, int Rebuilds, long Folds, int Folded);

    private sealed record Run(IReadOnlyList<Published> Masters, int Pushed, TimeSpan Replayed, Image? Last);

    // The capture pushed into the node's ring at its rate, frame by frame on its own clock, while the node's loop stacks it.
    private async Task<Run> ReplayAsync(SerFrameStream source, RollingWindowOptions options, double rate, TimeSpan budget, CancellationToken ct)
    {
        using var ring = new LiveCameraFrameStream(source.Width, source.Height, source.Layout, Math.Max(options.MaxWindowFrames * 2, 1024));
        var masters = new List<Published>();
        Image? last = null;
        var replaying = 1;
        var start = timeProvider.GetTimestamp();
        var loop = Task.Run(() => LiveStackLoop.RunAsync(() => Volatile.Read(ref replaying) == 1, () => ring, options, timeProvider,
            (master, stacker, built) =>
            {
                masters.Add(new Published(timeProvider.GetElapsedTime(start), built, ring.LatestIndex, stacker.Rebuilds, stacker.Folds, stacker.FoldedFrameCount));
                Interlocked.Exchange(ref last, master)?.Release();
            },
            ex => consoleHost.WriteError($"[planetary] a live stack failed: {ex.Message}"),
            ct), ct);

        var pushed = 0;
        try
        {
            for (var i = 0; i < source.FrameCount; i++)
            {
                var due = TimeSpan.FromSeconds(i / rate);
                if (due > budget)
                {
                    break;
                }
                var wait = due - timeProvider.GetElapsedTime(start);
                if (wait > TimeSpan.Zero)
                {
                    await timeProvider.SleepAsync(wait, ct);
                }
                var frame = await source.LoadAsync(i, ct);
                try
                {
                    ring.Push(frame, source.TimestampOf(i));
                }
                finally
                {
                    frame.Release();
                }
                pushed++;
            }
        }
        finally
        {
            Volatile.Write(ref replaying, 0);
            await loop;
        }
        return new Run(masters, pushed, timeProvider.GetElapsedTime(start), Interlocked.Exchange(ref last, null));
    }

    private void Report(string name, Run run, double rate)
    {
        var inv = CultureInfo.InvariantCulture;
        var masters = run.Masters;
        if (masters.Count < 2)
        {
            consoleHost.WriteScrollable($"[planetary] {name}: {masters.Count} master(s) in {run.Replayed.TotalSeconds:0.0} s, too few to read a rate");
            return;
        }
        var intervals = masters.Zip(masters.Skip(1), (a, b) => (b.At - a.At).TotalMilliseconds).Order().ToArray();
        var lags = masters.Select(m => (m.Newest - m.Built) / rate * 1000).Order().ToArray();
        var (first, final) = (masters[0], masters[^1]);
        // Frames folded a second between the first master and the last (rebuilds' folds included), against the frames the
        // replay actually delivered a second, which a starved process falls short of the capture's rate.
        var folded = (final.Folds - first.Folds) / (final.At - first.At).TotalSeconds;
        var delivered = run.Pushed / run.Replayed.TotalSeconds;
        var held = masters.Select(m => (double)m.Folded).Order().ToArray();
        consoleHost.WriteScrollable(string.Create(inv,
            $"[planetary] {name}: {masters.Count} masters from {run.Pushed} frames in {run.Replayed.TotalSeconds:0.0} s ({delivered:0.0} a second delivered of {rate:0.0}); interval median {Median(intervals):0} ms (p90 {Percentile(intervals, 0.9):0}); {folded:0.0} frames folded a second ({folded / rate:P0} of the capture's); behind the newest frame median {Median(lags):0} ms (p90 {Percentile(lags, 0.9):0}); {final.Rebuilds} rebuilds over {masters.Count} masters; a master holds median {Median(held):0} frames (last {final.Folded})"));
    }

    private static double Median(double[] sorted) => Percentile(sorted, 0.5);

    private static double Percentile(double[] sorted, double p) => sorted[Math.Clamp((int)Math.Round(p * (sorted.Length - 1)), 0, sorted.Length - 1)];
}
