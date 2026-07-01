using System;
using System.CommandLine;
using System.Globalization;
using System.IO;
using TianWen.Lib.Sequencing;

namespace TianWen.Cli;

/// <summary>
/// <c>tianwen flats</c> -- capture flat frames on-demand, outside a full imaging session. Connects only
/// the flat-relevant devices of the active profile (cameras / covers / filter wheels / focusers, plus the
/// mount for sky-flats), cools to the imaging setpoint, captures per <c>--source</c>, then finalises
/// (warm cameras, close covers, disconnect). Frames land under <c>&lt;output&gt;/Flats/&lt;date&gt;/&lt;filter&gt;/Flat/</c>
/// with the same denormalised FITS headers as lights, so the stacker matches them with no extra wiring.
///
/// <para>Two illumination sources: <c>calibrator</c> (any cover/calibrator device assigned to the OTA -- a
/// flip-flat, a driver lightbox / panel, or a <c>ManualCoverDevice</c> hand-switched panel; the default) and
/// <c>sky</c> (twilight sky-flats; <c>--period dawn|dusk</c> selects the ramp direction and needs the mount).
/// A manual panel is selected by assigning a Manual Light Panel to the OTA cover slot, not by a source flag.</para>
/// </summary>
internal sealed class FlatsSubCommand(
    IConsoleHost consoleHost,
    ISessionFactory sessionFactory,
    ProfileSelector profileSelector)
{
    public Command Build()
    {
        var sourceOpt = new Option<string>("--source")
        {
            Description = "Illumination source. 'calibrator' (default) uses any cover/calibrator assigned to the OTA: a flip-flat, a driver panel, or a hand-switched Manual Light Panel. 'sky' uses twilight sky-flats.",
            DefaultValueFactory = _ => "calibrator",
        };
        var periodOpt = new Option<string>("--period")
        {
            Description = "Twilight period for --source sky: 'dusk' (evening, exposures lengthen; default) or 'dawn' (morning, exposures shorten). Ignored for calibrator.",
            DefaultValueFactory = _ => "dusk",
        };
        var countOpt = new Option<int?>("--count")
        {
            Description = "Flat frames to keep per filter (default: profile/config value).",
        };
        var targetOpt = new Option<double?>("--target")
        {
            Description = "Target exposure level as a fraction of full well, 0..1 (default 0.5).",
        };
        var toleranceOpt = new Option<double?>("--tolerance")
        {
            Description = "Acceptance band around --target, 0..1 (default 0.05).",
        };
        var minExpOpt = new Option<double?>("--min-exposure")
        {
            Description = "Minimum exposure in seconds (auto-exposure lower clamp).",
        };
        var maxExpOpt = new Option<double?>("--max-exposure")
        {
            Description = "Maximum exposure in seconds (auto-exposure upper clamp).",
        };
        var initExpOpt = new Option<double?>("--initial-exposure")
        {
            Description = "First metering exposure in seconds (calibrator source only; the solver brackets from here).",
        };
        var brightnessOpt = new Option<int?>("--brightness")
        {
            Description = "Calibrator panel brightness as a percentage of its maximum, 0..100 (calibrator source only).",
        };
        var bracketsOpt = new Option<int?>("--brackets")
        {
            Description = "Maximum auto-exposure metering brackets before giving up (calibrator only).",
        };

        var flatsCommand = new Command("flats", "Capture flat frames on-demand from a cover/calibrator device (flip-flat, driver panel, or manual panel) or the twilight sky.")
        {
            Options = { sourceOpt, periodOpt, countOpt, targetOpt, toleranceOpt, minExpOpt, maxExpOpt, initExpOpt, brightnessOpt, bracketsOpt },
        };

        flatsCommand.SetAction(async (parseResult, ct) =>
        {
            var sourceStr = parseResult.GetValue(sourceOpt) ?? "calibrator";
            if (!FlatRunParsing.TryParseSource(sourceStr, out var source))
            {
                consoleHost.WriteError($"--source must be 'calibrator' or 'sky', got '{sourceStr}'");
                return 1;
            }

            var periodStr = parseResult.GetValue(periodOpt) ?? "dusk";
            if (!FlatRunParsing.TryParsePeriod(periodStr, out var period))
            {
                consoleHost.WriteError($"--period must be 'dawn' or 'dusk', got '{periodStr}'");
                return 1;
            }

            var profile = await profileSelector.ResolveProfileAsync(parseResult, interactive: false, ct);
            if (profile is null)
            {
                // ResolveProfileAsync already wrote the specific error.
                return 1;
            }

            var defaults = new SessionConfiguration();
            var data = profile.Data;
            var config = defaults with
            {
                // Site drives the mount sync + denorm stamp + sky-flat solar-altitude gate; the mount's own
                // site is the fallback when the profile has none (handled in ConnectForFlatsAsync).
                SiteLatitude = data?.SiteLatitude ?? defaults.SiteLatitude,
                SiteLongitude = data?.SiteLongitude ?? defaults.SiteLongitude,
                FlatSource = source,
                FlatsPerFilter = parseResult.GetValue(countOpt) ?? defaults.FlatsPerFilter,
                FlatTargetAduFraction = parseResult.GetValue(targetOpt) ?? defaults.FlatTargetAduFraction,
                FlatAduTolerance = parseResult.GetValue(toleranceOpt) ?? defaults.FlatAduTolerance,
                FlatMaxBrackets = parseResult.GetValue(bracketsOpt) ?? defaults.FlatMaxBrackets,
                FlatCalibratorBrightnessPercent = parseResult.GetValue(brightnessOpt) ?? defaults.FlatCalibratorBrightnessPercent,
                FlatInitialExposure = parseResult.GetValue(initExpOpt) is { } ie ? TimeSpan.FromSeconds(ie) : defaults.FlatInitialExposure,
                FlatMinExposure = parseResult.GetValue(minExpOpt) is { } mn ? TimeSpan.FromSeconds(mn) : defaults.FlatMinExposure,
                FlatMaxExposure = parseResult.GetValue(maxExpOpt) is { } mx ? TimeSpan.FromSeconds(mx) : defaults.FlatMaxExposure,
            };

            var sourceLabel = source switch
            {
                FlatIlluminationSource.TwilightSky => $"sky ({period.ToString().ToLowerInvariant()})",
                _ => "calibrator",
            };
            consoleHost.WriteScrollable($"[flats] profile '{profile.DisplayName}', source={sourceLabel}, {config.FlatsPerFilter} frame(s)/filter, target {config.FlatTargetAduFraction:P0}.");

            // Populate the device hub (discovery + solver support check) so Create can resolve profile URIs.
            await sessionFactory.InitializeAsync(ct);

            ISession session;
            try
            {
                session = sessionFactory.Create(profile.ProfileId, config, []);
            }
            catch (ArgumentException ex)
            {
                consoleHost.WriteError(ex.Message);
                return 1;
            }

            // Count written flats by the output-folder delta -- flat frames don't flow through the
            // observation frame counter (TotalFramesWritten), which tracks light frames only.
            var flatsRoot = Path.Combine(consoleHost.External.ImageOutputFolder.FullName, "Flats");
            var before = CountFlats(flatsRoot);

            session.PhaseChanged += (_, e) => consoleHost.WriteScrollable($"[flats] {e.NewPhase}");

            try
            {
                await session.RunFlatsOnlyAsync(period, ct);
            }
            finally
            {
                await session.DisposeAsync();
            }

            var written = Math.Max(0, CountFlats(flatsRoot) - before);
            var ok = session.Phase is SessionPhase.Complete;
            consoleHost.WriteScrollable($"[flats] {(ok ? "complete" : session.Phase.ToString())}: {written} flat frame(s) written to {flatsRoot}.");
            return ok ? 0 : 2;
        });

        return flatsCommand;
    }

    private static int CountFlats(string flatsRoot)
        => Directory.Exists(flatsRoot) ? Directory.GetFiles(flatsRoot, "*.fits", SearchOption.AllDirectories).Length : 0;
}
