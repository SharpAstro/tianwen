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
        bool FilterWheelConnected)
    {
        /// <summary>Default instance with NaN temperatures and no connections.</summary>
        public static readonly PreviewOTATelemetry Unknown = new PreviewOTATelemetry(
            "", "", double.NaN, double.NaN, double.NaN,
            false, 0, double.NaN, false, "--",
            false, false, false);
    }
}
