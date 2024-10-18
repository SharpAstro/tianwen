namespace Astap.Lib.Devices;

public enum CalibratorStatus
{
    /// <summary>
    /// This device does not have a calibration capability.
    /// </summary>
    NotPresent = 0,

    /// <summary>
    /// The calibrator is off.
    /// </summary>
    Off = 1,

    /// <summary>
    /// The calibrator is stabilising or is not yet in the commanded state.
    /// </summary>
    NotReady = 2,

    /// <summary>
    /// The calibrator is ready for use.
    /// </summary>
    Ready = 3,

    /// <summary>
    /// The calibrator state is unknown.
    /// </summary>
    Unknown = 4,

    /// <summary>
    /// The calibrator encountered an error when changing state.
    /// </summary>
    Error = 5,
}
