using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace TianWen.Lib.Logging;

public static class FileLoggingExtensions
{
    /// <summary>
    /// Adds file logging to the service collection.
    /// Logs are written to <c>{CommonDataRoot}/Logs/{date}/{appName}_{timestamp}.log</c>.
    /// </summary>
    public static IServiceCollection AddFileLogging(this IServiceCollection services, string appName)
    {
        return services.AddLogging(builder =>
        {
            builder.AddProvider(new FileLoggerProvider(appName));
        });
    }
}
