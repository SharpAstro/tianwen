using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Threading;

namespace TianWen.Lib.Logging;

/// <summary>
/// A simple file-based logger provider that writes log entries to a single file.
/// Log path: <c>{CommonDataRoot}/Logs/{date}/{appName}_{timestamp}.log</c>
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly StreamWriter _writer;
    private readonly Lock _lock = new Lock();

    public FileLoggerProvider(string appName)
    {
        var now = DateTime.Now;
        var dateDir = now.ToString("yyyyMMdd");
        var timestamp = now.ToString("yyyyMMdd'T'HH_mm_ss");

        var logDir = TianWenPaths.CommonDataRoot.CreateSubdirectory("Logs").CreateSubdirectory(dateDir).FullName;

        var logFile = Path.Combine(logDir, $"{appName}_{timestamp}.log");
        _writer = new StreamWriter(logFile, append: false) { AutoFlush = true };
    }

    public ILogger CreateLogger(string categoryName)
    {
        return new FileLogger(categoryName, _writer, _lock);
    }

    public void Dispose()
    {
        _writer.Dispose();
    }
}

internal sealed class FileLogger(string categoryName, StreamWriter writer, Lock @lock) : ILogger
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Debug;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
        {
            return;
        }

        var message = formatter(state, exception);
        var timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
        var level = logLevel switch
        {
            LogLevel.Trace => "TRC",
            LogLevel.Debug => "DBG",
            LogLevel.Information => "INF",
            LogLevel.Warning => "WRN",
            LogLevel.Error => "ERR",
            LogLevel.Critical => "CRT",
            _ => "???"
        };

        lock (@lock)
        {
            writer.WriteLine($"[{timestamp}] [{level}] {categoryName}: {message}");

            if (exception is not null)
            {
                writer.WriteLine(exception);
            }
        }
    }
}
