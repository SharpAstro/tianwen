namespace Astap.Lib.Devices;

public interface IFocuserDriver : IDeviceDriver
{
    int Position { get; set; }
}