using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Threading;

namespace TianWen.Lib.Logging;

/// <summary>
/// A simple file-based logger provider that writes log entries to one file a day.
/// Log path: <c>{CommonDataRoot}/Logs/{date}/{appName}_{timestamp}.log</c>, with the process id
/// appended (<c>{appName}_{timestamp}_{pid}.log</c>) only when that file already exists.
/// </summary>
/// <remarks>
/// <para>
/// The timestamp is to the second, so two processes of one app started within the same second
/// want the same file. Until 2026-09-07 the second one died at start-up with "the process cannot
/// access the file because it is being used by another process", before it had logged a line: a
/// launcher that ran <c>tianwen --version</c> and then <c>tianwen stack</c> lost the stack. The name
/// is opened with <see cref="FileMode.CreateNew"/> so a collision is detected rather than truncating
/// the other process's log, and only a collision changes the name.
/// </para>
/// <para>
/// <b>A process that runs past local midnight moves to the new day's folder</b>, at the first line it
/// logs on the new day, and opens that file with the same banner. The day used to be fixed when the
/// process started, so a node that ran for a week wrote the whole week into the evening it started
/// (P1 of docs/plans/hardware-in-the-server.md, #917). If the new day's file cannot be made, the
/// provider keeps writing to the old one rather than lose a line, and tries again the next day.
/// </para>
/// </remarks>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _appName;
    private readonly string _logsRoot;
    private readonly TimeProvider _timeProvider;
    private StreamWriter _writer;
    // The local day the current file belongs to: every line checks it under the lock, and the one that
    // finds another day rolls the file.
    private DateOnly _day;
    // Shared by every FileLogger: StreamWriter is not thread-safe and an entry (message + exception) must
    // land as one block. A lock rather than a queue drained by a writer task because AutoFlush writes each
    // line before Log returns, which is what keeps the last lines before a crash. The one clause of the
    // standing rule this does not meet: a render thread that logs takes it (docs/todo/infra.md).
    private readonly Lock _lock = new Lock();

    public FileLoggerProvider(string appName)
        : this(appName, SharedStaticData.CommonDataRoot.CreateSubdirectory("Logs").FullName, TimeProvider.System)
    {
    }

    /// <summary>The provider over an explicit <c>Logs</c> folder and clock, for tests; the public constructor uses
    /// the app data <c>Logs</c> folder and the machine's clock.</summary>
    internal FileLoggerProvider(string appName, string logsRoot, TimeProvider timeProvider)
    {
        _appName = appName;
        _logsRoot = logsRoot;
        _timeProvider = timeProvider;

        // Machine-local wall clock (carries the local offset). Sourced from a TimeProvider rather than
        // DateTime.Now so we never touch the banned BCL now-statics.
        var now = timeProvider.GetLocalNow();
        _writer = Open(now);
        _day = DateOnly.FromDateTime(now.DateTime);
    }

    /// <summary>A new file under <paramref name="now"/>'s day folder, with the banner written.</summary>
    private StreamWriter Open(DateTimeOffset now)
    {
        var logDir = Directory.CreateDirectory(Path.Combine(_logsRoot, now.ToString("yyyyMMdd"))).FullName;
        var timestamp = now.ToString("yyyyMMdd'T'HH_mm_ss");

        var stream = TryCreateNew(Path.Combine(logDir, $"{_appName}_{timestamp}.log"))
            ?? TryCreateNew(Path.Combine(logDir, $"{_appName}_{timestamp}_{Environment.ProcessId}.log"))
            ?? throw new IOException($"Could not create a log file for {_appName} under {logDir}: both the timestamped name and its process-id variant exist.");
        var writer = new StreamWriter(stream) { AutoFlush = true };

        WriteBanner(writer, _appName, now);
        return writer;
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

    /// <summary>The file this provider writes to now.</summary>
    public string LogFilePath
    {
        get
        {
            lock (_lock)
            {
                return ((FileStream)_writer.BaseStream).Name;
            }
        }
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
        return new FileLogger(categoryName, this);
    }

    /// <summary>One entry, stamped with this provider's clock, in the current day's file.</summary>
    internal void Write(string level, string categoryName, string message, Exception? exception)
    {
        // Carry the UTC offset (e.g. "12:34:56.789 +02:00") so log timestamps are unambiguous across timezones.
        var now = _timeProvider.GetLocalNow();
        var timestamp = now.ToString("HH:mm:ss.fff zzz");
        var today = DateOnly.FromDateTime(now.DateTime);

        lock (_lock)
        {
            if (today != _day)
            {
                RollTo(today, now);
            }

            _writer.WriteLine($"[{timestamp}] [{level}] {categoryName}: {message}");

            if (exception is not null)
            {
                _writer.WriteLine(exception);
            }
        }
    }

    /// <summary>Moves to <paramref name="today"/>'s folder. Called under the lock.</summary>
    private void RollTo(DateOnly today, DateTimeOffset now)
    {
        // Whatever happens, this is the day now: a new file that cannot be made is tried again tomorrow, not
        // at every line of today.
        _day = today;
        var stamp = now.ToString("HH:mm:ss.fff zzz");

        var previous = _writer;
        try
        {
            _writer = Open(now);
        }
        catch (IOException ex)
        {
            previous.WriteLine($"[{stamp}] [WRN] {BannerCategory}: could not start the log for {today:yyyy-MM-dd}, carrying on here: {ex.Message}");
            return;
        }

        previous.WriteLine($"[{stamp}] [INF] {BannerCategory}: continued in {((FileStream)_writer.BaseStream).Name}");
        previous.Dispose();
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _writer.Dispose();
        }
    }
}

internal sealed class FileLogger(string categoryName, FileLoggerProvider provider) : ILogger
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Debug;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
        {
            return;
        }

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

        provider.Write(level, categoryName, formatter(state, exception), exception);
    }
}
