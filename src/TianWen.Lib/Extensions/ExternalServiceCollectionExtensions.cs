using System;
using TianWen.Lib.Devices;
using Microsoft.Extensions.DependencyInjection;
using TianWen.Lib.Connections;

namespace TianWen.Lib.Extensions;

public static class ExternalServiceCollectionExtensions
{
    public static IServiceCollection AddExternal(this IServiceCollection services) => services
        .AddSingleton<IUtf8TextBasedConnectionFactory, JsonRPCOverTcpConnectionFactory>()
        .AddSingleton<ITimeProvider, SystemTimeProvider>()
        .AddSingleton<IExternal, External>()
        // OS credential vault on Windows; owner-restricted file elsewhere. Both behind ICredentialStore.
        .AddSingleton<ICredentialStore>(sp => OperatingSystem.IsWindows()
            ? new WindowsCredentialStore()
            : new FileCredentialStore(sp.GetRequiredService<IExternal>()));
}
