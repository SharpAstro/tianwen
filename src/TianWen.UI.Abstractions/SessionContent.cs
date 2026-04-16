using System;
using System.Collections.Generic;
using System.Text;
using TianWen.Lib.Devices;

namespace TianWen.UI.Abstractions
{
    /// <summary>
    /// Display-model for one OTA's camera settings row.
    /// </summary>
    public readonly record struct CameraSettingsRow(
        string OtaName,
        string FRatioLabel,
        sbyte SetpointTempC,
        string GainLabel,
        int Offset);

    /// <summary>
    /// Display-model for one observation row in the session tab.
    /// </summary>
    public readonly record struct ObservationRow(
        int Index,
        string TargetName,
        string TimeWindow,
        string Duration,
        string Exposure,
        string FrameEstimate,
        bool IsWarning);

    /// <summary>
    /// Shared content model for the session tab. Provides display-ready data
    /// from <see cref="SessionTabState"/> and <see cref="PlannerState"/>.
    /// Both GPU and TUI hosts consume these methods.
    /// </summary>
    public static class SessionContent
    {
        /// <summary>
        /// Formats a sub-exposure duration for display.
        /// </summary>
        public static string FormatExposure(TimeSpan ts)
        {
            if (ts.TotalMinutes >= 1 && ts.TotalSeconds % 60 == 0)
            {
                return $"{(int)ts.TotalMinutes}min";
            }
            return $"{(int)ts.TotalSeconds}s";
        }

        /// <summary>
        /// Returns the default sub-exposure in seconds from the first OTA's f-ratio,
        /// or 120 if no cameras are configured.
        /// </summary>
        public static int DefaultExposureSeconds(SessionTabState sessionState)
        {
            return sessionState.CameraSettings.Count > 0
                ? SessionTabState.DefaultExposureFromFRatio(sessionState.CameraSettings[0].FRatio)
                : 120;
        }

        /// <summary>
        /// Builds display-ready camera settings rows from the session state.
        /// </summary>
        public static List<CameraSettingsRow> GetCameraRows(SessionTabState sessionState)
        {
            var rows = new List<CameraSettingsRow>(sessionState.CameraSettings.Count);

            foreach (var cam in sessionState.CameraSettings)
            {
                var fRatioLabel = !double.IsNaN(cam.FRatio) ? $"f/{cam.FRatio:0.#}" : "";

                string gainLabel;
                if (cam.UsesGainMode && cam.GainModes.Count > 0)
                {
                    gainLabel = cam.Gain >= 0 && cam.Gain < cam.GainModes.Count
                        ? cam.GainModes[cam.Gain]
                        : $"Mode {cam.Gain}";
                }
                else
                {
                    gainLabel = $"{cam.Gain}";
                }

                rows.Add(new CameraSettingsRow(cam.OtaName, fRatioLabel, cam.SetpointTempC, gainLabel, cam.Offset));
            }

            return rows;
        }

        /// <summary>
        /// Builds display-ready observation rows from session and planner state.
        /// </summary>
        public static List<ObservationRow> GetObservationRows(SessionTabState sessionState, PlannerState plannerState, ITimeProvider? timeProvider = null)
        {
            var proposals = plannerState.Proposals;
            var sliders = plannerState.HandoffSliders;
            var dark = plannerState.AstroDark;
            var twilight = plannerState.AstroTwilight;
            var tz = plannerState.SiteTimeZone;
            var defaultExpSec = DefaultExposureSeconds(sessionState);

            var rows = new List<ObservationRow>(proposals.Length);

            for (var i = 0; i < proposals.Length; i++)
            {
                var proposal = proposals[i];

                // Compute window, clipped to "now" for remaining time estimate
                var windowStart = i > 0 && i - 1 < sliders.Count ? sliders[i - 1] : dark;
                var windowEnd = i < sliders.Count ? sliders[i] : twilight;
                var effectiveStart = windowStart;
                if (timeProvider is not null)
                {
                    var utcNow = timeProvider.GetUtcNow();
                    if (utcNow > windowStart && utcNow < windowEnd)
                    {
                        effectiveStart = utcNow;
                    }
                }
                var window = effectiveStart != default && windowEnd != default
                    ? windowEnd - effectiveStart
                    : TimeSpan.Zero;

                var subExp = proposal.SubExposure ?? TimeSpan.FromSeconds(defaultExpSec);
                var expStr = FormatExposure(subExp);

                var frameCount = window > TimeSpan.Zero
                    ? SessionTabState.EstimateFrameCount(window, subExp)
                    : 0;
                var frameStr = frameCount > 0 ? $"~{frameCount}" : "\u2014";

                string timeWindow;
                string duration;
                bool isWarning;

                if (window > TimeSpan.Zero)
                {
                    var startStr = windowStart.ToOffset(tz).ToString("HH:mm");
                    var endStr = windowEnd.ToOffset(tz).ToString("HH:mm");
                    timeWindow = $"{startStr}\u2013{endStr}";
                    duration = window.TotalHours >= 1
                        ? $"{window.TotalHours:0.0}h"
                        : $"{window.TotalMinutes:0}min";
                    isWarning = window.TotalHours < 1.5;
                }
                else
                {
                    timeWindow = "";
                    duration = "";
                    isWarning = false;
                }

                rows.Add(new ObservationRow(i + 1, proposal.Target.Name, timeWindow, duration, expStr, frameStr, isWarning));
            }

            return rows;
        }

        /// <summary>
        /// Formats the right panel (camera settings + observation list) as markdown for the TUI.
        /// </summary>
        public static string FormatRightPanelMarkdown(SessionTabState sessionState, PlannerState plannerState, ITimeProvider? timeProvider = null)
        {
            var sb = new StringBuilder();

            // Camera settings
            sb.AppendLine("## Camera Settings");
            sb.AppendLine();

            var cameraRows = GetCameraRows(sessionState);
            if (cameraRows.Count == 0)
            {
                sb.AppendLine("*No cameras configured.*");
            }
            else
            {
                foreach (var cam in cameraRows)
                {
                    var fStr = cam.FRatioLabel.Length > 0 ? $" {cam.FRatioLabel}" : "";
                    sb.AppendLine($"**{cam.OtaName}**{fStr}");
                    sb.AppendLine($"  Setpoint: {cam.SetpointTempC}\u00b0C  Gain: {cam.GainLabel}  Offset: {cam.Offset}");
                    sb.AppendLine();
                }
            }

            // Observation list
            sb.AppendLine("## Observations");
            sb.AppendLine();

            var obsRows = GetObservationRows(sessionState, plannerState, timeProvider);
            if (obsRows.Count == 0)
            {
                sb.AppendLine("*No targets pinned.*");
                sb.AppendLine("Use Planner tab to add targets.");
                return sb.ToString();
            }

            foreach (var obs in obsRows)
            {
                sb.AppendLine($"{obs.Index}. **{obs.TargetName}**");

                if (obs.TimeWindow.Length > 0)
                {
                    sb.AppendLine($"   {obs.TimeWindow} ({obs.Duration}) | {obs.Exposure} | {obs.FrameEstimate} frames");
                }
                else
                {
                    sb.AppendLine($"   {obs.Exposure} | {obs.FrameEstimate} frames");
                }

                sb.AppendLine();
            }

            return sb.ToString();
        }
    }
}
