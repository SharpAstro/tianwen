using Microsoft.Extensions.DependencyInjection;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.OpenPHD2;

namespace TianWen.Lib.Extensions;

public static class PHD2ServiceCollectionExtensions
{
    public static IServiceCollection AddPHD2(this IServiceCollection services) => services.AddDevicSource<OpenPHD2GuiderDevice, OpenPHD2GuiderDriver>(uri => new OpenPHD2GuiderDevice(uri));
}