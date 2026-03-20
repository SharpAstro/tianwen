using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using TianWen.Lib.Devices;
using TianWen.Lib.Sequencing;

namespace TianWen.UI.Abstractions
{
    /// <summary>
    /// Per-OTA camera settings for the current session.
    /// Initialized from profile defaults; user can override per-session without changing the profile.
    /// </summary>
    public class PerOtaCameraSettings
    {
        /// <summary>OTA name from the profile (e.g. "Telescope #0").</summary>
        public required string OtaName { get; init; }

        /// <summary>Focal length in mm.</summary>
        public required int FocalLength { get; init; }

        /// <summary>Aperture in mm (null if not specified).</summary>
        public int? Aperture { get; init; }

        /// <summary>f-ratio computed from focal length / aperture. NaN if aperture unknown.</summary>
        public double FRatio => Aperture is > 0 ? (double)FocalLength / Aperture.Value : double.NaN;

        /// <summary>CCD cooling setpoint in °C for this session. Derived from profile default.</summary>
        public sbyte SetpointTempC { get; set; } = -10;

        /// <summary>
        /// Camera gain for this session. For cameras with <c>UsesGainMode</c>, this is the mode index.
        /// Derived from profile's camera URI query param or a sensible default.
        /// </summary>
        public int Gain { get; set; } = 120;

        /// <summary>Whether this camera uses named gain modes instead of numeric values.</summary>
        public bool UsesGainMode { get; init; }

        /// <summary>Named gain modes (populated from camera driver when <see cref="UsesGainMode"/> is true).</summary>
        public IReadOnlyList<string> GainModes { get; init; } = [];

        /// <summary>Camera offset for this session.</summary>
        public int Offset { get; set; }

        /// <summary>Whether this camera uses named offset modes instead of numeric values.</summary>
        public bool UsesOffsetMode { get; init; }

        /// <summary>Named offset modes (populated from camera driver when <see cref="UsesOffsetMode"/> is true).</summary>
        public IReadOnlyList<string> OffsetModes { get; init; } = [];
    }

    /// <summary>
    /// Shared state for the session configuration tab, used by both GUI and TUI.
    /// </summary>
    public class SessionTabState
    {
        /// <summary>The session configuration being edited.</summary>
        public SessionConfiguration Configuration { get; set; } = DefaultConfiguration;

        /// <summary>Per-OTA camera settings for this session. One entry per OTA in the profile.</summary>
        public List<PerOtaCameraSettings> CameraSettings { get; set; } = [];

        /// <summary>Scroll offset in pixels for the configuration form panel.</summary>
        public int ConfigScrollOffset { get; set; }

        /// <summary>Whether the display needs a redraw.</summary>
        public bool NeedsRedraw { get; set; }

        /// <summary>Sensible defaults for all required SessionConfiguration fields.</summary>
        public static SessionConfiguration DefaultConfiguration { get; } = new SessionConfiguration(
            SetpointCCDTemperature: new SetpointTemp(-10, SetpointTempKind.Normal),
            CooldownRampInterval: TimeSpan.FromMinutes(5),
            WarmupRampInterval: TimeSpan.FromMinutes(5),
            MinHeightAboveHorizon: 20,
            DitherPixel: 5.0,
            SettlePixel: 1.0,
            DitherEveryNthFrame: 3,
            SettleTime: TimeSpan.FromSeconds(10),
            GuidingTries: 3
        );

        /// <summary>
        /// Initializes <see cref="CameraSettings"/> from the active profile's OTAs.
        /// Computes default gain and exposure from f-ratio.
        /// </summary>
        public void InitializeFromProfile(Profile? profile)
        {
            CameraSettings.Clear();

            if (profile?.Data is not { } data)
            {
                return;
            }

            foreach (var ota in data.OTAs)
            {
                var gain = 120;
                var offset = 0;

                // Try to read defaults from camera URI query params
                if (ota.Camera is { } cameraUri)
                {
                    var query = cameraUri.Query;
                    if (TryParseQueryInt(query, "gain", out var g))
                    {
                        gain = g;
                    }
                    if (TryParseQueryInt(query, "offset", out var o))
                    {
                        offset = o;
                    }
                }

                CameraSettings.Add(new PerOtaCameraSettings
                {
                    OtaName = ota.Name,
                    FocalLength = ota.FocalLength,
                    Aperture = ota.Aperture,
                    Gain = gain,
                    Offset = offset,
                    SetpointTempC = -10,
                });
            }
        }

        /// <summary>
        /// Computes a default sub-exposure in seconds based on f-ratio.
        /// Rule of thumb: ~5 * (f/ratio)^2, clamped to [10, 600].
        /// </summary>
        public static int DefaultExposureFromFRatio(double fRatio)
        {
            if (double.IsNaN(fRatio) || fRatio <= 0)
            {
                return 120; // fallback
            }

            var seconds = (int)(5.0 * fRatio * fRatio);
            return Math.Clamp(seconds, 10, 600);
        }

        /// <summary>
        /// Steps an exposure time intelligently: 10s steps below 1min, 30s steps 1-2min, 60s steps above 2min.
        /// </summary>
        public static TimeSpan StepExposure(TimeSpan current, bool up)
        {
            var sec = current.TotalSeconds;
            var step = sec switch
            {
                < 60 => 10,
                < 120 => 30,
                _ => 60
            };

            var newSec = up ? sec + step : sec - step;
            newSec = Math.Clamp(newSec, 1, 3600);
            return TimeSpan.FromSeconds(newSec);
        }

        /// <summary>
        /// Estimates the number of frames given window duration and sub-exposure.
        /// Accounts for ~10s overhead per frame (download, dither, settle).
        /// </summary>
        public static int EstimateFrameCount(TimeSpan window, TimeSpan subExposure)
        {
            if (subExposure <= TimeSpan.Zero)
            {
                return 0;
            }

            const double overheadSeconds = 10.0;
            var cycleSeconds = subExposure.TotalSeconds + overheadSeconds;
            return Math.Max(0, (int)(window.TotalSeconds / cycleSeconds));
        }

        private static bool TryParseQueryInt(string query, string key, out int value)
        {
            value = 0;
            if (string.IsNullOrEmpty(query))
            {
                return false;
            }

            var search = $"{key}=";
            var idx = query.IndexOf(search, StringComparison.OrdinalIgnoreCase);
            if (idx < 0)
            {
                return false;
            }

            var start = idx + search.Length;
            var end = query.IndexOf('&', start);
            var span = end < 0 ? query.AsSpan(start) : query.AsSpan(start, end - start);
            return int.TryParse(span, out value);
        }
    }
}
