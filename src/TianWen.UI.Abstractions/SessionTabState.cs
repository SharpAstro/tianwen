using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using DIR.Lib;
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

        /// <summary>Whether this camera supports CCD cooling. False for DSLRs.</summary>
        public bool HasCooling { get; init; } = true;

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
        /// <summary>The computed schedule from proposals. Null until scheduling is run from this tab.</summary>
        public ScheduledObservationTree? Schedule { get; set; }

        /// <summary>Whether a session is currently running (config fields become read-only).</summary>
        public bool IsSessionRunning { get; set; }

        /// <summary>The session configuration being edited.</summary>
        public SessionConfiguration Configuration { get; set; } = DefaultConfiguration;

        /// <summary>Per-OTA camera settings for this session. One entry per OTA in the profile.</summary>
        public List<PerOtaCameraSettings> CameraSettings { get; set; } = [];

        /// <summary>Tracks the profile identity to detect when reinitialisation is needed.</summary>
        private Guid? _lastProfileId;

        /// <summary>Tracks the OTA count to detect profile structure changes.</summary>
        private int _lastOtaCount;

        /// <summary>Scroll offset in pixels for the configuration form panel.</summary>
        public int ConfigScrollOffset { get; set; }

        /// <summary>Currently selected config field index (-1 = none).</summary>
        public int SelectedFieldIndex { get; set; } = -1;

        /// <summary>Total number of editable config fields (computed during render).</summary>
        public int FieldCount { get; set; }

        /// <summary>Signal bus for posting session config events. Set by the host during initialization.</summary>
        public SignalBus? Bus { get; set; }

        /// <summary>Index of the observation whose exposure is being edited via text input, or -1 if none.</summary>
        public int EditingExposureIndex { get; set; } = -1;

        /// <summary>Text input for editing an observation's sub-exposure duration (in seconds).</summary>
        public TextInputState ExposureInput { get; } = new() { Placeholder = "seconds" };

        /// <summary>Marks the configuration as dirty and triggers a save signal.</summary>
        public void MarkDirty()
        {
            IsDirty = true;
        }

        /// <summary>Whether the session configuration has unsaved changes.</summary>
        public bool IsDirty
        {
            get => _isDirty;
            internal set
            {
                _isDirty = value;
                if (value)
                {
                    Bus?.Post(new SaveSessionConfigSignal());
                }
            }
        }
        private bool _isDirty;

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
        /// Returns true if the profile has changed since the last initialization
        /// (different profile ID or different OTA count).
        /// </summary>
        public bool NeedsReinitialization(Profile? profile)
        {
            if (profile is null)
            {
                return CameraSettings.Count > 0;
            }

            return profile.ProfileId != _lastProfileId
                || (profile.Data?.OTAs.Length ?? 0) != _lastOtaCount;
        }

        /// <summary>
        /// <summary>
        /// Initializes <see cref="CameraSettings"/> from the active profile's OTAs.
        /// Computes default gain and exposure from f-ratio.
        /// </summary>
        public void InitializeFromProfile(Profile? profile, IDeviceHub? registry = null)
        {
            CameraSettings.Clear();

            if (profile?.Data is not { } data)
            {
                _lastProfileId = null;
                _lastOtaCount = 0;
                return;
            }

            _lastProfileId = profile.ProfileId;
            _lastOtaCount = data.OTAs.Length;

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

                // Resolve camera capabilities from device registry
                IReadOnlyList<string> gainModes = [];
                var hasCooling = true;
                if (ota.Camera is { } camUri && registry is not null
                    && registry.TryGetDeviceFromUri(camUri, out var device))
                {
                    if (device is IDeviceWithGainModes withGain)
                    {
                        gainModes = withGain.GainModes;
                    }
                    if (device is IUncooledCamera)
                    {
                        hasCooling = false;
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
                    HasCooling = hasCooling,
                    UsesGainMode = gainModes.Count > 0,
                    GainModes = gainModes,
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

            // When stepping down, use the zone we'd enter (e.g. 60→50 uses 10s step, not 30s)
            var refSec = up ? sec : sec - 0.001;
            var step = refSec switch
            {
                < 10 => 1,
                < 60 => 10,
                < 120 => 30,
                _ => 60
            };

            // Snap to step grid: if not on a grid line, snap to the nearest grid line in the
            // requested direction first; if already on a grid line, step normally.
            double newSec;
            if (up)
            {
                var snapped = Math.Ceiling(sec / step) * step;
                newSec = snapped > sec ? snapped : snapped + step;
            }
            else
            {
                var snapped = Math.Floor(sec / step) * step;
                newSec = snapped < sec ? snapped : snapped - step;
            }

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

        /// <summary>
        /// Finds the <see cref="ConfigFieldDescriptor"/> at the given flat field index
        /// across all config groups. Returns null if out of range.
        /// </summary>
        public static ConfigFieldDescriptor? FindField(int targetIndex)
        {
            var idx = 0;
            foreach (var group in SessionConfigGroups.Groups)
            {
                foreach (var field in group.Fields)
                {
                    if (idx == targetIndex)
                    {
                        return field;
                    }
                    idx++;
                }
            }
            return null;
        }

        /// <summary>
        /// Increments the selected field's value.
        /// </summary>
        public void IncrementSelectedField()
        {
            if (FindField(SelectedFieldIndex) is { } field)
            {
                Configuration = field.Increment(Configuration);
                IsDirty = true;
                NeedsRedraw = true;
            }
        }

        /// <summary>
        /// Decrements the selected field's value.
        /// </summary>
        public void DecrementSelectedField()
        {
            if (FindField(SelectedFieldIndex) is { } field)
            {
                Configuration = field.Decrement(Configuration);
                IsDirty = true;
                NeedsRedraw = true;
            }
        }

        /// <summary>
        /// Tries to parse a user-entered exposure string as seconds.
        /// Accepts plain numbers (e.g. "120"), or suffixed values ("2m", "2min", "1.5m").
        /// </summary>
        public static bool TryParseExposureInput(string input, out TimeSpan result)
        {
            result = default;
            var trimmed = input.Trim();
            if (trimmed.Length == 0)
            {
                return false;
            }

            // Try "Nm" or "Nmin" suffix
            if (trimmed.EndsWith("min", StringComparison.OrdinalIgnoreCase))
            {
                if (double.TryParse(trimmed.AsSpan(0, trimmed.Length - 3), out var minutes) && minutes > 0)
                {
                    result = TimeSpan.FromMinutes(minutes);
                    return result.TotalSeconds is >= 1 and <= 3600;
                }
                return false;
            }

            if (trimmed.EndsWith('m') || trimmed.EndsWith('M'))
            {
                if (double.TryParse(trimmed.AsSpan(0, trimmed.Length - 1), out var minutes) && minutes > 0)
                {
                    result = TimeSpan.FromMinutes(minutes);
                    return result.TotalSeconds is >= 1 and <= 3600;
                }
                return false;
            }

            // Try "Ns" suffix or plain number (seconds)
            var numSpan = trimmed.EndsWith('s') || trimmed.EndsWith('S')
                ? trimmed.AsSpan(0, trimmed.Length - 1)
                : trimmed.AsSpan();

            if (double.TryParse(numSpan, out var seconds) && seconds >= 1 && seconds <= 3600)
            {
                result = TimeSpan.FromSeconds(seconds);
                return true;
            }

            return false;
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
