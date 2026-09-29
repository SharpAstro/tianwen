using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.CommandLine;
using System.Globalization;
using System.IO;
using System.Linq;
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
        var captureArg = new Argument<string>("capture") { Description = "A mono SER capture of a planet." };
        var truthOpt = new Option<string?>("--truth") { Description = "The truth to score the stacks against (planetary-degrade writes it beside a synthetic capture); without it, only the truth-free metrics." };
        var planetOpt = new Option<string>("--planet") { Description = "jupiter or saturn.", DefaultValueFactory = _ => "jupiter" };
        var utcOpt = new Option<string?>("--utc") { Description = "The capture's time (ISO 8601, UTC), for a SER without timestamps and no truth." };
        var framesOpt = new Option<int?>("--frames") { Description = "Only the capture's first frames." };
        var keepOpt = new Option<string>("--keep") { Description = "The keep fractions to stack at, a comma list.", DefaultValueFactory = _ => "0.02,0.05,0.2,0.5" };
        var sharpenOpt = new Option<string>("--sharpen") { Description = "The sharpening presets to stack with, a comma list of none, default, bandpass and combo.", DefaultValueFactory = _ => "none,default,combo" };
        var methodOpt = new Option<string>("--method") { Description = "ap (alignment points, the full lucky-imaging path) or global.", DefaultValueFactory = _ => "ap" };
        var cutoffOpt = new Option<double?>("--cutoff") { Description = "The telescope's cutoff in cycles a pixel, for the power past it (fabrication); none past Nyquist." };

        var command = new Command("planetary-measure",
            "Stacks a capture as each candidate asks, and its two halves, and measures every stack (R3): fidelity per wavelet band and the limb against a truth, the halves' agreement per band, the limb's undershoot; with a truth, how the truth-free metrics rank the candidates against the truth-based ones.")
        {
            Arguments = { captureArg },
            Options = { truthOpt, planetOpt, utcOpt, framesOpt, keepOpt, sharpenOpt, methodOpt, cutoffOpt },
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
                if (!Image.TryReadFitsFile(truthPath, out var truthImage))
                {
                    consoleHost.WriteError($"{truthPath}: not a readable FITS image");
                    return 1;
                }
                using (var fits = Image.OpenFitsHeader(truthPath))
                {
                    var header = fits.ReadFirstImageHduHeaderOnly()?.Header;
                    if (header is null || !double.IsFinite(header.GetDoubleValue("DISKX", double.NaN)))
                    {
                        consoleHost.WriteError($"{truthPath}: no DISKX, DISKY, DISKR in its header (planetary-degrade writes them)");
                        return 1;
                    }
                    truthTime = PlanetaryGeometrySubCommands.ParseUtc(header.GetStringValue("DATE-OBS"));
                    truthDisk = new MetricDisk(header.GetDoubleValue("DISKX", double.NaN), header.GetDoubleValue("DISKY", double.NaN),
                        header.GetDoubleValue("DISKR", double.NaN), 1, header.GetDoubleValue("NORTHANG", 0));
                }
                truthPlane = truthImage.GetChannelSpan(0).ToArray();
            }
            if ((truthTime ?? PlanetaryGeometrySubCommands.MidCapture(stream) ?? PlanetaryGeometrySubCommands.ParseUtc(parseResult.GetValue(utcOpt))) is not { } when)
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
            var global = parseResult.GetValue(methodOpt)?.ToLowerInvariant() == "global";
            var cutoff = parseResult.GetValue(cutoffOpt);
            var stacker = new LuckyImagingStacker();
            MetricDisk? reference = truthDisk;
            var rows = new List<Row>();
            consoleHost.WriteScrollable($"{Path.GetFileName(input)}: {frames} frames, {keeps.Length * presets.Length} candidates by {(global ? "global" : "alignment-point")} stacking, each with its two halves");
            foreach (var preset in presets)
            {
                foreach (var keep in keeps)
                {
                    ct.ThrowIfCancellationRequested();
                    var options = new PlanetaryStackOptions { KeepFraction = keep, Sharpen = SharpenFor(preset) };
                    var full = await StackAsync(stacker, stream, options, global, ct);
                    using var halfA = PlanetaryFrameSubset.Half(stream, 0);
                    using var halfB = PlanetaryFrameSubset.Half(stream, 1);
                    var a = await StackAsync(stacker, halfA, options, global, ct);
                    var b = await StackAsync(stacker, halfB, options, global, ct);
                    // Every stack onto one disk: the truth's, or the first stack's own.
                    if (Register(full.Master, limbOptions, reference) is not { } fullPlane
                        || Register(a.Master, limbOptions, reference ??= fullPlane.Disk) is not { } aPlane
                        || Register(b.Master, limbOptions, reference) is not { } bPlane)
                    {
                        consoleHost.WriteError($"keep {keep}, {preset}: a stack's limb could not be fitted");
                        continue;
                    }
                    var disk = reference ?? fullPlane.Disk;
                    var row = new Row(keep, preset, full.FramesUsed,
                        truth is null ? [] : PlanetaryMetrics.Fidelity(fullPlane.Plane, truth, width, height, disk),
                        truth is null ? double.NaN : PlanetaryMetrics.LimbProfileError(fullPlane.Plane, truth, width, height, disk),
                        PlanetaryMetrics.SplitHalf(aPlane.Plane, bPlane.Plane, width, height, disk),
                        PlanetaryMetrics.LimbUndershoot(fullPlane.Plane, width, height, disk),
                        cutoff is { } c ? PlanetaryMetrics.PowerAbove(fullPlane.Plane, width, height, disk, c) : double.NaN);
                    rows.Add(row);
                    WriteRow(row);
                }
            }
            if (truth is not null && rows.Count >= 3)
            {
                WriteRanking(rows);
            }
            return 0;
        });
        return command;
    }

    // One candidate's stack and its metrics.
    private sealed record Row(double Keep, string Preset, int FramesUsed, ImmutableArray<BandFidelity> Fidelity, double LimbError,
        ImmutableArray<BandAgreement> Halves, double Undershoot, double PowerPastCutoff);

    private static async Task<PlanetaryStackResult> StackAsync(LuckyImagingStacker stacker, IPlanetaryFrameStream stream, PlanetaryStackOptions options, bool global, CancellationToken ct)
    {
        return global ? await stacker.StackGlobalAsync(stream, options, ct) : await stacker.StackAsync(stream, options, ct);
    }

    // A stack's plane normalised on its own fitted disk and moved onto `onto`'s centre (or left where it is), with that disk.
    private static (float[] Plane, MetricDisk Disk)? Register(Image master, LimbFitOptions options, MetricDisk? onto)
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
        consoleHost.WriteScrollable(string.Create(inv, $"keep {row.Keep:0.###}, {row.Preset}: {row.FramesUsed} frames"));
        if (!row.Fidelity.IsDefaultOrEmpty)
        {
            consoleHost.WriteScrollable("    fidelity (transfer / error) by band: " + string.Join("  ", row.Fidelity.Select(f => string.Create(inv, $"{f.Band}: {f.Transfer:0.000} / {f.Error:0.000}"))));
            consoleHost.WriteScrollable(string.Create(inv, $"    limb profile against the truth: {row.LimbError:0.0000} RMS"));
        }
        consoleHost.WriteScrollable("    halves' correlation by band: " + string.Join("  ", row.Halves.Select(h => string.Create(inv, $"{h.Band}: {h.Correlation:0.000}"))));
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
