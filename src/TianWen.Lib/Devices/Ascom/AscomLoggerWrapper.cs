using Microsoft.Extensions.Logging;
using AscomLogLevel = ASCOM.Common.Interfaces.LogLevel;

namespace TianWen.Lib.Devices.Ascom;

internal class AscomLoggerWrapper(ILogger logger) : ASCOM.Common.Interfaces.ILogger
{
    public AscomLogLevel LoggingLevel { get; private set; } = AscomLogLevel.Information;

    public void Log(AscomLogLevel level, string message) => logger.Log(level switch
    {
        AscomLogLevel.Debug => LogLevel.Debug,
        AscomLogLevel.Information => LogLevel.Information,
        AscomLogLevel.Warning => LogLevel.Warning,
        AscomLogLevel.Error => LogLevel.Error,
        _ => LogLevel.None
    }, "{Message}", message);

    public void SetMinimumLoggingLevel(AscomLogLevel level)
    {
        LoggingLevel = level;
    }
}
