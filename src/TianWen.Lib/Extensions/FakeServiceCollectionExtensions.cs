using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Fake;
using Microsoft.Extensions.DependencyInjection;

namespace TianWen.Lib.Extensions;

public static class FakeServiceCollectionExtensions
{
    public static IServiceCollection AddFake(this IServiceCollection services) => services.AddSingleton<IDeviceSource<DeviceBase>, FakeDeviceSource>();
}
