using Microsoft.Extensions.DependencyInjection;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Guider;

namespace TianWen.Lib.Extensions;

public static class PHD2ServiceCollectionExtensions
{
    public static IServiceCollection AddPHD2(this IServiceCollection services) => services.AddDevicSource<GuiderDevice, PHD2GuiderDriver>(uri => new GuiderDevice(uri));
}