using System;
using System.Collections.Generic;
using System.CommandLine;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Console.Lib;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Planetary;
using TianWen.Lib.Imaging.Stacking;
using TianWen.Lib.Stat;

namespace TianWen.Cli;

/// <summary>
/// <c>tianwen planetary-sharpen &lt;master.fits&gt;</c>: a planetary master sharpened again without stacking it again, as
/// <c>planetary-stack</c> sharpens it (<see cref="PlanetarySharpening"/>: gains derived through the limb's edge given the telescope, the
/// limb kept from ringing). With <c>--fix all</c> and a synthetic capture's <c>--truth</c> it is also how the enhanced pipeline's
/// sharpening was chosen (docs/plans/planetary-restoration.md).
/// </summary>
internal sealed class PlanetarySharpenSubCommand(IConsoleHost consoleHost, MasterPreviewRenderer previewRenderer)
{
    public Command Build()
    {
        var masterArg = new Argument<string>("master") { Description = "A linear planetary master (planetary-stack's master_*.fits)." };
        var planetOpt = new Option<string?>("--planet") { Description = "jupiter or saturn; read off the file's name when not given." };
        var utcOpt = new Option<string?>("--utc") { Description = "The instant the master shows the planet at (ISO 8601, UTC); its own DATE-OBS and EXPTIME's middle when not given, else the truth's." };
        var wavelengthOpt = new Option<string?>("--wavelength") { Description = "The filter's effective wavelength, nm, a comma list for a colour master's channels (550 when not given)." };
        var fixOpt = new Option<string>("--fix") { Description = "How the limb is kept from ringing: modelfeathered (the default: outside the limb the planet's model through the pupil, feathered to the stack far out, #1171), bounded (never brighter than the stack outside the limb but for its moons), floored, limb (the limb as its own channel), feathered, plain, heldoutside, modelfloor, blended, modelglow, modeloutside, glowswapped or modelfeathered (#1171's candidates against bounded's trough), all to compare the first five, or outside to compare bounded with #1171's.", DefaultValueFactory = _ => "modelfeathered" };
        var truthOpt = new Option<string?>("--truth") { Description = "A synthetic capture's truth (planetary-degrade's .truth.fits): every sharpening scored against it." };
        var outputOpt = new Option<string?>("--output", "-o") { Description = "Where the sharpened masters go (master_*_sharpened[_fix].fits); the master's folder when not given." };
        var noWriteOpt = new Option<bool>("--no-write") { Description = "Score only, write nothing." };
        var stackedPreviewOpt = new Option<bool>("--stacked-preview") { Description = "Also write the master as stacked through the same high-key planetary preview as the sharpened one (<name>_stacked.png), so the two are seen through one renderer: planetary-stack's own preview is of the sharpened master." };
        var fitOpt = new Option<string>("--fit") { Description = "How the gains are fitted: free, nonnegative (their composite through the kernel held at or above zero), or both to compare them.", DefaultValueFactory = _ => "free" };
        var finestOpt = new Option<string>("--colour-finest") { Description = "A colour master's finest band (#1187): held (as stacked on every colour, the default), derived (its derived gain), heldbutgreen (as stacked on red and blue), or all to compare them.", DefaultValueFactory = _ => "held" };
        var ringEdgeOpt = new Option<bool>("--ring-edge") { Description = "Read Saturn's edge off its rings' outer rim as well as its polar limb (#1256)." };
        var edgeReachOpt = new Option<double?>("--edge-reach") { Description = "Take the kernel as the physical one fitted to the limb's edge from 0.02 cycles a pixel to this, carried by its physics to the cutoff, rather than the edge as read at every frequency." };
        var slidersOpt = new Option<bool>("--sliders") { Description = "Also sharpen as a live view's wavelet sliders do once a derivation seeds them (the same gains over the whole master, no denoise, held at its darkest level), then drawn outside the limb by the limb the derivation keeps (#1201), and score both: whether the live view reaches the derived sharpening. Says how far the live drawing lies from this sharpening outside the limb, and what the drawing costs beside the sliders' wavelet pass." };
        var finishOpt = new Option<string>("--finish") { Description = "A finishing step after the derived sharpening (#1279): none (the default), cutoff (a low-pass at the pupil's diffraction cutoff), adaptive (the change from the stack weighted by the stack's local contrast, at matched noise), kolivas (Kolivas's own damped Richardson-Lucy step, a reference), wiener (Kolivas's FFT denoise: a smooth low-pass fitted to the sharpened window's Wiener target), joined by + (adaptive+cutoff); a comma list sharpens with each, each written and scored.", DefaultValueFactory = _ => "none" };
        var kolivasAmountOpt = new Option<double>("--kolivas-amount") { Description = "The amount --finish kolivas takes, as his tool's slider (15.6, his PlanetRecon's judging recipe).", DefaultValueFactory = _ => 15.6 };
        var colourOpt = new Option<string>("--colour") { Description = "How a colour master's detail is sharpened (#1295): perchannel (the default: each channel through its own diffraction), luminance (the mean of the planes sharpened once, every plane given the stack's own colour), or both to compare them.", DefaultValueFactory = _ => "perchannel" };
        var strengthOpt = new Option<string>("--strength") { Description = "How far past the truth bands 2 and 3 are taken (#1251): 1, the default, is the derived sharpening; a comma list sharpens at each (e.g. '1,1.5,2'), each written and scored.", DefaultValueFactory = _ => "1" };
        var pupil = PlanetaryMasterScore.PupilOptions();

        var command = new Command("planetary-sharpen", "Sharpen a planetary master again, by gains derived through the limb's edge (R8), the limb kept from ringing.")
        {
            Arguments = { masterArg },
            Options = { planetOpt, utcOpt, wavelengthOpt, fixOpt, fitOpt, finestOpt, colourOpt, strengthOpt, edgeReachOpt, ringEdgeOpt, slidersOpt, truthOpt, outputOpt, noWriteOpt, stackedPreviewOpt, pupil.ApertureMm, pupil.Obstruction, pupil.Telescope, finishOpt, kolivasAmountOpt },
        };

        command.SetAction(async (parseResult, ct) =>
        {
            var inv = CultureInfo.InvariantCulture;
            var path = parseResult.GetValue(masterArg) ?? "";
            if (!Image.TryReadFitsFile(path, out var master))
            {
                consoleHost.WriteError($"{path}: not a readable FITS image");
                return 1;
            }
            try
            {
                var planetName = parseResult.GetValue(planetOpt)?.ToLowerInvariant();
                CatalogIndex? planet = planetName switch
                {
                    null => PlanetaryCaptureName.Planet(path),
                    "jupiter" => CatalogIndex.Jupiter,
                    "saturn" => CatalogIndex.Saturn,
                    _ => null,
                };
                if (planet is not { } body)
                {
                    consoleHost.WriteError($"{path}: name the planet (--planet jupiter or saturn)");
                    return 1;
                }
                var truthPath = parseResult.GetValue(truthOpt);
                var meta = master.ImageMeta;
                DateTimeOffset? when = PlanetaryGeometrySubCommands.ParseUtc(parseResult.GetValue(utcOpt))
                    ?? (meta.ExposureStartTime.Year > 1 ? meta.ExposureStartTime + (meta.ExposureDuration / 2) : (DateTimeOffset?)null)
                    ?? (truthPath is not null ? PlanetaryMeasureSubCommand.ReadTruth(colourTruth(truthPath, master), consoleHost)?.Time : null);
                if (when is not { } instant)
                {
                    consoleHost.WriteError($"{path}: no time in its header; give --utc");
                    return 1;
                }
                if (PlanetaryMasterScore.Wavelengths(consoleHost, parseResult.GetValue(wavelengthOpt)) is not { } wavelengths)
                {
                    return 1;
                }
                var fixName = (parseResult.GetValue(fixOpt) ?? "modelfeathered").ToLowerInvariant();
                PlanetaryLimbFix[] fixes = fixName switch
                {
                    "all" => [PlanetaryLimbFix.Plain, PlanetaryLimbFix.Floored, PlanetaryLimbFix.LimbChannel, PlanetaryLimbFix.Feathered, PlanetaryLimbFix.Bounded],
                    "plain" => [PlanetaryLimbFix.Plain],
                    "floored" => [PlanetaryLimbFix.Floored],
                    "bounded" => [PlanetaryLimbFix.Bounded],
                    "feathered" => [PlanetaryLimbFix.Feathered],
                    "limb" => [PlanetaryLimbFix.LimbChannel],
                    "heldoutside" => [PlanetaryLimbFix.HeldOutside],
                    "modelfloor" => [PlanetaryLimbFix.ModelFloor],
                    "blended" => [PlanetaryLimbFix.Blended],
                    "modelglow" => [PlanetaryLimbFix.ModelGlow],
                    "modeloutside" => [PlanetaryLimbFix.ModelOutside],
                    "glowswapped" => [PlanetaryLimbFix.GlowSwapped],
                    "modelfeathered" => [PlanetaryLimbFix.ModelFeathered],
                    // #1171's candidates against the trough bounded leaves at the limb.
                    "outside" => [PlanetaryLimbFix.Bounded, PlanetaryLimbFix.HeldOutside, PlanetaryLimbFix.ModelFloor, PlanetaryLimbFix.Blended, PlanetaryLimbFix.ModelGlow, PlanetaryLimbFix.ModelOutside, PlanetaryLimbFix.GlowSwapped, PlanetaryLimbFix.ModelFeathered],
                    _ => [],
                };
                if (fixes.Length == 0)
                {
                    consoleHost.WriteError($"--fix {fixName}: floored, bounded, limb, feathered, plain or all");
                    return 1;
                }
                var options = new PlanetarySharpenOptions(body, instant, PlanetaryMasterScore.PupilFrom(parseResult, pupil)) { WavelengthsNm = [.. wavelengths] };
                consoleHost.WriteScrollable(string.Create(inv,
                    $"{Path.GetFileName(path)}: {master.ChannelCount}ch {master.Width}x{master.Height}, {body} at {instant:yyyy-MM-dd HH:mm:ss} UTC, {(options.Pupil is { } p ? $"a {p.DiameterM * 1000:0} mm pupil {p.ObstructionRatio:P0} obstructed" : "no telescope (the preset, the limb kept as stacked)")}"));
                if (truthPath is not null)
                {
                    PlanetaryMasterScore.AgainstTruth(consoleHost, master, truthPath, body, "the master as stacked");
                    PlanetaryMasterScore.TruthGains(consoleHost, master, truthPath, body);
                }
                else
                {
                    PlanetaryMasterScore.Undershoot(consoleHost, master, body, instant, "the master as stacked");
                }

                var fitName = (parseResult.GetValue(fitOpt) ?? "free").ToLowerInvariant();
                bool[] fits = fitName switch
                {
                    "both" => [false, true],
                    "free" => [false],
                    "nonnegative" => [true],
                    _ => [],
                };
                if (fits.Length == 0)
                {
                    consoleHost.WriteError($"--fit {fitName}: free, nonnegative or both");
                    return 1;
                }
                var finestName = (parseResult.GetValue(finestOpt) ?? "held").ToLowerInvariant();
                PlanetaryColourFinestBand[] finests = finestName switch
                {
                    "all" => [PlanetaryColourFinestBand.Derived, PlanetaryColourFinestBand.Held, PlanetaryColourFinestBand.HeldButGreen],
                    "derived" => [PlanetaryColourFinestBand.Derived],
                    "held" => [PlanetaryColourFinestBand.Held],
                    "heldbutgreen" => [PlanetaryColourFinestBand.HeldButGreen],
                    _ => [],
                };
                if (finests.Length == 0)
                {
                    consoleHost.WriteError($"--colour-finest {finestName}: derived, held, heldbutgreen or all");
                    return 1;
                }
                var colourName = (parseResult.GetValue(colourOpt) ?? "perchannel").ToLowerInvariant();
                bool[] luminances = colourName switch
                {
                    "both" => [false, true],
                    "perchannel" => [false],
                    "luminance" => [true],
                    _ => [],
                };
                if (luminances.Length == 0)
                {
                    consoleHost.WriteError($"--colour {colourName}: perchannel, luminance or both");
                    return 1;
                }
                var strengths = new List<double>();
                foreach (var word in (parseResult.GetValue(strengthOpt) ?? "1").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (!double.TryParse(word, NumberStyles.Float, inv, out var strength) || strength <= 0)
                    {
                        consoleHost.WriteError($"--strength {word}: a positive number, or a comma list of them");
                        return 1;
                    }
                    strengths.Add(strength);
                }
                var finishes = new List<(PlanetaryFinish Finish, string Word)>();
                foreach (var word in (parseResult.GetValue(finishOpt) ?? "none").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    var finish = PlanetaryFinish.None;
                    foreach (var part in word.ToLowerInvariant().Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        PlanetaryFinish? step = part switch
                        {
                            "none" => PlanetaryFinish.None,
                            "cutoff" => PlanetaryFinish.Cutoff,
                            "adaptive" => PlanetaryFinish.Adaptive,
                            "kolivas" => PlanetaryFinish.Kolivas,
                            "wiener" => PlanetaryFinish.Wiener,
                            _ => null,
                        };
                        if (step is not { } known)
                        {
                            consoleHost.WriteError($"--finish {word}: none, cutoff, adaptive, kolivas or wiener, joined by + and listed by commas");
                            return 1;
                        }
                        finish |= known;
                    }
                    finishes.Add((finish, word.ToLowerInvariant()));
                }
                var kolivasAmount = parseResult.GetValue(kolivasAmountOpt);
                var outputDir = parseResult.GetValue(outputOpt) ?? Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".";
                var variants = (options.Pupil is null ? [PlanetaryLimbFix.LimbChannel] : fixes)
                    .SelectMany(f => fits.SelectMany(n => finests.SelectMany(b => strengths.SelectMany(k => finishes.SelectMany(e => luminances.Select(l => (Fix: f, NonNegative: n, Finest: b, Strength: k, Finish: e, Luminance: l))))))).ToArray();
                foreach (var (fix, nonNegative, finest, strength, (finish, finishWord), luminance) in variants)
                {
                    ct.ThrowIfCancellationRequested();
                    if (PlanetarySharpening.Sharpen(master, options with { Fix = fix, NonNegative = nonNegative, ColourFinestBand = finest, Strength = strength, EdgeReach = parseResult.GetValue(edgeReachOpt), RingEdge = parseResult.GetValue(ringEdgeOpt), Finish = finish, KolivasAmount = kolivasAmount, LuminanceOnly = luminance }) is not { } result)
                    {
                        consoleHost.WriteError($"{path}: the planet's limb could not be fitted");
                        return 1;
                    }
                    try
                    {
                        var stem = Path.Combine(outputDir, Path.GetFileNameWithoutExtension(path) + "_sharpened"
                            + (variants.Length > strengths.Count * finishes.Count * luminances.Length ? "_" + fix.ToString().ToLowerInvariant() + (nonNegative ? "_nonnegative" : "") + (finests.Length > 1 ? "_" + finest.ToString().ToLowerInvariant() : "") : "")
                            + (strengths.Count > 1 ? string.Create(inv, $"_s{strength:0.##}") : "")
                            + (finishes.Count > 1 ? "_f" + finishWord.Replace('+', '-') : "")
                            + (luminances.Length > 1 ? (luminance ? "_luminance" : "_perchannel") : ""));
                        var finestWords = finests.Length > 1 ? $", the colour's finest band {finest.ToString().ToLowerInvariant()}" : "";
                        var strengthWords = (strength == 1 ? "" : string.Create(inv, $", strength {strength:0.##}"))
                            + (finish == PlanetaryFinish.None ? "" : $", finished {finishWord}")
                            + (luminance && master.ChannelCount == 3 ? ", the luminance sharpened, the stack's colour kept" : "");
                        var what = result.Derived ? $"derived{(nonNegative ? " non-negative" : "")}, {PlanetaryBestStack.Describe(fix)}{finestWords}{strengthWords}" : $"PlanetaryDefault{strengthWords}, the limb kept as stacked";
                        consoleHost.WriteScrollable(string.Create(inv,
                            $"[planetary] {what}: gains {string.Join(", ", result.Gains.Select(g => g.ToString("0.00", inv)))}; the limb's edge at 0.1 and 0.3 cycles a pixel {result.EdgeAtTenth:0.000}, {result.EdgeAtThreeTenths:0.000}"));
                        if (!result.Gains.IsDefaultOrEmpty)
                        {
                            consoleHost.WriteScrollable($"[planetary] {what}: {PlanetaryMasterScore.FilterWords(result.Gains.AsSpan())}");
                        }
                        if (!result.Cutoffs.IsDefaultOrEmpty)
                        {
                            var sampled = result.Cutoffs.Max() >= 0.5 ? "; at or past 0.5 the master is sampled coarser than the optics resolve, and nothing lies past the cutoff" : "";
                            consoleHost.WriteScrollable(string.Create(inv,
                                $"[planetary] the pupil's cutoff, each channel: {string.Join(", ", result.Cutoffs.Select(f => f.ToString("0.000", inv)))} cycles a pixel{sampled}"));
                        }
                        if (!result.WienerCuts.IsDefaultOrEmpty)
                        {
                            consoleHost.WriteScrollable(string.Create(inv,
                                $"[planetary] {what}: the fitted Wiener low-pass, each channel, from and to: {string.Join("; ", result.WienerCuts.Select(w => string.Create(inv, $"{w.From:0.00} to {w.To:0.00}")))} cycles a pixel"));
                        }
                        if (truthPath is not null)
                        {
                            PlanetaryMasterScore.AgainstTruth(consoleHost, result.Sharpened, truthPath, body, what, master);
                        }
                        else
                        {
                            PlanetaryMasterScore.Undershoot(consoleHost, result.Sharpened, body, instant, what, master);
                        }
                        if (parseResult.GetValue(slidersOpt) && result.Derived && !result.Gains.IsDefaultOrEmpty)
                        {
                            // The live view's path: one set of gains over the whole master, then the limb the derivation kept drawn outside
                            // it, followed to the master first as every live master is (#1201).
                            var sliderOptions = PlanetaryBestStack.SliderOptions([.. result.Gains.Select(g => (float)g)]);
                            var slid = WaveletSharpen.Sharpen(master, sliderOptions);
                            // Timed warm, the median of five, as a live view runs them master after master.
                            var waveletMs = MedianMs(() => WaveletSharpen.Sharpen(master, sliderOptions).Release());
                            Image? drawn = null;
                            try
                            {
                                const string sliders = "the live view's sliders, the same gains held at the darkest";
                                const string live = "the live view's sliders, the kept limb drawn outside them";
                                if (result.Limb is { } kept)
                                {
                                    var followed = kept.FollowedTo(master);
                                    drawn = followed?.Draw(master, slid);
                                    var drawMs = MedianMs(() => kept.FollowedTo(master)?.Draw(master, slid).Release());
                                    consoleHost.WriteScrollable(string.Create(inv,
                                        $"[planetary] the sliders' wavelet pass {waveletMs:0.0} ms; the kept limb followed{(ReferenceEquals(followed, kept) ? " (unmoved)" : followed is null ? " (lost)" : " (moved)")} and drawn {drawMs:0.0} ms"));
                                    if (drawn is not null)
                                    {
                                        consoleHost.WriteScrollable(string.Create(inv,
                                            $"[planetary] outside the limb the live drawing is {LargestOutsideTheLimb(result.Sharpened, drawn, kept.Disk):E2} of the disk's level from this {PlanetaryBestStack.Describe(fix)} sharpening (the sliders alone {LargestOutsideTheLimb(result.Sharpened, slid, kept.Disk):0.0000})"));
                                    }
                                }
                                foreach (var (image, label, suffix) in new[] { (slid, sliders, "_sliders"), (drawn, live, "_live") })
                                {
                                    if (image is null)
                                    {
                                        continue;
                                    }
                                    if (truthPath is not null)
                                    {
                                        PlanetaryMasterScore.AgainstTruth(consoleHost, image, truthPath, body, label);
                                    }
                                    else
                                    {
                                        PlanetaryMasterScore.Undershoot(consoleHost, image, body, instant, label);
                                    }
                                    if (!parseResult.GetValue(noWriteOpt))
                                    {
                                        Directory.CreateDirectory(outputDir);
                                        image.WriteToFitsFile(stem + suffix + ".fits");
                                        consoleHost.WriteScrollable($"[planetary] wrote {Path.GetFileName(stem + suffix + ".fits")}, {label}");
                                    }
                                }
                            }
                            finally
                            {
                                slid.Release();
                                drawn?.Release();
                            }
                        }
                        if (!parseResult.GetValue(noWriteOpt))
                        {
                            Directory.CreateDirectory(outputDir);
                            var file = stem + ".fits";
                            result.Sharpened.WriteToFitsFile(file);
                            var png = Path.ChangeExtension(file, ".png");
                            await previewRenderer.RenderPlanetaryAsync(result.Sharpened, png, gamma: 0.75, ct: ct);
                            consoleHost.WriteScrollable($"[planetary] wrote {Path.GetFileName(file)} and its high-key preview {Path.GetFileName(png)}");
                            if (parseResult.GetValue(stackedPreviewOpt))
                            {
                                var stacked = Path.Combine(outputDir, Path.GetFileNameWithoutExtension(path) + "_stacked.png");
                                await previewRenderer.RenderPlanetaryAsync(master, stacked, gamma: 0.75, ct: ct);
                                consoleHost.WriteScrollable($"[planetary] wrote {Path.GetFileName(stacked)}, the master as stacked through the same preview");
                            }
                        }
                    }
                    finally
                    {
                        result.Sharpened.Release();
                    }
                }
                return 0;
            }
            finally
            {
                master.Release();
            }
        });
        return command;

        // A colour master's truths are its colours'; the red one carries the time.
        static string colourTruth(string truthPath, Image master) => master.ChannelCount == 3 ? Path.ChangeExtension(truthPath, ".r.fits") : truthPath;
    }

    // The largest difference between two masters past the disk's limb, over every channel, in each channel's disk level above its sky
    // (the first master's): how far a live drawing lies from the batch's where neither sharpens (#1201).
    private static double LargestOutsideTheLimb(Image a, Image b, MetricDisk disk)
    {
        double largest = 0;
        for (var c = 0; c < a.ChannelCount; c++)
        {
            var planeA = a.GetChannelSpan(c);
            var planeB = b.GetChannelSpan(c);
            var (_, scale) = PlanetaryMetrics.NormalisationLevels(planeA, a.Width, a.Height, disk);
            for (var y = 0; y < a.Height; y++)
            {
                for (var x = 0; x < a.Width; x++)
                {
                    if (disk.RadiiAt(x, y) > 1)
                    {
                        largest = Math.Max(largest, Math.Abs(planeA[(y * a.Width) + x] - planeB[(y * a.Width) + x]) / scale);
                    }
                }
            }
        }
        return largest;
    }

    // The median of five timed runs of `run`, after one untimed (the JIT's).
    private static double MedianMs(Action run)
    {
        run();
        var times = new double[5];
        for (var i = 0; i < times.Length; i++)
        {
            var started = Stopwatch.GetTimestamp();
            run();
            times[i] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        }
        return StatisticsHelper.NthSmallest(times, times.Length / 2);
    }
}
