using Astap.Lib.Devices;


namespace Astap.Lib.Sequencing;

public class Cover : ControllableDeviceBase<ICoverDriver>
{
    public Cover(DeviceBase device)
        : base(device)
    {

    }

    protected override void Driver_DeviceConnectedEvent(object? sender, DeviceConnectedEventArgs e)
    {
        // empty
    }

    public void Open() => Driver.Open();

    public void Close() => Driver.Close();

    public bool IsCalibrationReady => Driver.IsCalibrationReady;

    public bool IsMoving => Driver.IsMoving;

    public int Brightness
    {
        get => Driver.Brightness;
        set => Driver.Brightness = value;
    }
}
