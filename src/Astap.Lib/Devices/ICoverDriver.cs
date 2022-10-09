using System;

namespace Astap.Lib.Devices;

public interface ICoverDriver : IDeviceDriver
{
    bool IsOpen { get; }

    bool IsClosed { get; }

    bool IsMoving { get; }

    bool IsCalibrationReady { get; }

    void Open();

    void Close();

    int Brightness { get; set; }
}
