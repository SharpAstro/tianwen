using System;
using System.Collections.Generic;
using System.CommandLine;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Imaging.Planetary;
using TianWen.Lib.Stat;
using SharpAstro.Ser;
using Console.Lib;

namespace TianWen.Cli;

/// <summary>
/// <c>planetary-registration</c> (docs/plans/planetary-restoration.md, R5 part 3): every frame of a capture registered several
/// ways, the stacker's cross-correlation against the best frame and against a stack of the best, whitened and plain, a limb fit,
/// and AutoStakkert's own track from its session file, set side by side with no truth. Each pair's difference is the sum of
/// their errors, and the three-cornered hat splits any three into each one's own; a synthetic capture's recorded motion scores
/// every track directly, which is what the hat is checked against.
/// </summary>
internal sealed class PlanetaryRegistrationSubCommand(IConsoleHost consoleHost)
{
    public Command Build()
    {
        var captureArg = new Argument<string>("capture") { Description = "A mono SER capture of a planet." };
        var as3Opt = new Option<string?>("--as3") { Description = "An AutoStakkert session file (.as3) of the same capture, whose planet track is set beside ours." };
        var truthOpt = new Option<string?>("--truth") { Description = "A synthetic capture's recorded motion (planetary-degrade's .frames.csv); by default the one beside the capture, where there is one." };
        var planetOpt = new Option<string>("--planet") { Description = "jupiter or saturn.", DefaultValueFactory = _ => "jupiter" };
        var utcOpt = new Option<string?>("--utc") { Description = "The capture's time (ISO 8601, UTC), for a SER without timestamps." };
        var framesOpt = new Option<int?>("--frames") { Description = "Only the capture's first frames." };
        var correlationOpt = new Option<string>("--correlation") { Description = "How frames are registered, a comma list of plain (cross-correlation) and whitened (phase correlation).", DefaultValueFactory = _ => "plain,whitened" };
        var referenceOpt = new Option<string>("--reference-frames") { Description = "The references to register against, a comma list: 0 the best frame, N a stack of the best N.", DefaultValueFactory = _ => "0" };
        var strideOpt = new Option<int>("--limb-stride") { Description = "Fit the limb on every this many frames (0 for no limb track).", DefaultValueFactory = _ => 4 };
        var fastOpt = new Option<int>("--fast-window") { Description = "The frames either side a difference's slow part is taken over; what is left is each frame's own error.", DefaultValueFactory = _ => 25 };
        var outOpt = new Option<string?>("--out") { Description = "Every track, frame by frame, as a CSV (frame, then each track's x and y)." };

        var command = new Command("planetary-registration",
            "Every frame of a capture registered several ways (the stacker's correlation, a limb fit, AutoStakkert's track) and compared with no truth by the three-cornered hat; against a synthetic capture's recorded motion as well (R5).")
        {
            Arguments = { captureArg },
            Options = { as3Opt, truthOpt, planetOpt, utcOpt, framesOpt, correlationOpt, referenceOpt, strideOpt, fastOpt, outOpt },
        };

        command.SetAction(async (parseResult, ct) =>
        {
            var input = parseResult.GetValue(captureArg) ?? "";
            using var reader = SerReader.Open(input);
            using var whole = new SerFrameStream(reader, ownsReader: false);
            var frames = Math.Min(whole.FrameCount, parseResult.GetValue(framesOpt) ?? whole.FrameCount);
            using var stream = new PlanetaryFrameWindow(whole, 0, frames);
            var inv = CultureInfo.InvariantCulture;

            var correlations = Split(parseResult.GetValue(correlationOpt));
            if (correlations.FirstOrDefault(c => c is not ("plain" or "whitened")) is { } unknown)
            {
                consoleHost.WriteError($"--correlation {unknown}: plain or whitened");
                return 1;
            }
            var references = Split(parseResult.GetValue(referenceOpt)).Select(r => int.Parse(r, inv)).ToArray();

            var tracks = new List<RegistrationTrack>();
            int? referenceIndex = null;
            RegistrationTrack? bestFramePlain = null;
            foreach (var correlation in correlations)
            {
                foreach (var referenceFrames in references)
                {
                    var options = new PlanetaryStackOptions { WhitenedCorrelation = correlation == "whitened", ReferenceFrames = referenceFrames };
                    var (dx, dy, index) = await LuckyImagingStacker.RegisterAllAsync(stream, options, ct);
                    referenceIndex ??= index;
                    var name = $"{correlation}, {(referenceFrames > 1 ? $"a stack of {referenceFrames}" : "the best frame")}";
                    var track = new RegistrationTrack(name, dx, dy);
                    tracks.Add(track);
                    if (correlation == "plain" && referenceFrames <= 1)
                    {
                        bestFramePlain = track;
                    }
                }
            }

            var stride = parseResult.GetValue(strideOpt);
            if (stride > 0 && referenceIndex is { } refIndex)
            {
                var planet = parseResult.GetValue(planetOpt)?.ToLowerInvariant() == "saturn" ? CatalogIndex.Saturn : CatalogIndex.Jupiter;
                if ((stream.MidCapture ?? PlanetaryGeometrySubCommands.ParseUtc(parseResult.GetValue(utcOpt))) is not { } when)
                {
                    consoleHost.WriteError($"{input}: no timestamps for the limb fit's ephemeris (pass --utc, or --limb-stride 0)");
                    return 1;
                }
                var limbOptions = PlanetaryLimbFit.OptionsFor(PhysicalEphemeris.Compute(planet, when));
                // The limb fits start from a plain registration against the best frame, measured for the purpose where none was asked.
                var start = bestFramePlain ?? await PlainBestFrameAsync(stream, ct);
                if (await RegistrationComparison.LimbAsync(stream, start, refIndex, limbOptions, stride, ct) is { } limb)
                {
                    tracks.Add(limb);
                }
                else
                {
                    consoleHost.WriteError($"{input}: the stack's limb could not be fitted, so no limb track");
                }
            }

            if (parseResult.GetValue(as3Opt) is { } as3Path)
            {
                if (AutoStakkertSession.TryRead(as3Path) is not { } session)
                {
                    consoleHost.WriteError($"{as3Path}: not an AutoStakkert session file");
                    return 1;
                }
                if (session.Track.Length != whole.FrameCount)
                {
                    consoleHost.WriteError($"{as3Path}: {session.Track.Length} frames tracked, the capture has {whole.FrameCount}");
                    return 1;
                }
                consoleHost.WriteScrollable($"AutoStakkert {session.Version}: quality {session.Setting("_quality_type")}, noise robust {session.Setting("_quality_gradient_noise_robust")}, "
                    + $"reference a stack of {session.Setting("_reference_num_frames")}, {session.AlignmentPoints.Length} alignment points ("
                    + string.Join(", ", session.AlignmentPoints.GroupBy(p => p.Size).OrderBy(g => g.Key).Select(g => $"{g.Count()} of {g.Key} px")) + ")");
                tracks.Add(new RegistrationTrack("AutoStakkert", [.. session.Track.Take(frames).Select(p => p.X)], [.. session.Track.Take(frames).Select(p => p.Y)]));
            }

            RegistrationTrack? truth = null;
            var truthPath = parseResult.GetValue(truthOpt) ?? Path.ChangeExtension(input, ".frames.csv");
            if (File.Exists(truthPath))
            {
                truth = ReadTruth(truthPath, frames);
            }

            consoleHost.WriteScrollable($"{Path.GetFileName(input)}: {frames} frames, the best frame {referenceIndex}; px a axis, x and y");
            foreach (var track in tracks)
            {
                consoleHost.WriteScrollable(string.Create(inv, $"    {track.Name}: {track.Placed} frames placed, spread {Spread(track.X):0.000}, {Spread(track.Y):0.000}")
                    + (truth is null ? "" : string.Create(inv, $"; its error against the truth {Sigma(RegistrationComparison.DifferenceVariance(track, truth).X):0.000}, {Sigma(RegistrationComparison.DifferenceVariance(track, truth).Y):0.000}")));
            }
            var window = parseResult.GetValue(fastOpt);
            consoleHost.WriteScrollable($"  each pair's difference (the sum of their errors, if independent), and its fast part (less its mean over {window} frames either side):");
            for (var i = 0; i < tracks.Count; i++)
            {
                for (var j = i + 1; j < tracks.Count; j++)
                {
                    var d = RegistrationComparison.DifferenceVariance(tracks[i], tracks[j]);
                    var fast = RegistrationComparison.FastDifferenceVariance(tracks[i], tracks[j], window);
                    consoleHost.WriteScrollable(string.Create(inv, $"    {tracks[i].Name} against {tracks[j].Name}: {Sigma(d.X):0.000}, {Sigma(d.Y):0.000}; fast {Sigma(fast.X):0.000}, {Sigma(fast.Y):0.000}"));
                }
            }
            consoleHost.WriteScrollable("  the three-cornered hat, each triple's errors (negative: the three are not independent):");
            for (var i = 0; i < tracks.Count; i++)
            {
                for (var j = i + 1; j < tracks.Count; j++)
                {
                    for (var k = j + 1; k < tracks.Count; k++)
                    {
                        var hat = RegistrationComparison.Hat(tracks[i], tracks[j], tracks[k]);
                        consoleHost.WriteScrollable(string.Create(inv, $"    {tracks[i].Name} {Sigma(hat[0].X):0.000}, {Sigma(hat[0].Y):0.000}; {tracks[j].Name} {Sigma(hat[1].X):0.000}, {Sigma(hat[1].Y):0.000}; {tracks[k].Name} {Sigma(hat[2].X):0.000}, {Sigma(hat[2].Y):0.000}"));
                    }
                }
            }
            if (parseResult.GetValue(outOpt) is { } outPath)
            {
                using var writer = new StreamWriter(outPath);
                writer.WriteLine("frame," + string.Join(',', tracks.Select(t => $"{Column(t.Name)}_x,{Column(t.Name)}_y")) + (truth is null ? "" : ",truth_x,truth_y"));
                for (var f = 0; f < frames; f++)
                {
                    var cells = tracks.Select(t => string.Create(inv, $"{t.X[f]:0.0000},{t.Y[f]:0.0000}"));
                    writer.WriteLine(string.Create(inv, $"{f},") + string.Join(',', cells) + (truth is null ? "" : string.Create(inv, $",{truth.X[f]:0.0000},{truth.Y[f]:0.0000}")));
                }
            }
            return 0;
        });
        return command;
    }

    private static async Task<RegistrationTrack> PlainBestFrameAsync(IPlanetaryFrameStream stream, CancellationToken ct)
    {
        var (dx, dy, _) = await LuckyImagingStacker.RegisterAllAsync(stream, new PlanetaryStackOptions { WhitenedCorrelation = false }, ct);
        return new RegistrationTrack("plain, the best frame", dx, dy);
    }

    // A synthetic capture's recorded disk shifts, the first `frames` of them.
    private static RegistrationTrack ReadTruth(string path, int frames)
    {
        var (x, y) = (new double[frames], new double[frames]);
        Array.Fill(x, double.NaN);
        Array.Fill(y, double.NaN);
        foreach (var line in File.ReadLines(path).Skip(1))
        {
            var fields = line.Split(',');
            if (fields.Length >= 3 && int.TryParse(fields[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var f) && f < frames)
            {
                x[f] = double.Parse(fields[1], CultureInfo.InvariantCulture);
                y[f] = double.Parse(fields[2], CultureInfo.InvariantCulture);
            }
        }
        return new RegistrationTrack("truth", x, y);
    }

    // A track's name as a CSV column: its words joined by underscores.
    private static string Column(string name) => string.Join('_', name.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries));

    private static string[] Split(string? text) => (text ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(s => s.ToLowerInvariant()).ToArray();

    // A variance as the standard deviation it is, its sign kept so a negative hat reading shows as one.
    private static double Sigma(double variance) => Math.Sign(variance) * Math.Sqrt(Math.Abs(variance));

    // A track's spread about its median, the same robust measure the differences use.
    private static double Spread(double[] values)
    {
        var zeros = new double[values.Length];
        return Sigma(ThreeCorneredHat.DifferenceVariance(values, zeros));
    }
}
