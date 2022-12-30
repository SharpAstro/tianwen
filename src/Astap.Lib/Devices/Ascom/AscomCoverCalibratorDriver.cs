namespace Astap.Lib.Devices.Ascom;

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

    public CoverStatus CoverState => Connected && _comObject?.CoverState is int cs ? (CoverStatus)cs : CoverStatus.Unknown;

    public CalibratorStatus CalibratorState => Connected && _comObject?.CalibratorState is int cs ? (CalibratorStatus)cs : CalibratorStatus.Unknown;

    public bool IsOpen => CoverState == CoverStatus.Open;

    public bool IsClosed => CoverState == CoverStatus.Closed;

    public bool IsMoving => CoverState == CoverStatus.Moving;

    public bool IsCalibrationReady => IsClosed && CalibratorState is CalibratorStatus.Ready;

    public bool Close()
    {
        if (Connected && _comObject is { } obj)
        {
            obj.CloseClover();
            return true;
        }
        return false;
    }

    public bool Open()
    {
        if (Connected && _comObject is { } obj)
        {
            obj.OpenClover();
            return true;
        }
        return false;
    }
}