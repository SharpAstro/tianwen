using System.Collections.Immutable;
using TianWen.Lib.Devices;

namespace TianWen.UI.Abstractions
{
    /// <summary>
    /// Per-OTA telemetry snapshot for preview mode (no active session).
    /// Populated by <see cref="AppSignalHandler.PollPreviewTelemetry"/> from hub-connected drivers.
    /// Index matches <c>ActiveProfile.Data.OTAs</c>.
    /// </summary>
    public readonly record struct PreviewOTATelemetry(
        string OtaName,
        string CameraDisplayName,
        double CcdTempC,
        double SetpointC,
        double CoolerPowerPct,
        bool CoolerOn,
        int FocusPosition,
        double FocuserTempC,
        bool FocuserIsMoving,
        string FilterName,
        bool CameraConnected,
        bool FocuserConnected,
        bool FilterWheelConnected,
        bool UsesGainValue = false,
        bool UsesGainMode = false,
        short GainMin = 0,
        short GainMax = 0,
        short CurrentGain = 0,
        ImmutableArray<string> GainModes = default,
        // Sensor geometry + ROI rules of the connected camera, snapshotted here so the planetary ROI picker
        // (PiP + size presets) reads the REAL constraints off the render thread without touching the driver.
        // SensorWidth <= 0 means "no camera connected / not yet sampled" -> the picker uses a fallback.
        int SensorWidth = 0,
        int SensorHeight = 0,
        RoiConstraints RoiConstraints = default)
    {
        /// <summary>Default instance with NaN temperatures and no connections.</summary>
        public static readonly PreviewOTATelemetry Unknown = new PreviewOTATelemetry(
            "", "", double.NaN, double.NaN, double.NaN,
            false, 0, double.NaN, false, "--",
            false, false, false);
    }
}
