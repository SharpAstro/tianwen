using System;
using System.CommandLine;
using System.Globalization;
using System.IO;
using TianWen.Hosting.Dto;
using TianWen.Lib.IO;
using TianWen.Lib.Sequencing;
using TianWen.RemoteClient;

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
///
/// <para>The flat run is this computer's node's (P6 of docs/plans/hardware-in-the-server.md, #936): this sends the profile,
/// the source and the knobs, follows the run to its end phase by phase, and stops it on the node on Ctrl+C. The frames are
/// written by the node, into the output folder this computer's user shares with it, which is how they are counted.</para>
/// </summary>
internal sealed class FlatsSubCommand(
    IConsoleHost consoleHost,
    ProfileSelector profileSelector)
{
    /// <summary>How often the node's run is read for its phase.</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);

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
                // An operator typed this command, and with a hand-switched panel they will have turned it
                // on before (or right after) doing so -- there is nobody else to ask, and refusing the job
                // they explicitly requested would be the wrong call. The SCHEDULED end-of-session flat
                // block keeps the safe default (Decline); see UnattendedPromptResponse.
                UnattendedPromptResponse = UnattendedPromptResponse.Proceed,
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

            if (await consoleHost.NodeAsync(ct) is not { } node)
            {
                return 1;
            }

            // The node's flats start from this configuration (the profile's site, and an operator's answer to a prompt) and
            // lay the knobs over it, as the GUI's do.
            var request = new FlatsRequestDto
            {
                Source = sourceStr,
                Period = periodStr,
                Count = config.FlatsPerFilter,
                Target = config.FlatTargetAduFraction,
                Tolerance = config.FlatAduTolerance,
                MinExposureSeconds = config.FlatMinExposure?.TotalSeconds,
                MaxExposureSeconds = config.FlatMaxExposure?.TotalSeconds,
                InitialExposureSeconds = config.FlatInitialExposure?.TotalSeconds,
                BrightnessPercent = config.FlatCalibratorBrightnessPercent,
                MaxBrackets = config.FlatMaxBrackets,
                Configuration = SessionConfigApiDto.FromConfiguration(config),
            };

            // Count written flats by the output-folder delta -- flat frames don't flow through the
            // observation frame counter (TotalFramesWritten), which tracks light frames only.
            var flatsRoot = Path.Combine(consoleHost.External.ImageOutputFolder.FullName, "Flats");
            var before = CountFlats(flatsRoot);

            var started = await node.StartFlatsAsync(request, profile.ProfileId, ct);
            if (!started.IsSuccess)
            {
                consoleHost.WriteError($"[flats] the node did not start the flat run: {started.Error}");
                return 1;
            }

            var ended = await FollowAsync(node, ct);

            var written = Math.Max(0, CountFlats(flatsRoot) - before);
            var ok = ended?.Phase is SessionPhase.Complete;
            consoleHost.WriteScrollable($"[flats] {(ok ? "complete" : ended?.Phase.ToString() ?? "lost")}: {written} flat frame(s) written to {flatsRoot}.");
            if (!ok && ended?.FailureReason is { } reason)
            {
                consoleHost.WriteError($"[flats] {reason}");
            }
            return ok ? 0 : 2;
        });

        return flatsCommand;
    }

    // The node's flat run followed to its end, each phase said as it is reached; a Ctrl+C aborts it on the node, where it
    // still ends through its own Finalise (warm, close the covers, disconnect).
    private async Task<SessionStateDto?> FollowAsync(TianWenNodeClient node, CancellationToken cancellationToken)
    {
        SessionStateDto? last = null;
        try
        {
            while (true)
            {
                // Whether the run goes on is the node's run record, set as the start was answered; the phase is the
                // session's, read after it, so the read that sees the run over also sees how it ended.
                var nodeNow = await node.GetNodeAsync(cancellationToken);
                if (nodeNow.Value is not { } info)
                {
                    consoleHost.WriteError($"[flats] the node did not answer: {nodeNow.Error}");
                    return last;
                }
                if ((await node.GetSessionStateAsync(cancellationToken)).Value is { } state)
                {
                    if (last?.Phase != state.Phase)
                    {
                        consoleHost.WriteScrollable($"[flats] {state.Phase}");
                    }
                    last = state;
                }
                if (info.Run is not { Kind: NodeRunKind.Flats })
                {
                    return last;
                }
                await consoleHost.TimeProvider.SleepAsync(PollInterval, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            var aborted = await node.AbortSessionAsync(CancellationToken.None);
            consoleHost.WriteScrollable(aborted.IsSuccess
                ? "[flats] stopping the flat run on the node; it finishes through its own ending"
                : $"[flats] the node did not stop the flat run: {aborted.Error}");
            throw;
        }
    }

    private static int CountFlats(string flatsRoot)
        => Directory.Exists(flatsRoot) ? FileEnumeration.CountFiles(flatsRoot, ".fits", recursive: true) : 0;
}
