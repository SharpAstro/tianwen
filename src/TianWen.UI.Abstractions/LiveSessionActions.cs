using System;
using DIR.Lib;
using TianWen.Lib.Sequencing;

namespace TianWen.UI.Abstractions
{
    /// <summary>
    /// Static helpers for the live session tab — phase labels, colors, formatting.
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
        public static string PhaseStatusText(LiveSessionState state, TimeProvider timeProvider)
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
                        ? $"Next: {f.Target.Name} at {f.Start:HH:mm} (in {FormatDuration(f.Start - TimeSpan.FromMinutes(10) - utcNow)})"
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
            return $"Total: {stats.TotalRMS:F1}\" Ra: {stats.RaRMS:F1}\" Dec: {stats.DecRMS:F1}\" Peak: {Math.Max(stats.PeakRa, stats.PeakDec):F1}\"";
        }

        /// <summary>Format exposure log entry as a compact TUI row.</summary>
        public static string FormatExposureLogRow(ExposureLogEntry entry)
        {
            var target = entry.TargetName.Length > 12 ? entry.TargetName[..12] : entry.TargetName;
            var filter = entry.FilterName.Length > 6 ? entry.FilterName[..6] : entry.FilterName;
            var hfd = entry.MedianHfd > 0 ? $"{entry.MedianHfd:F1}\"" : "--";
            var stars = entry.StarCount > 0 ? $"{entry.StarCount}" : "--";
            return $"{entry.Timestamp:HH:mm} {target,-12} {filter,-6} {hfd,5} {stars,4}\u2605";
        }

        /// <summary>Format focus history entry as a compact row.</summary>
        public static string FormatFocusHistoryRow(FocusRunRecord record)
        {
            return $"{record.Timestamp:HH:mm} {record.OtaName} {record.FilterName} {record.BestHfd:F1}\" @{record.BestPosition}";
        }
    }
}
