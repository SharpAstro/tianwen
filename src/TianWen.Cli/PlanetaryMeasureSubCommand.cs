using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.CommandLine;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Planetary;
using TianWen.Lib.Stat;
using SharpAstro.Ser;
using Console.Lib;

namespace TianWen.Cli;

/// <summary>
/// <c>planetary-measure</c> (docs/plans/planetary-restoration.md, R3): stacks a capture as each candidate asks (a keep fraction,
/// a sharpening preset, a method), and its two halves the same way from disjoint frames, then measures every stack with
/// <see cref="PlanetaryMetrics"/>, registered onto one disk by the limb fit. With a truth (a synthetic capture's), each stack's
/// fidelity and limb against it, and how the truth-free metrics rank the candidates against the truth-based ones (Spearman,
/// the plan's pre-registered 0.8).
/// </summary>
internal sealed class PlanetaryMeasureSubCommand(IConsoleHost consoleHost)
{
    public Command Build()
    {
        var captureArg = new Argument<string>("capture") { Description = "A SER capture of a planet, mono or colour (a colour one is scored a colour at a time against its truths beside it)." };
        var truthOpt = new Option<string?>("--truth") { Description = "The truth to score the stacks against (planetary-degrade writes it beside a synthetic capture); without it, only the truth-free metrics." };
        var planetOpt = new Option<string>("--planet") { Description = "jupiter or saturn.", DefaultValueFactory = _ => "jupiter" };
        var utcOpt = new Option<string?>("--utc") { Description = "The capture's time (ISO 8601, UTC), for a SER without timestamps and no truth." };
        var framesOpt = new Option<int?>("--frames") { Description = "Only the capture's first frames." };
        var keepOpt = new Option<string>("--keep") { Description = "The keep fractions to stack at, a comma list.", DefaultValueFactory = _ => "0.02,0.05,0.2,0.5" };
        var sharpenOpt = new Option<string>("--sharpen") { Description = "The sharpening presets to stack with, a comma list of none, default, bandpass and combo.", DefaultValueFactory = _ => "none,default,combo" };
        var methodOpt = new Option<string>("--method") { Description = "The stacking methods, a comma list of ap (alignment points with the per-point best-of weighting, the full lucky-imaging path), ap-flat (alignment points, each frame weighted as a whole) and global.", DefaultValueFactory = _ => "ap" };
        var spacingOpt = new Option<string>("--ap-spacing") { Description = "The alignment points' spacings to stack at, a comma list (ap methods only).", DefaultValueFactory = _ => "24" };
        var patchOpt = new Option<int>("--ap-patch") { Description = "The alignment points' patch, a power of two.", DefaultValueFactory = _ => 32 };
        var correlationOpt = new Option<string>("--correlation") { Description = "How frames and points are registered, a comma list of whitened (phase correlation) and plain (cross-correlation).", DefaultValueFactory = _ => "whitened" };
        var meshOpt = new Option<float>("--mesh-spacing") { Description = "The displacement mesh's node spacing, px (ap methods only).", DefaultValueFactory = _ => 24f };
        var influenceOpt = new Option<float>("--mesh-influence") { Description = "How far a point's displacement reaches into the mesh, px (ap methods only).", DefaultValueFactory = _ => 48f };
        var poolOpt = new Option<string>("--pool") { Description = "Pool each point's warp over this many frames either side (a Gaussian's sigma, 0 for none), a comma list (ap methods only).", DefaultValueFactory = _ => "0" };
        var geometryOpt = new Option<string>("--geometry") { Description = "The geometry the points put the stack on, a comma list of reference (the reference frame's) and median (each point's median over the frames); ap methods only.", DefaultValueFactory = _ => "reference" };
        var interpolationOpt = new Option<string>("--interpolation") { Description = "The kernel each frame is resampled by as it is stacked, a comma list of bilinear, lanczos3 and lanczos3-clamped.", DefaultValueFactory = _ => "bilinear" };
        var referenceOpt = new Option<string>("--reference-frames") { Description = "What frames are registered against, a comma list: 0 the best frame, N a stack of the best N.", DefaultValueFactory = _ => "0" };
        var drizzleOpt = new Option<string>("--drizzle") { Description = "A colour capture's Bayer drizzle stacks, a comma list of scales (1 the sensor's grid, 1.5 past it); R5a. Each is scored against the truths at its scale, which planetary-degrade --truth-upsample writes.", DefaultValueFactory = _ => "" };
        var pixfracOpt = new Option<float>("--pixfrac") { Description = "The drizzle's drop size, a fraction of a photosite.", DefaultValueFactory = _ => 1f };
        var noHalvesOpt = new Option<bool>("--no-halves") { Description = "Stack each candidate only, not its two halves: no halves' agreement, in a third of the time." };
        var cutoffOpt = new Option<double?>("--cutoff") { Description = "The telescope's cutoff in cycles a pixel, for the power past it (fabrication); none past Nyquist." };
        var derotateOpt = new Option<string>("--derotate") { Description = "Each frame as taken (none) or carried through the planet's rotation to the truth's instant, or the capture's middle without one (frames; R6), a comma list.", DefaultValueFactory = _ => "none" };

        var command = new Command("planetary-measure",
            "Stacks a capture as each candidate asks, and its two halves, and measures every stack (R3): fidelity per wavelet band and the limb against a truth, the halves' agreement per band, the limb's undershoot; with a truth, how the truth-free metrics rank the candidates against the truth-based ones.")
        {
            Arguments = { captureArg },
            Options = { truthOpt, planetOpt, utcOpt, framesOpt, keepOpt, sharpenOpt, methodOpt, spacingOpt, patchOpt, correlationOpt, poolOpt, geometryOpt, meshOpt, influenceOpt, interpolationOpt, referenceOpt, drizzleOpt, pixfracOpt, noHalvesOpt, cutoffOpt, derotateOpt },
        };

        command.SetAction(async (parseResult, ct) =>
        {
            var input = parseResult.GetValue(captureArg) ?? "";
            var planet = parseResult.GetValue(planetOpt)?.ToLowerInvariant() == "saturn" ? CatalogIndex.Saturn : CatalogIndex.Jupiter;
            using var reader = SerReader.Open(input);
            using var whole = new SerFrameStream(reader, ownsReader: false);
            var frames = Math.Min(whole.FrameCount, parseResult.GetValue(framesOpt) ?? whole.FrameCount);
            using var stream = new PlanetaryFrameWindow(whole, 0, frames);

            // The truth, and the time and disk it was rendered at.
            float[]? truthPlane = null;
            MetricDisk? truthDisk = null;
            DateTimeOffset? truthTime = null;
            if (parseResult.GetValue(truthOpt) is { } truthPath)
            {
                if (ReadTruth(truthPath, consoleHost) is not { } read)
                {
                    return 1;
                }
                (truthPlane, truthDisk, truthTime) = (read.Plane, read.Disk, read.Time);
            }
            if ((truthTime ?? stream.MidCapture ?? PlanetaryGeometrySubCommands.ParseUtc(parseResult.GetValue(utcOpt))) is not { } when)
            {
                consoleHost.WriteError($"{input}: no timestamps (pass --utc)");
                return 1;
            }
            var limbOptions = PlanetaryLimbFit.OptionsFor(PhysicalEphemeris.Compute(planet, when));
            var (width, height) = (stream.Width, stream.Height);
            float[]? truth = null;
            if (truthPlane is not null && truthDisk is { } td)
            {
                truthDisk = td with { AxisRatio = limbOptions.AxisRatio };
                truth = PlanetaryMetrics.Normalise(truthPlane, width, height, truthDisk.Value);
            }

            var keeps = ParseList(parseResult.GetValue(keepOpt), double.Parse);
            var presets = ParseList(parseResult.GetValue(sharpenOpt), name => name);
            var methods = ParseList(parseResult.GetValue(methodOpt), name => name.ToLowerInvariant());
            if (methods.FirstOrDefault(m => m is not ("ap" or "ap-flat" or "global")) is { } unknown)
            {
                consoleHost.WriteError($"--method {unknown}: ap, ap-flat or global");
                return 1;
            }
            var spacings = ParseList(parseResult.GetValue(spacingOpt), int.Parse);
            var correlations = ParseList(parseResult.GetValue(correlationOpt), name => name.ToLowerInvariant());
            var pools = ParseList(parseResult.GetValue(poolOpt), text => double.Parse(text, CultureInfo.InvariantCulture));
            var geometries = ParseList(parseResult.GetValue(geometryOpt), name => name.ToLowerInvariant());
            if (geometries.FirstOrDefault(g => g is not ("reference" or "median")) is { } unknownGeometry)
            {
                consoleHost.WriteError($"--geometry {unknownGeometry}: reference or median");
                return 1;
            }
            if (correlations.FirstOrDefault(c => c is not ("whitened" or "plain")) is { } unknownCorrelation)
            {
                consoleHost.WriteError($"--correlation {unknownCorrelation}: whitened or plain");
                return 1;
            }
            var interpolations = ParseList(parseResult.GetValue(interpolationOpt), name => name.ToLowerInvariant() switch
            {
                "bilinear" => (WarpInterpolation?)WarpInterpolation.Bilinear,
                "lanczos3" => WarpInterpolation.Lanczos3,
                "lanczos3-clamped" => WarpInterpolation.Lanczos3Clamped,
                _ => null,
            });
            if (interpolations.Any(i => i is null))
            {
                consoleHost.WriteError($"--interpolation {parseResult.GetValue(interpolationOpt)}: bilinear, lanczos3 or lanczos3-clamped");
                return 1;
            }
            var references = ParseList(parseResult.GetValue(referenceOpt), int.Parse);
            var derotations = ParseList(parseResult.GetValue(derotateOpt), name => name.ToLowerInvariant());
            if (derotations.FirstOrDefault(d => d is not ("none" or "frames")) is { } unknownDerotation)
            {
                consoleHost.WriteError($"--derotate {unknownDerotation}: none or frames");
                return 1;
            }
            // A de-rotated stack shows the planet at the truth's instant, which it is scored against, or at the capture's middle.
            PlanetaryDerotationOptions? DerotationFor(string derotate, DateTimeOffset? epoch)
                => derotate == "frames" ? new PlanetaryDerotationOptions(planet) { Epoch = epoch ?? stream.MidCapture } : null;
            var patch = parseResult.GetValue(patchOpt);
            var halves = !parseResult.GetValue(noHalvesOpt);
            var cutoff = parseResult.GetValue(cutoffOpt);
            var stacker = new LuckyImagingStacker();
            if (stream.Layout == PlanetaryFrameLayout.SplitCfa)
            {
                return await MeasureColourAsync();
            }
            MetricDisk? reference = truthDisk;
            var rows = new List<Row>();
            // A global stack has no alignment points, so it is stacked once, whatever the spacings.
            var candidates = references.SelectMany(r => interpolations.SelectMany(i => correlations.SelectMany(c => methods.SelectMany(m => m == "global"
                ? [(Method: m, Spacing: 0, Correlation: c, Pool: 0.0, Median: false, Interpolation: i ?? WarpInterpolation.Bilinear, Reference: r)]
                : spacings.SelectMany(s => pools.SelectMany(pool => geometries.Select(g => (Method: m, Spacing: s, Correlation: c, Pool: pool, Median: g == "median", Interpolation: i ?? WarpInterpolation.Bilinear, Reference: r))))))))
                .SelectMany(candidate => derotations.Select(d => (Candidate: candidate, Derotate: d))).ToArray();
            consoleHost.WriteScrollable($"{Path.GetFileName(input)}: {frames} frames, {candidates.Length * keeps.Length * presets.Length} candidates{(halves ? ", each with its two halves" : "")}");
            foreach (var ((method, spacing, correlation, pool, median, interpolation, referenceFrames), derotate) in candidates)
            {
                foreach (var preset in presets)
                {
                    foreach (var keep in keeps)
                    {
                        ct.ThrowIfCancellationRequested();
                        var options = new PlanetaryStackOptions
                        {
                            KeepFraction = keep,
                            Sharpen = SharpenFor(preset),
                            AlignmentPointSpacing = spacing > 0 ? spacing : new PlanetaryStackOptions().AlignmentPointSpacing,
                            AlignmentPatchSize = patch,
                            PerPointQualityWeighting = method == "ap",
                            WhitenedCorrelation = correlation == "whitened",
                            WarpPoolFrames = pool,
                            MeshNodeSpacing = parseResult.GetValue(meshOpt),
                            MeshInfluence = parseResult.GetValue(influenceOpt),
                            MedianGeometry = median,
                            Interpolation = interpolation,
                            ReferenceFrames = referenceFrames,
                            Derotation = DerotationFor(derotate, truthTime),
                        };
                        var name = (method == "global" ? "global" : $"{method} {spacing} px") + (correlation == "plain" ? ", plain" : "")
                            + (pool > 0 ? string.Create(CultureInfo.InvariantCulture, $", pooled {pool:0.#}") : "") + (median ? ", median" : "")
                            + interpolation switch { WarpInterpolation.Lanczos3 => ", Lanczos-3", WarpInterpolation.Lanczos3Clamped => ", Lanczos-3 clamped", _ => "" }
                            + (referenceFrames > 1 ? $", against a stack of {referenceFrames}" : "")
                            + (derotate == "frames" ? ", de-rotated" : "");
                        var full = await StackAsync(stacker, stream, options, method, ct);
                        // Every stack onto one disk: the truth's, or the first stack's own.
                        if (Register(full.Master, limbOptions, reference) is not { } fullPlane)
                        {
                            consoleHost.WriteError($"keep {keep}, {preset}, {name}: the stack's limb could not be fitted");
                            continue;
                        }
                        reference ??= fullPlane.Disk;
                        var agreement = ImmutableArray<BandAgreement>.Empty;
                        if (halves)
                        {
                            using var halfA = PlanetaryFrameSubset.Half(stream, 0);
                            using var halfB = PlanetaryFrameSubset.Half(stream, 1);
                            var a = await StackAsync(stacker, halfA, options, method, ct);
                            var b = await StackAsync(stacker, halfB, options, method, ct);
                            if (Register(a.Master, limbOptions, reference) is not { } aPlane || Register(b.Master, limbOptions, reference) is not { } bPlane)
                            {
                                consoleHost.WriteError($"keep {keep}, {preset}, {name}: a half's limb could not be fitted");
                                continue;
                            }
                            agreement = PlanetaryMetrics.SplitHalf(aPlane.Plane, bPlane.Plane, width, height, reference.Value);
                        }
                        var disk = reference.Value;
                        var row = new Row(keep, preset, name, full.FramesUsed,
                            truth is null ? [] : PlanetaryMetrics.Fidelity(fullPlane.Plane, truth, width, height, disk),
                            truth is null ? double.NaN : PlanetaryMetrics.LimbProfileError(fullPlane.Plane, truth, width, height, disk),
                            agreement,
                            PlanetaryMetrics.LimbUndershoot(fullPlane.Plane, width, height, disk),
                            cutoff is { } c ? PlanetaryMetrics.PowerAbove(fullPlane.Plane, width, height, disk, c) : double.NaN);
                        rows.Add(row);
                        WriteRow(row);
                    }
                }
            }
            if (truth is not null && halves && rows.Count >= 3)
            {
                WriteRanking(rows);
            }
            return 0;

            // A colour capture (R5a): its truths beside it, one per colour at every scale a stack is made at (planetary-degrade
            // writes them), and every stack scored a colour at a time against that colour's truth, a demosaiced stack's colours
            // and a Bayer drizzle's alike, each on the grid it made. No halves: R3 found their agreement sees only the noise.
            async Task<int> MeasureColourAsync()
            {
                var drizzles = ParseList(parseResult.GetValue(drizzleOpt), text => double.Parse(text, CultureInfo.InvariantCulture));
                var pixfrac = parseResult.GetValue(pixfracOpt);
                var (sensorWidth, sensorHeight) = (stream.Width * 2, stream.Height * 2);
                var colourTruths = new Dictionary<(int Channel, double Scale), (float[] Plane, MetricDisk Disk, int Width, int Height)>();
                DateTimeOffset? colourTruthTime = null;
                foreach (var scale in drizzles.Prepend(1.0).Distinct())
                {
                    foreach (var (channel, colour) in Colours)
                    {
                        var path = Path.ChangeExtension(input, scale == 1 ? $".truth.{colour}.fits" : string.Create(CultureInfo.InvariantCulture, $".truth.{colour}.x{scale:0.##}.fits"));
                        if (ReadTruth(path, consoleHost) is not { } read)
                        {
                            return 1;
                        }
                        var (w, h) = ((int)Math.Round(sensorWidth * scale), (int)Math.Round(sensorHeight * scale));
                        if (read.Plane.Length != w * h)
                        {
                            consoleHost.WriteError($"{path}: not the {w} x {h} a stack at {scale.ToString(CultureInfo.InvariantCulture)}x makes");
                            return 1;
                        }
                        var disk = read.Disk with { AxisRatio = limbOptions.AxisRatio };
                        colourTruths[(channel, scale)] = (PlanetaryMetrics.Normalise(read.Plane, w, h, disk), disk, w, h);
                        colourTruthTime ??= read.Time;
                    }
                }

                // Each way of stacking, demosaiced and drizzled at each scale; a drizzle warps by the points where its method has them.
                var colourCandidates = references.SelectMany(r => interpolations.SelectMany(i => correlations.SelectMany(c => methods.SelectMany(m =>
                    (m == "global" ? [0] : spacings).SelectMany(sp => drizzles.Select(d => (double?)d).Prepend(null).Select(d =>
                        (Method: m, Spacing: sp, Correlation: c, Interpolation: i ?? WarpInterpolation.Bilinear, Reference: r, Drizzle: d)))))))
                    // A drizzle resamples nothing, so it is stacked once, whatever the interpolations.
                    .Where(candidate => candidate.Drizzle is null || candidate.Interpolation == (interpolations[0] ?? WarpInterpolation.Bilinear))
                    .SelectMany(candidate => derotations.Select(d => (Candidate: candidate, Derotate: d))).ToArray();
                consoleHost.WriteScrollable($"{Path.GetFileName(input)}: {frames} frames of a colour capture, {colourCandidates.Length * keeps.Length * presets.Length} stacks, each scored a colour at a time");
                foreach (var ((method, spacing, correlation, interpolation, referenceFrames, drizzle), derotate) in colourCandidates)
                {
                    foreach (var preset in presets)
                    {
                        foreach (var keep in keeps)
                        {
                            ct.ThrowIfCancellationRequested();
                            var options = new PlanetaryStackOptions
                            {
                                KeepFraction = keep,
                                Sharpen = SharpenFor(preset),
                                AlignmentPointSpacing = spacing > 0 ? spacing : new PlanetaryStackOptions().AlignmentPointSpacing,
                                AlignmentPatchSize = patch,
                                PerPointQualityWeighting = method == "ap",
                                WhitenedCorrelation = correlation == "whitened",
                                MeshNodeSpacing = parseResult.GetValue(meshOpt),
                                MeshInfluence = parseResult.GetValue(influenceOpt),
                                Interpolation = interpolation,
                                ReferenceFrames = referenceFrames,
                                Drizzle = drizzle is { } d ? new PlanetaryDrizzleOptions((float)d, pixfrac, AlignmentPointMesh: method != "global") : null,
                                Derotation = DerotationFor(derotate, colourTruthTime),
                            };
                            var name = (method == "global" ? "global" : $"{method} {spacing} px") + (correlation == "plain" ? ", plain" : "")
                                + (drizzle is null ? interpolation switch { WarpInterpolation.Lanczos3 => ", Lanczos-3", WarpInterpolation.Lanczos3Clamped => ", Lanczos-3 clamped", _ => "" } : "")
                                + (referenceFrames > 1 ? $", against a stack of {referenceFrames}" : "")
                                + (drizzle is { } dz ? string.Create(CultureInfo.InvariantCulture, $", Bayer drizzle {dz:0.##}x, pixfrac {pixfrac:0.##}") : ", demosaiced")
                                + (derotate == "frames" ? ", de-rotated" : "");
                            var result = drizzle is null ? await StackAsync(stacker, stream, options, method, ct) : await stacker.StackDrizzleAsync(stream, options, ct);
                            try
                            {
                                Score(result.Master, drizzle ?? 1, keep, preset, name, result.FramesUsed);
                                // Every stack on the sensor grid, demosaiced or drizzled, at each finer drizzle's scale too, by Lanczos-3: a
                                // band is counted in the OUTPUT grid's pixels, so a drizzle at 1.5x is judged beside what the sensor grid
                                // carries on that grid, never beside the 1x stack's own bands.
                                foreach (var scale in (drizzle ?? 1) == 1 ? drizzles.Where(d => d > 1) : [])
                                {
                                    var upsampled = await UpsampleAsync(result.Master, scale, ct);
                                    try
                                    {
                                        Score(upsampled, scale, keep, preset, string.Create(CultureInfo.InvariantCulture, $"{name}, Lanczos-3 to {scale:0.##}x"), result.FramesUsed);
                                    }
                                    finally
                                    {
                                        upsampled.Release();
                                    }
                                }
                            }
                            finally
                            {
                                result.Master.Release();
                            }
                        }
                    }
                }
                return 0;

                // A colour master's channels, each onto its own colour's truth at the master's scale.
                void Score(Image master, double scale, double keep, string preset, string name, int framesUsed)
                {
                    foreach (var (channel, colour) in Colours)
                    {
                        var truthOne = colourTruths[(channel, scale)];
                        var plane = master.ChannelImage(channel);
                        try
                        {
                            if (plane.Width != truthOne.Width || plane.Height != truthOne.Height)
                            {
                                consoleHost.WriteError($"keep {keep}, {preset}, {name}, {colour}: a {plane.Width} x {plane.Height} stack against a {truthOne.Width} x {truthOne.Height} truth");
                                continue;
                            }
                            if (Register(plane, limbOptions, truthOne.Disk) is not { } fitted)
                            {
                                consoleHost.WriteError($"keep {keep}, {preset}, {name}, {colour}: the stack's limb could not be fitted");
                                continue;
                            }
                            WriteRow(new Row(keep, preset, $"{name}, {colour}", framesUsed,
                                PlanetaryMetrics.Fidelity(fitted.Plane, truthOne.Plane, truthOne.Width, truthOne.Height, truthOne.Disk),
                                PlanetaryMetrics.LimbProfileError(fitted.Plane, truthOne.Plane, truthOne.Width, truthOne.Height, truthOne.Disk),
                                [], PlanetaryMetrics.LimbUndershoot(fitted.Plane, truthOne.Width, truthOne.Height, truthOne.Disk), double.NaN));
                        }
                        finally
                        {
                            plane.Release();
                        }
                    }
                }
            }
        });
        return command;
    }

    /// <summary>
    /// A truth planetary-degrade wrote: its plane, the disk it was rendered with (DISKX, DISKY, DISKR, NORTHANG; round, the
    /// caller sets the axis ratio) and its time. Null, said on the console, when the file or its cards are missing.
    /// </summary>
    internal static (float[] Plane, MetricDisk Disk, DateTimeOffset? Time)? ReadTruth(string path, IConsoleHost consoleHost)
    {
        if (!Image.TryReadFitsFile(path, out var image))
        {
            consoleHost.WriteError($"{path}: not a readable FITS image");
            return null;
        }
        using var fits = Image.OpenFitsHeader(path);
        var header = fits.ReadFirstImageHduHeaderOnly()?.Header;
        if (header is null || !double.IsFinite(header.GetDoubleValue("DISKX", double.NaN)))
        {
            consoleHost.WriteError($"{path}: no DISKX, DISKY, DISKR in its header (planetary-degrade writes them)");
            return null;
        }
        // AXISRAT is written by planetary-lucky-frames, whose window is scored with the stack's own oblate disk; a degrade truth has none.
        var disk = new MetricDisk(header.GetDoubleValue("DISKX", double.NaN), header.GetDoubleValue("DISKY", double.NaN),
            header.GetDoubleValue("DISKR", double.NaN), header.GetDoubleValue("AXISRAT", 1), header.GetDoubleValue("NORTHANG", 0));
        return (image.GetChannelSpan(0).ToArray(), disk, PlanetaryGeometrySubCommands.ParseUtc(header.GetStringValue("DATE-OBS")));
    }

    // A colour master's channels and the name its truths carry.
    private static readonly (int Channel, string Colour)[] Colours = [(0, "r"), (1, "g"), (2, "b")];

    // One candidate's stack and its metrics.
    private sealed record Row(double Keep, string Preset, string Method, int FramesUsed, ImmutableArray<BandFidelity> Fidelity, double LimbError,
        ImmutableArray<BandAgreement> Halves, double Undershoot, double PowerPastCutoff);

    private static async Task<PlanetaryStackResult> StackAsync(LuckyImagingStacker stacker, IPlanetaryFrameStream stream, PlanetaryStackOptions options, string method, CancellationToken ct)
    {
        return method == "global" ? await stacker.StackGlobalAsync(stream, options, ct) : await stacker.StackAsync(stream, options, ct);
    }

    // A stack's plane normalised on its own fitted disk and moved onto `onto`'s centre (or left where it is), with that disk.
    internal static (float[] Plane, MetricDisk Disk)? Register(Image master, LimbFitOptions options, MetricDisk? onto)
    {
        if (PlanetaryLimbFit.Fit(master, options) is not { } fit)
        {
            return null;
        }
        var (width, height) = (master.Width, master.Height);
        var own = MetricDisk.From(fit, options.AxisRatio);
        var plane = PlanetaryMetrics.Normalise(master.GetChannelSpan(0), width, height, own);
        return onto is { } target
            ? (PlanetaryMetrics.Shift(plane, width, height, target.X - own.X, target.Y - own.Y), target)
            : (plane, own);
    }

    // A master resampled by Lanczos-3 to `scale` times its grid, pixel centres to pixel centres as a drizzle's truth is rendered
    // ((x + 0.5) u - 0.5); the edge the kernel cannot reach reads zero, as a drizzle's uncovered cells do.
    private static async Task<Image> UpsampleAsync(Image master, double scale, CancellationToken ct)
    {
        var (width, height) = ((int)Math.Round(master.Width * scale), (int)Math.Round(master.Height * scale));
        var u = (float)scale;
        var transform = Matrix3x2.CreateTranslation(0.5f, 0.5f) * Matrix3x2.CreateScale(u) * Matrix3x2.CreateTranslation(-0.5f, -0.5f);
        var upsampled = await master.WarpToReferenceGridAsync(transform, width, height, WarpInterpolation.Lanczos3, ct);
        var planes = new float[upsampled.ChannelCount][,];
        for (var c = 0; c < planes.Length; c++)
        {
            var plane = new float[height, width];
            var flat = MemoryMarshal.CreateSpan(ref plane[0, 0], plane.Length);
            upsampled.GetChannelSpan(c).CopyTo(flat);
            for (var i = 0; i < flat.Length; i++)
            {
                if (float.IsNaN(flat[i]))
                {
                    flat[i] = 0f;
                }
            }
            planes[c] = plane;
        }
        upsampled.Release();
        return new Image(planes, BitDepth.Float32, master.MaxValue, master.MinValue, 0f, master.ImageMeta);
    }

    private static WaveletSharpenOptions? SharpenFor(string preset)
    {
        return preset.ToLowerInvariant() switch
        {
            "none" => null,
            "bandpass" => WaveletSharpenOptions.Bandpass,
            "combo" => WaveletSharpenOptions.Combo,
            _ => WaveletSharpenOptions.PlanetaryDefault,
        };
    }

    private static T[] ParseList<T>(string? text, Func<string, T> parse)
    {
        return [.. (text ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(item => parse(item.ToString(CultureInfo.InvariantCulture)))];
    }

    private void WriteRow(Row row)
    {
        var inv = CultureInfo.InvariantCulture;
        consoleHost.WriteScrollable(string.Create(inv, $"keep {row.Keep:0.###}, {row.Preset}, {row.Method}: {row.FramesUsed} frames"));
        if (!row.Fidelity.IsDefaultOrEmpty)
        {
            consoleHost.WriteScrollable("    fidelity (transfer / error) by band: " + string.Join("  ", row.Fidelity.Select(f => string.Create(inv, $"{f.Band}: {f.Transfer:0.000} / {f.Error:0.000}"))));
            consoleHost.WriteScrollable(string.Create(inv, $"    limb profile against the truth: {row.LimbError:0.0000} RMS"));
        }
        if (!row.Halves.IsDefaultOrEmpty)
        {
            consoleHost.WriteScrollable("    halves' correlation by band: " + string.Join("  ", row.Halves.Select(h => string.Create(inv, $"{h.Band}: {h.Correlation:0.000}"))));
        }
        consoleHost.WriteScrollable(string.Create(inv, $"    limb undershoot: {row.Undershoot:0.0000} of the disk{(double.IsFinite(row.PowerPastCutoff) ? $"; power past the cutoff: {row.PowerPastCutoff:E2}" : "")}"));
    }

    // How each truth-free metric ranks the candidates against its truth-based counterpart, each signed so agreement is positive.
    private void WriteRanking(List<Row> rows)
    {
        var inv = CultureInfo.InvariantCulture;
        consoleHost.WriteScrollable($"ranking over {rows.Count} candidates (Spearman; the plan pre-registers at least 0.8):");
        for (var j = 0; j < PlanetaryMetrics.Bands; j++)
        {
            var truthFree = rows.Select(r => r.Halves[j].Correlation).ToArray();
            var truthBased = rows.Select(r => -r.Fidelity[j].Error).ToArray();
            var rho = StatisticsHelper.Spearman(truthFree, truthBased);
            consoleHost.WriteScrollable(string.Create(inv, $"    band {j + 1}: the halves' correlation against the fidelity error: {rho:+0.00;-0.00}{(rho >= 0.8 ? "" : "  BELOW 0.8")}"));
        }
        var undershoot = rows.Select(r => -r.Undershoot).ToArray();
        var limb = rows.Select(r => -r.LimbError).ToArray();
        var limbRho = StatisticsHelper.Spearman(undershoot, limb);
        consoleHost.WriteScrollable(string.Create(inv, $"    limb: the undershoot against the limb's profile error: {limbRho:+0.00;-0.00}{(limbRho >= 0.8 ? "" : "  BELOW 0.8")}"));
    }
}
