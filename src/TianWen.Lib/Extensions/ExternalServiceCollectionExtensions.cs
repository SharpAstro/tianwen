using TianWen.Lib.Devices;
using Microsoft.Extensions.DependencyInjection;
using TianWen.Lib.Connections;

namespace TianWen.Lib.Extensions;

public static class ExternalServiceCollectionExtensions
{
    public static IServiceCollection UseSystemExternal(this IServiceCollection services) => services
        .AddSingleton<IUtf8TextBasedConnectionFactory, JsonRPCOverTCPConnectionFactory>()
        .AddSingleton<IExternal, External>();
}