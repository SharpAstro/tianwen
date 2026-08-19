using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace TianWen.Lib.Logging;

public static class FileLoggingExtensions
{
    /// <summary>
    /// Adds file logging to the service collection.
    /// Logs are written to <c>{CommonDataRoot}/Logs/{date}/{appName}_{timestamp}.log</c>.
    /// </summary>
    /// <param name="minimumLevel">
    /// Minimum level to record. Omit to keep the logging factory's own default, which is
    /// <see cref="LogLevel.Information"/> -- note that this is what the FILTER admits, and is a
    /// separate thing from what <c>FileLogger</c> is willing to write (Debug and above). A caller
    /// that wants Debug in its file has to say so here; the provider allowing it is not enough.
    /// </param>
    public static IServiceCollection AddFileLogging(this IServiceCollection services, string appName,
        LogLevel? minimumLevel = null)
    {
        return services.AddLogging(builder =>
        {
            builder.AddProvider(new FileLoggerProvider(appName));
            if (minimumLevel is { } level)
            {
                builder.SetMinimumLevel(level);
            }
        });
    }
}
