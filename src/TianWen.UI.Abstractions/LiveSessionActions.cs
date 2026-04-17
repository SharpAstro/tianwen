using System;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DIR.Lib;
using Microsoft.Extensions.Logging;
using TianWen.Lib.Devices;
using TianWen.Lib.Extensions;
using TianWen.Lib.Imaging;
using TianWen.Lib.Sequencing;

namespace TianWen.UI.Abstractions
{
    /// <summary>
    /// Static helpers for the live session tab — phase labels, colors, formatting,
    /// plus I/O helpers for preview capture / snapshot / focuser jog. These are
    /// pure-dependency helpers (no signal bus, no DI container) so they're directly
    /// testable with fakes.
    /// </summary>
    public static class LiveSessionActions
    {
        /// <summary>User-facing label for a session phase.</summary>
        public static string PhaseLabel(SessionPhase phase) => phase switch
        {
            SessionPhase.NotStarted => "Not Started",
            SessionPhase.Initialising => "Initialising",
            SessionPhase.WaitingForDark => "Waiting for Dark",
            SessionPhase.Cooling => "Cooling",
            SessionPhase.RoughFocus => "Rough Focus",
            SessionPhase.AutoFocus => "Auto-Focus",
            SessionPhase.CalibratingGuider => "Calibrating Guider",
            SessionPhase.Observing => "Observing",
            SessionPhase.Finalising => "Finalising",
            SessionPhase.Complete => "Complete",
            SessionPhase.Failed => "Failed",
            SessionPhase.Aborted => "Aborted",
            _ => phase.ToString()
        };

        /// <summary>Color for the phase pill in the top strip.</summary>
        public static RGBAColor32 PhaseColor(SessionPhase phase) => phase switch
        {
            SessionPhase.Observing => new RGBAColor32(0x30, 0x88, 0x30, 0xff),         // green
            SessionPhase.Complete => new RGBAColor32(0x30, 0x88, 0x30, 0xff),           // green
            SessionPhase.Failed => new RGBAColor32(0xcc, 0x33, 0x33, 0xff),             // red
            SessionPhase.Aborted => new RGBAColor32(0xcc, 0x88, 0x00, 0xff),            // amber
            SessionPhase.WaitingForDark => new RGBAColor32(0x44, 0x44, 0x88, 0xff),     // dim blue
            SessionPhase.Cooling => new RGBAColor32(0x33, 0x66, 0xcc, 0xff),            // blue
            _ => new RGBAColor32(0x55, 0x55, 0x88, 0xff)                                // default purple-grey
        };

        /// <summary>Format a timespan as compact duration (e.g. "4h56m" or "23m").</summary>
        public static string FormatDuration(TimeSpan ts)
        {
            if (ts.TotalHours >= 1)
            {
                return $"{(int)ts.TotalHours}h{ts.Minutes:D2}m";
            }
            if (ts.TotalMinutes >= 1)
            {
                return $"{(int)ts.TotalMinutes}m{ts.Seconds:D2}s";
            }
            return $"{(int)ts.TotalSeconds}s";
        }

        /// <summary>Phase-specific status text with countdowns and context.</summary>
        public static string PhaseStatusText(LiveSessionState state, ITimeProvider timeProvider)
        {
            // Use CurrentActivity if available — it's more specific than the phase-level text
            if (state.CurrentActivity is { Length: > 0 } activity)
            {
                return activity;
            }

            var obs = state.ActiveSession?.Observations;
            var first = obs is { Count: > 0 } ? obs[0] : (ScheduledObservation?)null;
            var utcNow = timeProvider.GetUtcNow();

            return state.Phase switch
            {
                SessionPhase.Initialising => "Connecting devices\u2026",

                SessionPhase.WaitingForDark when first is { } f =>
                    utcNow < f.Start - TimeSpan.FromMinutes(10)
                        ? $"Next: {f.Target.Name} at {f.Start.ToOffset(state.SiteTimeZone):HH:mm} (in {FormatDuration(f.Start - TimeSpan.FromMinutes(10) - utcNow)})"
                        : $"Next: {f.Target.Name} \u2014 starting soon",

                SessionPhase.WaitingForDark => "Waiting for dark\u2026",

                SessionPhase.Cooling =>
                    $"Cooling to {state.ActiveSession?.Setup.Telescopes[0].Camera.Driver.FocalLength}\u2026"
                    is var _ // placeholder — show temp from cooling samples
                    && state.CoolingSamples is { Length: > 0 } samples
                        ? $"Cooling: {samples[^1].TemperatureC:F0}\u00B0C \u2192 setpoint {samples[^1].SetpointTempC:F0}\u00B0C"
                        : "Cooling cameras\u2026",

                SessionPhase.RoughFocus => "Rough focus: slewing to zenith, detecting stars\u2026",
                SessionPhase.AutoFocus => state.FocusHistory is { Length: > 0 } fh
                    ? $"Auto-focus: last HFD {fh[^1].BestHfd:F1}\" @ pos {fh[^1].BestPosition}"
                    : "Auto-focus: scanning V-curve\u2026",
                SessionPhase.CalibratingGuider => "Calibrating guider\u2026",

                SessionPhase.Observing when state.ActiveObservation is { } ao =>
                    $"Imaging: {ao.Target.Name} ({state.TotalFramesWritten} frames, {FormatDuration(state.TotalExposureTime)})",

                SessionPhase.Finalising =>
                    state.CoolingSamples is { Length: > 0 } ws
                        ? $"Finalising: warming {ws[^1].TemperatureC:F0}\u00B0C \u2192 ambient"
                        : "Finalising: parking mount, warming cameras\u2026",

                SessionPhase.Complete => $"Session complete: {state.TotalFramesWritten} frames, {FormatDuration(state.TotalExposureTime)}",
                SessionPhase.Aborted => "Session aborted",
                SessionPhase.Failed => "Session failed",

                _ => ""
            };
        }

        /// <summary>Format guide RMS as a compact string (e.g. "Total: 1.2\" Ra: 0.8\" Dec: 0.9\"").</summary>
        public static string FormatGuideRms(TianWen.Lib.Devices.Guider.GuideStats? stats)
        {
            if (stats is null)
            {
                return "Guiding: --";
            }
            return $"RMS: {stats.TotalRMS:F1}\" Ra: {stats.RaRMS:F1}\" Dec: {stats.DecRMS:F1}\"";
        }

        /// <summary>Format exposure log entry as a compact TUI row.</summary>
        public static string FormatExposureLogRow(ExposureLogEntry entry, TimeSpan siteTimeZone)
        {
            var target = entry.TargetName.Length > 12 ? entry.TargetName[..12] : entry.TargetName;
            var filter = entry.FilterName.Length > 6 ? entry.FilterName[..6] : entry.FilterName;
            var hfd = entry.MedianHfd > 0 ? $"{entry.MedianHfd:F1}\"" : "--";
            var stars = entry.StarCount > 0 ? $"{entry.StarCount}" : "--";
            return $"{entry.Timestamp.ToOffset(siteTimeZone):HH:mm} {target,-12} {filter,-6} {hfd,5} {stars,4}\u2605";
        }

        /// <summary>Format focus history entry as a compact row.</summary>
        public static string FormatFocusHistoryRow(FocusRunRecord record, TimeSpan siteTimeZone)
        {
            return $"{record.Timestamp.ToOffset(siteTimeZone):HH:mm} {record.OtaName} {record.FilterName} {record.BestHfd:F1}\" @{record.BestPosition}";
        }

        /// <summary>
        /// Samples camera, focuser, and filter-wheel telemetry for one OTA from hub-connected
        /// drivers. Driver calls are guarded by <see cref="LoggerCatchExtensions.CatchAsync{T}"/>
        /// so a single flaky driver call doesn't abort the whole sample.
        /// </summary>
        public static async Task<PreviewOTATelemetry> SampleOTATelemetryAsync(
            IDeviceHub hub, OTAData ota, ILogger logger, CancellationToken ct)
        {
            // Camera
            var ccdTemp = double.NaN;
            var setpoint = double.NaN;
            var power = double.NaN;
            var coolerOn = false;
            var usesGainValue = false;
            var usesGainMode = false;
            short gainMin = 0, gainMax = 0, currentGain = 0;
            var gainModes = ImmutableArray<string>.Empty;
            var cameraConnected = hub.TryGetConnectedDriver<ICameraDriver>(ota.Camera, out var camera);
            if (cameraConnected && camera is not null)
            {
                ccdTemp = await logger.CatchAsyncIf(camera.CanGetCCDTemperature, camera.GetCCDTemperatureAsync, ct, double.NaN);
                power = await logger.CatchAsyncIf(camera.CanGetCoolerPower, camera.GetCoolerPowerAsync, ct, double.NaN);
                coolerOn = await logger.CatchAsyncIf(camera.CanGetCoolerOn, camera.GetCoolerOnAsync, ct);
                setpoint = await logger.CatchAsync(camera.GetSetCCDTemperatureAsync, ct, double.NaN);
                usesGainValue = camera.UsesGainValue;
                usesGainMode = camera.UsesGainMode;
                gainMin = camera.GainMin;
                gainMax = camera.GainMax;
                currentGain = await logger.CatchAsync(camera.GetGainAsync, ct);
                if (usesGainMode && camera.Gains is { Count: > 0 } gains)
                {
                    gainModes = [.. gains];
                }
            }

            // Focuser
            var focPos = 0;
            var focTemp = double.NaN;
            var focMoving = false;
            var focConnected = false;
            if (ota.Focuser is { } focUri)
            {
                focConnected = hub.TryGetConnectedDriver<IFocuserDriver>(focUri, out var foc);
                if (focConnected && foc is not null)
                {
                    focPos = await logger.CatchAsync(foc.GetPositionAsync, ct);
                    focTemp = await logger.CatchAsync(foc.GetTemperatureAsync, ct, double.NaN);
                    focMoving = await logger.CatchAsync(foc.GetIsMovingAsync, ct);
                }
            }

            // Filter wheel
            var filterName = "--";
            var fwConnected = false;
            if (ota.FilterWheel is { } fwUri)
            {
                fwConnected = hub.TryGetConnectedDriver<IFilterWheelDriver>(fwUri, out var fw);
                if (fwConnected && fw is not null)
                {
                    var filter = await logger.CatchAsync(fw.GetCurrentFilterAsync, ct);
                    filterName = filter.DisplayName ?? "--";
                }
            }

            var camDisplay = hub.TryGetDeviceFromUri(ota.Camera, out var dev) && dev is not null
                ? dev.DisplayName : ota.Name;

            return new PreviewOTATelemetry(
                OtaName: ota.Name,
                CameraDisplayName: camDisplay,
                CcdTempC: ccdTemp,
                SetpointC: setpoint,
                CoolerPowerPct: power,
                CoolerOn: coolerOn,
                FocusPosition: focPos,
                FocuserTempC: focTemp,
                FocuserIsMoving: focMoving,
                FilterName: filterName,
                CameraConnected: cameraConnected,
                FocuserConnected: focConnected,
                FilterWheelConnected: fwConnected,
                UsesGainValue: usesGainValue,
                UsesGainMode: usesGainMode,
                GainMin: gainMin,
                GainMax: gainMax,
                CurrentGain: currentGain,
                GainModes: gainModes);
        }

        /// <summary>
        /// Captures a single preview frame: applies optional gain / binning, starts the
        /// exposure, polls until ready, and returns the image. Caller owns the returned
        /// image's lifetime (must call <see cref="Image.Release"/> when done).
        /// </summary>
        public static async Task<Image?> CaptureCameraPreviewAsync(
            ICameraDriver camera,
            TimeSpan exposure,
            short? gain,
            int binning,
            ITimeProvider timeProvider,
            CancellationToken ct)
        {
            // Apply gain if user explicitly set one (null = keep camera default).
            // Both numeric (ZWO/ASCOM) and mode (DSLR ISO) cameras expose SetGainAsync.
            if (gain.HasValue && (camera.UsesGainValue || camera.UsesGainMode))
            {
                try { await camera.SetGainAsync(gain.Value, ct); } catch { }
            }
            if (binning > 1)
            {
                try { camera.BinX = binning; } catch { }
            }

            await camera.StartExposureAsync(exposure, FrameType.Light, ct);

            while (!await camera.GetImageReadyAsync(ct))
            {
                await timeProvider.SleepAsync(TimeSpan.FromMilliseconds(200), ct);
            }

            return await camera.GetImageAsync(ct);
        }

        /// <summary>
        /// Writes a preview image to a per-date Snapshot folder under the configured image
        /// output root. Returns the generated file name (not the full path).
        /// </summary>
        public static async Task<string> SaveSnapshotAsync(
            Image image, int otaIndex, IExternal external, ITimeProvider timeProvider)
        {
            var utcNow = timeProvider.GetUtcNow();
            var dateFolderUtc = utcNow.ToString("yyyy-MM-dd", DateTimeFormatInfo.InvariantInfo);
            var snapshotFolder = Path.Combine(
                external.ImageOutputFolder.FullName,
                "Snapshot",
                dateFolderUtc);
            Directory.CreateDirectory(snapshotFolder);

            var fileName = external.GetSafeFileName(
                $"snapshot_{utcNow:yyyy-MM-ddTHH_mm_ss}_OTA{otaIndex + 1}.fits");
            var filePath = Path.Combine(snapshotFolder, fileName);

            await external.WriteFitsFileAsync(image, filePath);
            return fileName;
        }

        /// <summary>
        /// Moves the focuser by <paramref name="steps"/> relative to its current position.
        /// Returns the computed absolute target position.
        /// </summary>
        public static async Task<int> JogFocuserAsync(IFocuserDriver focuser, int steps, CancellationToken ct)
        {
            var currentPos = await focuser.GetPositionAsync(ct);
            var targetPos = currentPos + steps;
            await focuser.BeginMoveAsync(targetPos, ct);
            return targetPos;
        }

        /// <summary>
        /// Standard astrophotography exposure-time ladder in seconds. The preview stepper
        /// walks this list in both directions; longer exposures (&gt;5 min) are intentionally
        /// absent since preview mode is for framing / focus checks, not sub-framing.
        /// </summary>
        public static readonly ImmutableArray<double> PreviewExposureSteps =
            [0.1, 0.2, 0.5, 1, 2, 3, 5, 10, 15, 30, 60, 120, 300];

        /// <summary>
        /// Returns the next value on the <see cref="PreviewExposureSteps"/> ladder above or
        /// below <paramref name="current"/>. <paramref name="direction"/> &gt; 0 steps up,
        /// &lt; 0 steps down, 0 returns <paramref name="current"/> unchanged. At the ends
        /// of the ladder the value clamps to the min / max step.
        /// </summary>
        public static double StepExposure(double current, int direction)
        {
            var steps = PreviewExposureSteps;
            if (direction > 0)
            {
                for (var i = 0; i < steps.Length; i++)
                {
                    if (steps[i] > current + 0.001)
                    {
                        return steps[i];
                    }
                }
                return steps[^1];
            }
            if (direction < 0)
            {
                for (var i = steps.Length - 1; i >= 0; i--)
                {
                    if (steps[i] < current - 0.001)
                    {
                        return steps[i];
                    }
                }
                return steps[0];
            }
            return current;
        }

        /// <summary>
        /// Returns the new preview gain for one OTA given the current user-override
        /// (<paramref name="current"/> — <c>null</c> means "use camera default"),
        /// the camera capability telemetry, and a signed direction (+1 / -1).
        /// <para>
        /// For numeric-gain cameras the step is <c>max(1, (GainMax-GainMin) / 20)</c>,
        /// clamped to <see cref="PreviewOTATelemetry.GainMin"/> / <see cref="PreviewOTATelemetry.GainMax"/>.
        /// For mode-gain (DSLR ISO) cameras the step is ±1 index into
        /// <see cref="PreviewOTATelemetry.GainModes"/>, clamped to the list bounds.
        /// If the camera supports neither, the effective gain is returned unchanged.
        /// </para>
        /// </summary>
        public static int StepGain(int? current, PreviewOTATelemetry tel, int direction)
        {
            // Treat "no override" as starting from whatever the camera reports so the
            // first click visibly moves off the default instead of jumping to zero.
            var effective = current ?? tel.CurrentGain;

            if (tel.UsesGainValue && tel.GainMax > tel.GainMin)
            {
                var step = Math.Max(1, (tel.GainMax - tel.GainMin) / 20);
                var next = effective + direction * step;
                return Math.Clamp(next, tel.GainMin, tel.GainMax);
            }

            if (tel.UsesGainMode && tel.GainModes.Length > 0)
            {
                var next = effective + direction;
                return Math.Clamp(next, 0, tel.GainModes.Length - 1);
            }

            return effective;
        }

        /// <summary>
        /// Compact label for a preview exposure duration in seconds — shows minutes above
        /// 60s (e.g. "2m"), seconds with up to 4 significant digits below (e.g. "0.5s", "30s").
        /// </summary>
        public static string FormatExposureLabel(double sec)
            => sec >= 60 ? $"{sec / 60:F0}m" : $"{sec:G4}s";

        /// <summary>
        /// Label for a preview gain value. <paramref name="gain"/> <c>null</c> means the
        /// user hasn't overridden the camera default; the returned string wraps the value
        /// in parentheses so the caller can render it dimly to signal "not overridden".
        /// <para>
        /// Returns an empty string for cameras that support neither numeric nor mode gain.
        /// </para>
        /// </summary>
        public static string FormatGainLabel(int? gain, PreviewOTATelemetry tel)
        {
            if (tel.UsesGainValue && tel.GainMax > tel.GainMin)
            {
                return gain.HasValue
                    ? $"Gain: {gain.Value}"
                    : $"Gain: ({tel.CurrentGain})";
            }
            if (tel.UsesGainMode && tel.GainModes.Length > 0)
            {
                var effective = gain ?? tel.CurrentGain;
                var modeName = effective >= 0 && effective < tel.GainModes.Length
                    ? tel.GainModes[effective]
                    : $"#{effective}";
                return gain.HasValue ? modeName : $"({modeName})";
            }
            return string.Empty;
        }
    }
}
