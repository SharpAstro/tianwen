using Microsoft.Extensions.DependencyInjection;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.OnStep;

namespace TianWen.Lib.Extensions;

public static class OnStepServiceCollectionExtensions
{
    public static IServiceCollection AddOnStep(this IServiceCollection services) => services.AddDevicSource<OnStepDevice, OnStepDeviceSource>(uri => new OnStepDevice(uri));
}
