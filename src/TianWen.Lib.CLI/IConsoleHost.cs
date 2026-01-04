using ImageMagick;
using Microsoft.Extensions.Logging;
using TianWen.Lib.Devices;

namespace TianWen.Lib.CLI;

public interface IConsoleHost
{
    Task<bool> HasSixelSupportAsync();

    ILogger Logger { get; }

    Task<IReadOnlyCollection<Profile>> ListProfilesAsync();

    IDeviceUriRegistry DeviceUriRegistry { get; }

    ValueTask RenderImageAsync(IMagickImage<float> image);
}
