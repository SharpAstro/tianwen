namespace Astap.Lib;

public class AscomDeviceDriver : AscomBase
{
    private readonly AscomDevice _device;

    public AscomDeviceDriver(AscomDevice device) : base(device.ProgId) => _device = device;

    public string Name => _comObject?.Name ?? _device.DisplayName;

    public string? Description => _comObject?.Description as string;

    public string? DriverInfo => _comObject?.DriverInfo as string;

    public string? DriverVersion => _comObject?.DriverVersion as string;

    public string DriverType => _device.DeviceType;

    public void SetupDialog() => _comObject?.SetupDialog();
}
