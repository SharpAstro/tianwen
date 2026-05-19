using System;
using System.CommandLine;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.PlateSolve;
using TianWen.Lib.Imaging;

namespace TianWen.Cli;

/// <summary>
/// <c>tianwen solve &lt;fits&gt;</c> -- plate-solve a single FITS file and
/// optionally export the detected stars (centroid + shape + photometry)
/// to CSV/JSON for use as test fixtures.
///
/// <para>Plate-solve goes through <see cref="IPlateSolverFactory"/> which
/// picks the highest-priority solver available (CatalogPlateSolver is
/// built-in and always works; ASTAP / Astrometry.net are external and
/// faster on the same input). Star detection is identical to what
/// <c>StackingPipeline</c> runs during registration, so exported star
/// lists are reproducible across the two paths.</para>
/// </summary>
internal sealed class SolveSubCommand(
    IConsoleHost consoleHost,
    IPlateSolverFactory plateSolverFactory)
{
    /// <summary>Solver scale-tolerance default (matches the internal
    /// <see cref="IPlateSolver"/> constant). +/- 3 % of the supplied
    /// pixel scale is a good blind-solve sweet spot.</summary>
    private const float DefaultRange = 0.03f;

    public Command Build()
    {
        var fitsArg = new Argument<string>("fits")
        {
            Description = "FITS file to plate-solve.",
        };

        // Three equivalent ways to supply the pixel scale:
        //   --scale <arcsec/px>   direct (default "auto" reads FITS headers)
        //   --focus-length <mm> + --pixel-size <um>   computed via 206.265 * px / fl
        // If more than one is supplied we reject; if only one of FL/PS is set we reject.
        var scaleOpt = new Option<string>("--scale")
        {
            Description = "Pixel scale in arcsec/px. 'auto' (default) reads PIXSIZE + FOCALLEN from the FITS header. Override with a literal number when the headers are missing or wrong.",
            DefaultValueFactory = _ => "auto",
        };
        var focusLengthOpt = new Option<double?>("--focus-length", "--fl")
        {
            Description = "Telescope focal length in millimetres. Combined with --pixel-size to derive scale (arcsec/px = 206.265 * pixel_size_um / focal_length_mm). Mutually exclusive with --scale.",
        };
        var pixelSizeOpt = new Option<double?>("--pixel-size", "--px")
        {
            Description = "Sensor pixel size in micrometres. Combined with --focus-length to derive scale. Mutually exclusive with --scale.",
        };

        var searchOriginOpt = new Option<string?>("--search-origin")
        {
            Description = "Initial WCS hint as '<ra-hours>,<dec-deg>' (e.g. '11.196,-61.35'). Narrows the search dramatically when present. Falls back to a blind solve when unset.",
        };
        var searchRadiusOpt = new Option<double>("--search-radius")
        {
            Description = "Search radius in degrees around --search-origin. Ignored unless --search-origin is set.",
            DefaultValueFactory = _ => 5.0,
        };
        var rangeOpt = new Option<float>("--range")
        {
            Description = "Scale-tolerance fraction passed to the solver (default 0.03 = +/- 3 %).",
            DefaultValueFactory = _ => DefaultRange,
        };

        var snrMinOpt = new Option<float>("--snr-min")
        {
            Description = "Star-detection SNR floor (passed through to FindStarsAsync).",
            DefaultValueFactory = _ => 5f,
        };
        var minStarsOpt = new Option<int>("--min-stars")
        {
            Description = "Retry threshold for star detection -- forces a second pass at lower SNR when the first pass returns fewer than this.",
            DefaultValueFactory = _ => 100,
        };
        var maxStarsOpt = new Option<int>("--max-stars")
        {
            Description = "Cap on the detected-star count returned for export. Solver still uses internal limits independently.",
            DefaultValueFactory = _ => 5000,
        };

        var exportStarsOpt = new Option<string?>("--export-stars")
        {
            Description = "Path to write detected stars to. Format selected by --export-format. Columns: x, y, ra_deg, dec_deg (filled only when solve succeeds), hfd, fwhm, ecc, flux, snr.",
        };
        var exportFormatOpt = new Option<string>("--export-format")
        {
            Description = "Output format for --export-stars: 'csv' (default) or 'json'.",
            DefaultValueFactory = _ => "csv",
        };

        var updateFitsOpt = new Option<bool>("--update-fits")
        {
            Description = "Write the solved WCS back into the input FITS file in place. Default off (read-only).",
        };

        var solveCommand = new Command("solve", "Plate-solve a FITS file and optionally export detected stars.")
        {
            Arguments = { fitsArg },
            Options =
            {
                scaleOpt, focusLengthOpt, pixelSizeOpt,
                searchOriginOpt, searchRadiusOpt, rangeOpt,
                snrMinOpt, minStarsOpt, maxStarsOpt,
                exportStarsOpt, exportFormatOpt,
                updateFitsOpt,
            },
        };
        solveCommand.SetAction(async (parseResult, ct) =>
        {
            var fitsPath = parseResult.GetValue(fitsArg)!;
            if (!File.Exists(fitsPath))
            {
                consoleHost.WriteError($"FITS file not found: {fitsPath}");
                return 1;
            }

            var scale = parseResult.GetValue(scaleOpt) ?? "auto";
            var fl = parseResult.GetValue(focusLengthOpt);
            var px = parseResult.GetValue(pixelSizeOpt);
            var scaleResult = ResolveScale(scale, fl, px);
            if (scaleResult.Error is { } scaleError)
            {
                consoleHost.WriteError(scaleError);
                return 1;
            }

            var exportPath = parseResult.GetValue(exportStarsOpt);
            var exportFormat = parseResult.GetValue(exportFormatOpt) ?? "csv";
            if (exportPath is not null && exportFormat is not ("csv" or "json"))
            {
                consoleHost.WriteError($"--export-format must be 'csv' or 'json', got '{exportFormat}'");
                return 1;
            }

            WCS? searchOrigin = null;
            if (parseResult.GetValue(searchOriginOpt) is { } originStr)
            {
                if (TryParseSearchOrigin(originStr, out var wcs) is not null)
                {
                    searchOrigin = wcs;
                }
                else
                {
                    consoleHost.WriteError($"--search-origin must be '<ra-hours>,<dec-deg>' (e.g. '11.196,-61.35'), got '{originStr}'");
                    return 1;
                }
            }

            if (!Image.TryReadFitsFile(fitsPath, out var image, out var existingWcs))
            {
                consoleHost.WriteError($"Failed to read FITS file: {fitsPath}");
                return 1;
            }

            // Star detection on channel 0 (Bayer raw / debayered first channel).
            // The full-pipeline registration also defaults to channel 0; staying
            // consistent so exported lists match what stacking sees.
            var snrMin = parseResult.GetValue(snrMinOpt);
            var minStars = parseResult.GetValue(minStarsOpt);
            var maxStars = parseResult.GetValue(maxStarsOpt);
            var stars = await image.FindStarsAsync(channel: 0, snrMin: snrMin, maxStars: maxStars, minStars: minStars, cancellationToken: ct);
            consoleHost.WriteScrollable($"[detect] {stars.Count} stars (snr>={snrMin}, max={maxStars})");

            // Build the ImageDim hint. "auto" lets the solver fall back to FITS
            // header reads; an explicit scale overrides.
            var (width, height) = (image.Width, image.Height);
            ImageDim? imageDim = scaleResult.Scale is { } s
                ? new ImageDim(s, width, height)
                : null;

            var range = parseResult.GetValue(rangeOpt);
            var radius = parseResult.GetValue(searchRadiusOpt);
            consoleHost.WriteScrollable(
                $"[solve] {(imageDim is { } d ? $"scale={d.PixelScale:F3} arcsec/px" : "scale=auto (from FITS)")} {(searchOrigin is null ? "(blind)" : $"hint @ {radius:F1}°")}");

            PlateSolveResult result;
            try
            {
                result = await plateSolverFactory.SolveFileAsync(
                    fitsPath, imageDim, range, searchOrigin, searchOrigin is null ? null : radius, ct);
            }
            catch (PlateSolverException ex)
            {
                consoleHost.WriteError($"[solve] failed: {ex.Message}");
                result = new PlateSolveResult(null, TimeSpan.Zero);
            }

            if (result.Solution is { } solvedWcs)
            {
                consoleHost.WriteScrollable(
                    $"[solve] RA={solvedWcs.CenterRA:F4}h Dec={solvedWcs.CenterDec:F4}° scale={solvedWcs.PixelScaleArcsec:F3}\"/px matched={result.MatchedStars}/{result.DetectedStars} catalog={result.CatalogStars}");

                if (parseResult.GetValue(updateFitsOpt))
                {
                    // Re-encode the image with the new WCS embedded. Existing
                    // headers are preserved; only WCS keywords get updated.
                    image.WriteToFitsFile(fitsPath, solvedWcs);
                    consoleHost.WriteScrollable($"[solve] wrote WCS to {fitsPath}");
                }
            }
            else
            {
                consoleHost.WriteScrollable("[solve] no solution");
            }

            if (exportPath is not null)
            {
                await ExportStarsAsync(stars, result.Solution, exportPath, exportFormat, ct);
                consoleHost.WriteScrollable($"[export] {stars.Count} stars -> {exportPath}");
            }

            return result.Solution is not null ? 0 : 2;
        });
        return solveCommand;
    }

    // -------------------------------------------------------------------- helpers

    /// <summary>
    /// Resolves the pixel-scale specification to either an explicit number
    /// (arcsec/px) or null (auto -- let the solver read from FITS). Validates
    /// mutual exclusion between --scale and the --focus-length + --pixel-size
    /// pair.
    /// </summary>
    private static (double? Scale, string? Error) ResolveScale(string scaleArg, double? focusLengthMm, double? pixelSizeUm)
    {
        var hasFl = focusLengthMm.HasValue;
        var hasPx = pixelSizeUm.HasValue;
        var hasExplicitScale = !string.Equals(scaleArg, "auto", StringComparison.OrdinalIgnoreCase);

        if (hasExplicitScale && (hasFl || hasPx))
        {
            return (null, "--scale is mutually exclusive with --focus-length / --pixel-size; use one or the other.");
        }
        if (hasFl != hasPx)
        {
            return (null, "--focus-length and --pixel-size must be set together; supplied one without the other.");
        }
        if (hasFl && hasPx)
        {
            // arcsec per pixel = 206.265 * pixel_size_um / focal_length_mm
            var scale = 206.265 * pixelSizeUm!.Value / focusLengthMm!.Value;
            return (scale, null);
        }
        if (hasExplicitScale)
        {
            if (!double.TryParse(scaleArg, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) || parsed <= 0)
            {
                return (null, $"--scale must be 'auto' or a positive number (arcsec/px), got '{scaleArg}'");
            }
            return (parsed, null);
        }
        // auto -- let the solver read from FITS
        return (null, null);
    }

    /// <summary>
    /// Parses an "<ra-hours>,<dec-deg>" hint into a sparse WCS struct that
    /// carries only RA/Dec for the search-origin pass. The solver uses these
    /// fields to narrow its candidate space; CD-matrix entries stay zero
    /// because the solve itself produces those.
    /// </summary>
    private static WCS? TryParseSearchOrigin(string text, out WCS wcs)
    {
        wcs = default;
        var parts = text.Split(',');
        if (parts.Length != 2) return null;
        if (!double.TryParse(parts[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var ra)) return null;
        if (!double.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var dec)) return null;
        wcs = new WCS(ra, dec);
        return wcs;
    }

    /// <summary>
    /// Writes the detected star list to <paramref name="path"/> in the
    /// requested format. RA/Dec columns are filled only when
    /// <paramref name="wcs"/> is non-null and the projection round-trips
    /// cleanly; otherwise they're written as empty (CSV) or null (JSON).
    /// </summary>
    private static async Task ExportStarsAsync(StarList stars, WCS? wcs, string path, string format, CancellationToken ct)
    {
        // Hand-rolled writers (CSV + JSON) so the CLI's AOT publish stays warning-free --
        // System.Text.Json's reflection-based serializer trips IL2026/IL3050 against
        // anonymous types. Schema is tiny and stable enough that a string builder
        // beats wiring a JsonSerializerContext for one record shape.
        var inv = CultureInfo.InvariantCulture;
        var isJson = string.Equals(format, "json", StringComparison.OrdinalIgnoreCase);

        await using var writer = new StreamWriter(path);
        if (isJson)
        {
            await writer.WriteAsync('[');
        }
        else
        {
            await writer.WriteLineAsync("x,y,ra_deg,dec_deg,hfd,fwhm,ecc,flux,snr");
        }

        var first = true;
        var sb = new StringBuilder(256);
        foreach (var star in stars)
        {
            ct.ThrowIfCancellationRequested();
            // PixelToSky returns RA in HOURS; export RA in degrees to match
            // standard catalog conventions (and the ra_deg column name).
            var sky = wcs?.PixelToSky(star.XCentroid + 1, star.YCentroid + 1);
            var raDeg = sky is { } s1 ? (double?)(s1.RA * 15.0) : null;
            var decDeg = sky?.Dec;

            sb.Clear();
            if (isJson)
            {
                if (!first) sb.Append(',');
                sb.Append("{\"x\":").Append(star.XCentroid.ToString("G7", inv))
                    .Append(",\"y\":").Append(star.YCentroid.ToString("G7", inv))
                    .Append(",\"ra_deg\":").Append(raDeg is { } r ? r.ToString("G9", inv) : "null")
                    .Append(",\"dec_deg\":").Append(decDeg is { } d ? d.ToString("G9", inv) : "null")
                    .Append(",\"hfd\":").Append(star.HFD.ToString("G4", inv))
                    .Append(",\"fwhm\":").Append(star.StarFWHM.ToString("G4", inv))
                    .Append(",\"ecc\":").Append(star.Ellipticity.ToString("G4", inv))
                    .Append(",\"flux\":").Append(star.Flux.ToString("G6", inv))
                    .Append(",\"snr\":").Append(star.SNR.ToString("G4", inv))
                    .Append('}');
            }
            else
            {
                sb.Append(star.XCentroid.ToString("G7", inv))
                    .Append(',').Append(star.YCentroid.ToString("G7", inv))
                    .Append(',').Append(raDeg is { } r ? r.ToString("G9", inv) : string.Empty)
                    .Append(',').Append(decDeg is { } d ? d.ToString("G9", inv) : string.Empty)
                    .Append(',').Append(star.HFD.ToString("G4", inv))
                    .Append(',').Append(star.StarFWHM.ToString("G4", inv))
                    .Append(',').Append(star.Ellipticity.ToString("G4", inv))
                    .Append(',').Append(star.Flux.ToString("G6", inv))
                    .Append(',').Append(star.SNR.ToString("G4", inv));
            }
            await writer.WriteAsync(sb);
            if (!isJson) await writer.WriteLineAsync();
            first = false;
        }
        if (isJson) await writer.WriteAsync(']');
    }
}
