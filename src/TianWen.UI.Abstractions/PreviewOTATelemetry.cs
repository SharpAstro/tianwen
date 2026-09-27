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

        /// <summary>
        /// One OTA's telemetry from what its devices read, a device that is not connected read as <see langword="null"/>:
        /// ONE rule for this computer's devices, read through its hub (<see cref="LiveSessionActions.SampleOTATelemetryAsync"/>),
        /// and a rig's, read by its node and sent as its device states (P5b part 9 of docs/plans/hardware-in-the-server.md),
        /// so an idle rig's OTA panels lay out as this computer's do. The camera is named by its URI, as every device is
        /// (<see cref="DeviceBase.DisplayNameOf"/>), else by the OTA.
        /// </summary>
        public static PreviewOTATelemetry From(OTAData ota, CameraReading? camera, FocuserReading? focuser, FilterWheelReading? filterWheel) =>
            new PreviewOTATelemetry(
                OtaName: ota.Name,
                CameraDisplayName: DeviceBase.DisplayNameOf(ota.Camera) is { Length: > 0 } name ? name : ota.Name,
                CcdTempC: camera?.CcdTemperatureC ?? double.NaN,
                SetpointC: camera?.SetpointC ?? double.NaN,
                CoolerPowerPct: camera?.CoolerPowerPercent ?? double.NaN,
                CoolerOn: camera?.CoolerOn ?? false,
                FocusPosition: focuser?.Position ?? 0,
                FocuserTempC: focuser?.TemperatureC ?? double.NaN,
                FocuserIsMoving: focuser?.IsMoving ?? false,
                FilterName: filterWheel?.FilterName ?? "--",
                CameraConnected: camera is not null,
                FocuserConnected: focuser is not null,
                FilterWheelConnected: filterWheel is not null,
                UsesGainValue: camera?.UsesGainValue ?? false,
                UsesGainMode: camera?.UsesGainMode ?? false,
                GainMin: camera?.GainMin ?? 0,
                GainMax: camera?.GainMax ?? 0,
                CurrentGain: camera?.Gain ?? 0,
                GainModes: camera?.GainModes ?? ImmutableArray<string>.Empty,
                SensorWidth: camera?.SensorWidth ?? 0,
                SensorHeight: camera?.SensorHeight ?? 0,
                RoiConstraints: camera?.RoiConstraints ?? default);
    }
}
