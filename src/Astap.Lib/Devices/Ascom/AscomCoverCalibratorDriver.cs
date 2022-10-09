namespace Astap.Lib.Devices.Ascom;

enum CoverStatus
{
    NotPresent = 0, // This device does not have a cover that can be closed independently
    Closed = 1, // The cover is closed
    Moving = 2, // The cover is moving to a new position
    Open = 3, // The cover is open
    Unknown = 4, // The state of the cover is unknown
    Error = 5, // The device encountered an error when changing state
}

enum CalibratorStatus
{
    NotPresent = 0, // This device does not have a calibration capability
    Off = 1, // The calibrator is off
    NotReady = 2, // The calibrator is stabilising or is not yet in the commanded state
    Ready = 3, // The calibrator is ready for use
    Unknown = 4, // The calibrator state is unknown
    Error = 5, // The calibrator encountered an error when changing state
}

public class AscomCoverCalibratorDriver : AscomDeviceDriverBase, ICoverDriver
{
    public AscomCoverCalibratorDriver(AscomDevice device) : base(device)
    {

    }

    public int Brightness
    {
        get => _comObject?.Brightness is int brightness ? brightness : -1;
        set
        {
            if (value <= 0)
            {
                _comObject?.CalibratorOff();
            }
            _comObject?.CalibratorOn(value);
        }
    }

    CoverStatus CoverState => Connected && _comObject?.CoverState is CoverStatus status ? status : CoverStatus.Unknown;

    CalibratorStatus CalibratorState => Connected && _comObject?.CoverState is CalibratorStatus status ? status : CalibratorStatus.Unknown;

    public bool IsOpen => CoverState == CoverStatus.Open;

    public bool IsClosed => CoverState == CoverStatus.Closed;

    public bool IsMoving => CoverState == CoverStatus.Moving;

    public bool IsCalibrationReady => IsClosed && CalibratorState is CalibratorStatus.Ready;

    public void Close() => _comObject?.CloseCover();

    public void Open() => _comObject?.OpenCover();
}