namespace Astap.Lib.Devices;

public interface ICoverDriver : IDeviceDriver
{
    bool IsOpen { get; }

    bool IsClosed { get; }

    bool IsMoving { get; }

    bool IsCalibrationReady { get; }

    bool Open();

    bool Close();

    int Brightness { get; set; }

    CoverStatus CoverState { get; }

    CalibratorStatus CalibratorState { get; }
}
