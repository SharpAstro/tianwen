using TianWen.Lib.Devices;
using TianWen.Lib.Devices.PlayerOne;
using Microsoft.Extensions.DependencyInjection;

namespace TianWen.Lib.Extensions;

public static class PlayerOneServiceCollectionExtensions
{
    public static IServiceCollection AddPlayerOne(this IServiceCollection services)
        => services.AddDevicSource<PlayerOneDevice, PlayerOneDeviceSource>(uri => new PlayerOneDevice(uri));
}
