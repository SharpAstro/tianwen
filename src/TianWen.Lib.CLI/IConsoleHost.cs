using ImageMagick;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TianWen.Lib.Devices;

namespace TianWen.Lib.CLI;

public interface IConsoleHost
{
    Task<bool> HasSixelSupportAsync();

    Task<IReadOnlyCollection<Profile>> ListProfilesAsync(CancellationToken cancellationToken);

    IDeviceUriRegistry DeviceUriRegistry { get; }
    
    IHostApplicationLifetime ApplicationLifetime { get; }

    IExternal External { get; }

    ValueTask RenderImageAsync(IMagickImage<float> image);
}
