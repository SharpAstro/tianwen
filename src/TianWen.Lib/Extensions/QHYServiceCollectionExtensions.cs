using TianWen.Lib.Devices;
using TianWen.Lib.Devices.QHYCCD;
using Microsoft.Extensions.DependencyInjection;

namespace TianWen.Lib.Extensions;

public static class QHYServiceCollectionExtensions
{
    public static IServiceCollection AddQHY(this IServiceCollection services) => services
        .AddDevicSource<QHYDevice, QHYDeviceSource>(uri => new QHYDevice(uri))
        .AddSerialProbe<QhyCfw3SerialProbe>()
        .AddSerialProbe<QfocSerialProbe>();
}
