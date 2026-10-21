using TianWen.Lib.Devices;
using TianWen.Lib.Devices.SVBony;
using Microsoft.Extensions.DependencyInjection;

namespace TianWen.Lib.Extensions;

public static class SVBonyServiceCollectionExtensions
{
    public static IServiceCollection AddSVBony(this IServiceCollection services)
        => services.AddDevicSource<SVBonyDevice, SVBonyDeviceSource>(uri => new SVBonyDevice(uri));
}
