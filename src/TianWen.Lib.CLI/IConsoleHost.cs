using TianWen.Lib.Devices;

namespace TianWen.Lib.CLI;

public interface IConsoleHost
{
    Task<IReadOnlyCollection<Profile>> ListProfilesAsync();

    IDeviceUriRegistry DeviceUriRegistry { get; }
}
