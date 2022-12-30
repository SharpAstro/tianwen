using System;

namespace Astap.Lib.Devices.Ascom;

public class AscomCoverCalibratorDriver : AscomDeviceDriverBase, ICoverDriver
{
    public AscomCoverCalibratorDriver(AscomDevice device) : base(device)
    {

    }

    public int MaxBrightness => _comObject?.MaxBrightness is int maxBrightness ? maxBrightness : -1;

    public bool CalibratorOn(int brightness)
    {
        if (Connected && _comObject is { } obj && IsCalibrationReady && brightness >= 0 && brightness <= MaxBrightness)
        {
            obj.CalibratorOn(brightness);
            return true;
        }

        return false;
    }

    public bool CalibratorOff()
    {
        if (Connected && _comObject is { } obj)
        {
            obj.CalibratorOff();
            return true;
        }

        return false;
    }

    public int Brightness
    {
        get => _comObject?.Brightness is int brightness ? brightness : -1;
    }

    public CoverStatus CoverState => Connected && _comObject?.CoverState is int cs ? (CoverStatus)cs : CoverStatus.Unknown;

    public CalibratorStatus CalibratorState => Connected && _comObject?.CalibratorState is int cs ? (CalibratorStatus)cs : CalibratorStatus.Unknown;

    public bool IsOpen => CoverState == CoverStatus.Open;

    public bool IsClosed => CoverState == CoverStatus.Closed;

    public bool IsMoving => CoverState == CoverStatus.Moving;

    public bool IsCalibrationReady => IsClosed && CalibratorState is not CalibratorStatus.NotReady or CalibratorStatus.Error;

    public bool Close()
    {
        if (Connected && _comObject is { } obj)
        {
            obj.CloseCover();
            return true;
        }
        return false;
    }

    public bool Open()
    {
        if (Connected && _comObject is { } obj)
        {
            obj.OpenCover();
            return true;
        }
        return false;
    }
}