using System;
using TianWen.Lib.Devices;
using Microsoft.Extensions.DependencyInjection;
using TianWen.Lib.Connections;

namespace TianWen.Lib.Extensions;

public static class ExternalServiceCollectionExtensions
{
    public static IServiceCollection AddExternal(this IServiceCollection services) => services
        .AddSingleton<IUtf8TextBasedConnectionFactory, JsonRPCOverTcpConnectionFactory>()
        // Single clock for the whole system. When the TIANWEN_NOW startup override is active, wrap
        // the system clock in an OffsetTimeProvider here -- planner, session, fake mount/camera and
        // mount-reported UTC all resolve ITimeProvider from DI, so they shift together (dev/test only).
        .AddSingleton<ITimeProvider>(static sp => StartupTimeOverride.TryGet(out _, out var offset)
            ? new SystemTimeProvider(new OffsetTimeProvider(TimeProvider.System, offset))
            : new SystemTimeProvider())
        .AddSingleton<IExternal, External>()
        // OS credential vault on Windows; owner-restricted file elsewhere. Both behind ICredentialStore.
        .AddSingleton<ICredentialStore>(sp => OperatingSystem.IsWindows()
            ? new WindowsCredentialStore()
            : new FileCredentialStore(sp.GetRequiredService<IExternal>()));
}
