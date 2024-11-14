using Microsoft.Extensions.DependencyInjection;

namespace TianWen.Lib.Hosting.Extensions;

public static class HostedSessionServiceCollectionExtensions
{
    public static IServiceCollection AddHostedSession(this IServiceCollection services)
        => services.AddSingleton<IHostedSession, HostedSession>();
}
