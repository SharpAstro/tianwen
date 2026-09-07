using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Threading;

namespace TianWen.Lib.Logging;

/// <summary>
/// A simple file-based logger provider that writes log entries to a single file.
/// Log path: <c>{CommonDataRoot}/Logs/{date}/{appName}_{timestamp}.log</c>, with the process id
/// appended (<c>{appName}_{timestamp}_{pid}.log</c>) only when that file already exists.
/// </summary>
/// <remarks>
/// The timestamp is to the second, so two processes of one app started within the same second
/// want the same file. Until 2026-09-07 the second one died at start-up with "the process cannot
/// access the file because it is being used by another process", before it had logged a line: a
/// launcher that ran <c>tianwen --version</c> and then <c>tianwen stack</c> lost the stack. The name
/// is opened with <see cref="FileMode.CreateNew"/> so a collision is detected rather than truncating
/// the other process's log, and only a collision changes the name.
/// </remarks>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly StreamWriter _writer;
    private readonly Lock _lock = new Lock();

    public FileLoggerProvider(string appName)
        : this(appName, SharedStaticData.CommonDataRoot.CreateSubdirectory("Logs")
            .CreateSubdirectory(TimeProvider.System.GetLocalNow().ToString("yyyyMMdd")).FullName)
    {
    }

    /// <summary>The provider over an explicit directory, for tests; the public constructor uses the
    /// app data <c>Logs/{date}</c> directory.</summary>
    internal FileLoggerProvider(string appName, string logDir)
    {
        // Machine-local wall clock (carries the local offset). Sourced from TimeProvider.System
        // rather than DateTime.Now so we never touch the banned BCL now-statics.
        var now = TimeProvider.System.GetLocalNow();
        var timestamp = now.ToString("yyyyMMdd'T'HH_mm_ss");

        var stream = TryCreateNew(Path.Combine(logDir, $"{appName}_{timestamp}.log"))
            ?? TryCreateNew(Path.Combine(logDir, $"{appName}_{timestamp}_{Environment.ProcessId}.log"))
            ?? throw new IOException($"Could not create a log file for {appName} under {logDir}: both the timestamped name and its process-id variant exist.");
        _writer = new StreamWriter(stream) { AutoFlush = true };

        WriteBanner(_writer, appName, now);
    }

    /// <summary>The file, created fresh, or null when it already exists (another process of this app
    /// started in the same second and owns it).</summary>
    private static FileStream? TryCreateNew(string path)
    {
        try
        {
            return new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        }
        catch (IOException) when (File.Exists(path))
        {
            return null;
        }
    }

    /// <summary>The file this provider writes to.</summary>
    public string LogFilePath => ((FileStream)_writer.BaseStream).Name;

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
