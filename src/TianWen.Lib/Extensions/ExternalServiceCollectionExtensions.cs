using TianWen.Lib.Devices;
using Microsoft.Extensions.DependencyInjection;
using TianWen.Lib.Connections;

namespace TianWen.Lib.Extensions;

public static class ExternalServiceCollectionExtensions
{
    public static IServiceCollection AddExternal(this IServiceCollection services) => services
        .AddSingleton<IUtf8TextBasedConnectionFactory, JsonRPCOverTcpConnectionFactory>()
        .AddSingleton<IExternal, External>();
}