namespace Astap.Lib.Devices.Ascom;

public record class AscomDevice(string DeviceId, string DeviceType, string DisplayName) : DeviceBase(DeviceId, DeviceType, DisplayName);
