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
        // Machine-local wall clock (carries the local offset). Sourced from TimeProvider.System
        // rather than DateTime.Now so we never touch the banned BCL now-statics.
        var now = TimeProvider.System.GetLocalNow();
        var dateDir = now.ToString("yyyyMMdd");
        var timestamp = now.ToString("yyyyMMdd'T'HH_mm_ss");

        var logDir = SharedStaticData.CommonDataRoot.CreateSubdirectory("Logs").CreateSubdirectory(dateDir).FullName;

        var logFile = Path.Combine(logDir, $"{appName}_{timestamp}.log");
        _writer = new StreamWriter(logFile, append: false) { AutoFlush = true };

        WriteBanner(_writer, appName, now);
    }

    /// <summary>
    /// First line of every log file: which binary, from which commit, out of which install.
    /// <para>
    /// Written here rather than by each host because this constructor is the one choke point every
    /// binary passes through (CLI, Server, GUI, FitsViewer via <c>AddFileLogging</c>, ...), so a new
    /// host cannot forget it -- and because being written at file-creation time makes it the first
    /// line unconditionally, with no dependence on log filters or DI ordering.
    /// </para>
    /// <para>
    /// The gap it closes: until now a log opened straight into its first event, so a log sent in by
    /// a user identified neither the version nor the install. On this box the Store package sat at
    /// 6.3.1352.0 while the working tree was 7.0, and nothing in a log said which one you were
    /// reading. <see cref="BuildInfo.InstallFolder"/> is the tell-tale -- an MSIX path names the
    /// package, version and architecture outright.
    /// </para>
    /// <para>
    /// Best-effort by construction: a banner is a diagnostic, so failing to write one must never
    /// stop a process from starting or logging.
    /// </para>
    /// </summary>
    private static void WriteBanner(StreamWriter writer, string appName, DateTimeOffset now)
    {
        try
        {
            var stamp = now.ToString("HH:mm:ss.fff zzz");
            writer.WriteLine($"[{stamp}] [INF] {BannerCategory}: {appName} {BuildInfo.Describe()}");
            writer.WriteLine($"[{stamp}] [INF] {BannerCategory}: install {BuildInfo.InstallFolder}");
        }
        catch (IOException)
        {
            // The log file is already open; a failure here is not worth taking the process down for.
        }
    }

    /// <summary>Category the banner is filed under. Its own name so a reader can grep the provenance
    /// of a run without knowing which app wrote it.</summary>
    private const string BannerCategory = "TianWen.Build";

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
        // Carry the UTC offset (e.g. "12:34:56.789 +02:00") so log timestamps are unambiguous
        // across timezones. Machine-local via TimeProvider.System -- never the banned BCL now-statics.
        var timestamp = TimeProvider.System.GetLocalNow().ToString("HH:mm:ss.fff zzz");
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
