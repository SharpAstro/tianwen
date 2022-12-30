namespace Astap.Lib.Devices;

public interface ICoverDriver : IDeviceDriver
{
    bool IsOpen { get; }

    bool IsClosed { get; }

    bool IsMoving { get; }

    bool IsCalibrationReady { get; }

    bool Open();

    bool Close();

    bool CalibratorOn(int brightness);

    bool CalibratorOff();

    int Brightness { get; }

    CoverStatus CoverState { get; }

    CalibratorStatus CalibratorState { get; }
}
