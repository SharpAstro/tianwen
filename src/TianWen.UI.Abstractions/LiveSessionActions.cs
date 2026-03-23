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
            return $"{entry.Timestamp:HH:mm} {target,-12} {filter,-6} {hfd,5}";
        }

        /// <summary>Format focus history entry as a compact row.</summary>
        public static string FormatFocusHistoryRow(FocusRunRecord record)
        {
            return $"{record.Timestamp:HH:mm} {record.OtaName} {record.FilterName} {record.BestHfd:F1}\" @{record.BestPosition}";
        }
    }
}
