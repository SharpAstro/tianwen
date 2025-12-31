using ImageMagick;
using Microsoft.Extensions.Logging;
using TianWen.Lib.Devices;

namespace TianWen.Lib.CLI;

public interface IConsoleHost
{
    bool HasSixelSupport { get; }

    ILogger Logger { get; }

    Task<IReadOnlyCollection<Profile>> ListProfilesAsync();

    IDeviceUriRegistry DeviceUriRegistry { get; }

    void RenderImage(IMagickImage<float> image);
}
