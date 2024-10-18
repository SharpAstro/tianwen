using Astap.Lib.Devices;
using Astap.Lib.Devices.Fake;
using Microsoft.Extensions.DependencyInjection;

namespace Astap.Lib.Extensions;

public static class FakeServiceCollectionExtensions
{
    public static IServiceCollection AddFake(this IServiceCollection services) => services.AddSingleton<IDeviceSource<FakeDevice>, FakeDeviceSource>();
}
