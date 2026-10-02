using System;
using System.CommandLine;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Console.Lib;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Planetary;
using TianWen.Lib.Imaging.Stacking;

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
        var fixOpt = new Option<string>("--fix") { Description = "How the limb is kept from ringing: bounded (the default: floored, and never brighter than the stack outside the limb but for its moons), floored, limb (the limb as its own channel), feathered, plain, or all to compare them.", DefaultValueFactory = _ => "bounded" };
        var truthOpt = new Option<string?>("--truth") { Description = "A synthetic capture's truth (planetary-degrade's .truth.fits): every sharpening scored against it." };
        var outputOpt = new Option<string?>("--output", "-o") { Description = "Where the sharpened masters go (master_*_sharpened[_fix].fits); the master's folder when not given." };
        var noWriteOpt = new Option<bool>("--no-write") { Description = "Score only, write nothing." };
        var fitOpt = new Option<string>("--fit") { Description = "How the gains are fitted: free, nonnegative (their composite through the kernel held at or above zero), or both to compare them.", DefaultValueFactory = _ => "free" };
        var slidersOpt = new Option<bool>("--sliders") { Description = "Also sharpen as a live view's wavelet sliders do once a derivation seeds them (the same gains over the whole master, no denoise, held at its darkest level) and score that too: whether the live view reaches the derived sharpening." };
        var pupil = PlanetaryMasterScore.PupilOptions();

        var command = new Command("planetary-sharpen", "Sharpen a planetary master again, by gains derived through the limb's edge (R8), the limb kept from ringing.")
        {
            Arguments = { masterArg },
            Options = { planetOpt, utcOpt, wavelengthOpt, fixOpt, fitOpt, slidersOpt, truthOpt, outputOpt, noWriteOpt, pupil.ApertureMm, pupil.Obstruction, pupil.Telescope },
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
                var fixName = (parseResult.GetValue(fixOpt) ?? "bounded").ToLowerInvariant();
                PlanetaryLimbFix[] fixes = fixName switch
                {
                    "all" => [PlanetaryLimbFix.Plain, PlanetaryLimbFix.Floored, PlanetaryLimbFix.LimbChannel, PlanetaryLimbFix.Feathered, PlanetaryLimbFix.Bounded],
                    "plain" => [PlanetaryLimbFix.Plain],
                    "floored" => [PlanetaryLimbFix.Floored],
                    "bounded" => [PlanetaryLimbFix.Bounded],
                    "feathered" => [PlanetaryLimbFix.Feathered],
                    "limb" => [PlanetaryLimbFix.LimbChannel],
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
                var outputDir = parseResult.GetValue(outputOpt) ?? Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".";
                var variants = (options.Pupil is null ? [PlanetaryLimbFix.LimbChannel] : fixes).SelectMany(f => fits.Select(n => (Fix: f, NonNegative: n))).ToArray();
                foreach (var (fix, nonNegative) in variants)
                {
                    ct.ThrowIfCancellationRequested();
                    if (PlanetarySharpening.Sharpen(master, options with { Fix = fix, NonNegative = nonNegative }) is not { } result)
                    {
                        consoleHost.WriteError($"{path}: the planet's limb could not be fitted");
                        return 1;
                    }
                    try
                    {
                        var what = result.Derived ? $"derived{(nonNegative ? " non-negative" : "")}, {PlanetaryBestStack.Describe(fix)}" : "PlanetaryDefault, the limb kept as stacked";
                        consoleHost.WriteScrollable(string.Create(inv,
                            $"[planetary] {what}: gains {string.Join(", ", result.Gains.Select(g => g.ToString("0.00", inv)))}; the limb's edge at 0.1 and 0.3 cycles a pixel {result.EdgeAtTenth:0.000}, {result.EdgeAtThreeTenths:0.000}"));
                        if (truthPath is not null)
                        {
                            PlanetaryMasterScore.AgainstTruth(consoleHost, result.Sharpened, truthPath, body, what);
                        }
                        else
                        {
                            PlanetaryMasterScore.Undershoot(consoleHost, result.Sharpened, body, instant, what);
                        }
                        if (parseResult.GetValue(slidersOpt) && result.Derived && !result.Gains.IsDefaultOrEmpty)
                        {
                            // The live view's path: one set of gains over the whole master, no limb fit a master.
                            var slid = WaveletSharpen.Sharpen(master, PlanetaryBestStack.SliderOptions([.. result.Gains.Select(g => (float)g)]));
                            try
                            {
                                const string sliders = "the live view's sliders, the same gains held at the darkest";
                                if (truthPath is not null)
                                {
                                    PlanetaryMasterScore.AgainstTruth(consoleHost, slid, truthPath, body, sliders);
                                }
                                else
                                {
                                    PlanetaryMasterScore.Undershoot(consoleHost, slid, body, instant, sliders);
                                }
                            }
                            finally
                            {
                                slid.Release();
                            }
                        }
                        if (!parseResult.GetValue(noWriteOpt))
                        {
                            Directory.CreateDirectory(outputDir);
                            var file = Path.Combine(outputDir, Path.GetFileNameWithoutExtension(path) + "_sharpened"
                                + (variants.Length > 1 ? "_" + fix.ToString().ToLowerInvariant() + (nonNegative ? "_nonnegative" : "") : "") + ".fits");
                            result.Sharpened.WriteToFitsFile(file);
                            var png = Path.ChangeExtension(file, ".png");
                            await previewRenderer.RenderPlanetaryAsync(result.Sharpened, png, gamma: 0.75, ct: ct);
                            consoleHost.WriteScrollable($"[planetary] wrote {Path.GetFileName(file)} and its high-key preview {Path.GetFileName(png)}");
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
}
