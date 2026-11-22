using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
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
/// <para>
/// <b>A line is queued by the thread that logs it and written by a thread of the provider's own</b> (#541). Every
/// logger of a process used to share one lock around a writer that flushed each line, so a render thread or
/// a camera's stream thread that logged waited on another thread's write to the file, which is what
/// <c>LogDebugOffThread</c> was written to get around, one call at a time. A line's time is taken where it is
/// logged, and the lines are written in the order they were queued.
/// </para>
/// <para>
/// <b>What that costs, and what covers it.</b> A line used to be handed to the operating system before <c>Log</c>
/// returned, so it outlived its process however that ended. Now a process that dies between the two loses what was
/// still queued. The queue is drained, and the caller waits for it (for at most <see cref="FlushBudget"/>), in the
/// unhandled-exception handler (<see cref="UnhandledExceptionLog"/>), at process exit and on
/// <see cref="Dispose"/>: a managed crash keeps its last lines as before. A native fail-fast or a kill runs none
/// of those, and loses the lines the writer had not reached: those logged in the time it takes the writer to wake
/// and write a batch.
/// </para>
/// </remarks>
public sealed class FileLoggerProvider : ILoggerProvider
{
    /// <summary>How long a drain waits for the writer: a crash handler and a process exit must not hang on a dead disk.</summary>
    internal static readonly TimeSpan FlushBudget = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The most lines that wait to be written. A disk that stops taking writes used to stop every thread that logged;
    /// now the queue would grow for as long as it lasted, so past this a line is dropped and counted
    /// (<see cref="LinesDropped"/>), and the file says how many at the place they are missing.
    /// <para>
    /// An Error or a Critical line is taken past it: it is the line a reader of a log that fell behind is looking
    /// for, a crash's own line among them, and the lock this replaced would have waited and written it. Those have a
    /// bound of their own, <see cref="UrgentHeadroom"/> times this, since an Error logged in a loop against a writer
    /// that is stuck would otherwise grow the queue for ever, which is what a cap is for.
    /// </para>
    /// </summary>
    internal const int MostQueued = 100_000;

    /// <summary>How many times <see cref="MostQueued"/> may wait before an Error or a Critical line is dropped too.</summary>
    internal const int UrgentHeadroom = 10;

    private static readonly ConcurrentDictionary<FileLoggerProvider, byte> Live = new();

    static FileLoggerProvider()
    {
        // A process that ends in the ordinary way still has lines queued: they are written before it goes.
        AppDomain.CurrentDomain.ProcessExit += static (_, _) => FlushAll();
    }

    private readonly string _appName;
    private readonly string _logsRoot;
    private readonly TimeProvider _timeProvider;
    // Test seam: run on the writer thread before each line is written.
    private readonly Action? _beforeWrite;
    // Test seam: makes the file at a path that must not exist yet (a test hands back one whose disk is full).
    private readonly Func<string, FileStream> _createFile;
    // The writer and the day its file belongs to are the writer thread's alone once it runs: the constructor opens the
    // first file, so that a provider that cannot log fails where it is made, and hands both over.
    private StreamWriter _writer;
    private DateOnly _day;
    private volatile string _logFilePath;

    private readonly int _mostQueued;
    private readonly ConcurrentQueue<Entry> _entries = new();
    // What the writer waits on. Released once for each wake it is asked for (_wakeAsked) and not once a line, so a wake
    // finds every line logged since the last one. Never disposed: a thread that passed the closed check as the log
    // closed still releases it, and a semaphore whose wait handle nobody asked for holds nothing to give back.
    private readonly SemaphoreSlim _queued = new(0);
    private readonly Thread _thread;
    private int _wakeAsked;
    private int _waiting;
    private long _dropped;
    private long _droppedUnsaid;
    private long _failed;
    private int _disposed;
    // Set by the writer as it takes its last entry: a mark queued from then on is not waited for.
    private volatile bool _ended;

    /// <summary>One line as it was logged, or (with <see cref="Written"/>) a mark the writer sets when it has written and
    /// flushed everything queued before it; <see cref="Last"/> ends the writer.</summary>
    private readonly record struct Entry(DateTimeOffset Now, string Level, string Category, string Message, Exception? Exception,
        ManualResetEventSlim? Written = null, bool Last = false);

    public FileLoggerProvider(string appName)
        : this(appName, SharedStaticData.CommonDataRoot.CreateSubdirectory("Logs").FullName, TimeProvider.System)
    {
    }

    /// <summary>The provider over an explicit <c>Logs</c> folder and clock, for tests; the public constructor uses
    /// the app data <c>Logs</c> folder and the machine's clock.</summary>
    /// <param name="beforeWrite">Run on the writer thread before each line is written: a test holds the writer there, or
    /// reads which thread it is.</param>
    /// <param name="mostQueued"><see cref="MostQueued"/>, which a test makes small.</param>
    /// <param name="createFile">Makes the log file at a path that must not exist yet; a test hands back one that refuses
    /// every write, as a full disk's does.</param>
    internal FileLoggerProvider(string appName, string logsRoot, TimeProvider timeProvider, Action? beforeWrite = null,
        int mostQueued = MostQueued, Func<string, FileStream>? createFile = null)
    {
        _createFile = createFile ?? (static path => new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read));
        _appName = appName;
        _logsRoot = logsRoot;
        _timeProvider = timeProvider;
        _beforeWrite = beforeWrite;
        _mostQueued = mostQueued;

        // Machine-local wall clock (carries the local offset). Sourced from a TimeProvider rather than
        // DateTime.Now so we never touch the banned BCL now-statics.
        var now = timeProvider.GetLocalNow();
        _writer = Open(now);
        _day = DateOnly.FromDateTime(now.DateTime);
        _logFilePath = ((FileStream)_writer.BaseStream).Name;
        Live[this] = 0;
        // A thread of its own and not a pool task: the lines of a process whose pool is starved are the ones wanted.
        // A background thread, so it never keeps a process alive; what it has not written is drained at exit.
        _thread = new Thread(WriteQueued) { IsBackground = true, Name = $"{appName} log writer" };
        _thread.Start();
    }

    /// <summary>A new file under <paramref name="now"/>'s day folder, with the banner written.</summary>
    private StreamWriter Open(DateTimeOffset now)
    {
        var logDir = Directory.CreateDirectory(Path.Combine(_logsRoot, now.ToString("yyyyMMdd"))).FullName;
        var timestamp = now.ToString("yyyyMMdd'T'HH_mm_ss");

        var stream = TryCreateNew(Path.Combine(logDir, $"{_appName}_{timestamp}.log"))
            ?? TryCreateNew(Path.Combine(logDir, $"{_appName}_{timestamp}_{Environment.ProcessId}.log"))
            ?? throw new IOException($"Could not create a log file for {_appName} under {logDir}: both the timestamped name and its process-id variant exist.");
        // Flushed by the writer thread after each batch it takes off the queue, and not after each line.
        try
        {
            var writer = new StreamWriter(stream) { AutoFlush = false };

            WriteBanner(writer, _appName, now);
            return writer;
        }
        catch
        {
            // Nothing above is expected to throw (the banner swallows what a disk refuses), so this is only the
            // promise that a file this made and cannot hand over is not left open.
            stream.Dispose();
            throw;
        }
    }

    /// <summary>The file, created fresh, or null when it already exists (another process of this app
    /// started in the same second and owns it).</summary>
    private FileStream? TryCreateNew(string path)
    {
        try
        {
            return _createFile(path);
        }
        catch (IOException) when (File.Exists(path))
        {
            return null;
        }
    }

    /// <summary>The file this provider writes to now: the one the last line WRITTEN went to, so a caller that has just
    /// logged across midnight calls <see cref="Flush"/> first.</summary>
    public string LogFilePath => _logFilePath;

    /// <summary>Lines dropped since the provider was made: because <see cref="MostQueued"/> were waiting already, or,
    /// for an Error or a Critical line, <see cref="UrgentHeadroom"/> times that.</summary>
    internal long LinesDropped => Interlocked.Read(ref _dropped);

    /// <summary>Lines the writer could not write (a full disk, a file gone).</summary>
    internal long LinesFailed => Interlocked.Read(ref _failed);

    /// <summary>
    /// Waits until every line logged before this call is written and flushed, for at most <see cref="FlushBudget"/>.
    /// False when the writer did not get there in time, or the provider is disposed.
    /// </summary>
    public bool Flush()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return false;
        }

        if (Thread.CurrentThread == _thread)
        {
            // The writer thread itself asks: it is dying of an exception of its own and runs the crash handler, so
            // nobody else would write what is queued, its crash line with it. Waiting for itself would use the whole
            // budget up and lose them.
            SayDropped(_timeProvider.GetLocalNow());
            return WriteWhatIsQueued();
        }

        return Mark(last: false);
    }

    /// <summary>Drains every live provider of the process: what a crash handler calls after its own line. False when
    /// one of them did not get there within <see cref="FlushBudget"/>.</summary>
    public static bool FlushAll()
    {
        var all = true;
        foreach (var (provider, _) in Live)
        {
            all &= provider.Flush();
        }
        return all;
    }

    private bool Mark(bool last)
    {
        SayDropped(_timeProvider.GetLocalNow());
        // Never disposed: the writer sets it when it gets there, which can be after this wait has given up, and an
        // event whose wait handle nobody asked for holds nothing to give back.
        var written = new ManualResetEventSlim();
        _entries.Enqueue(new Entry(default, "", "", "", null, written, last));
        Wake();
        // A writer that has ended is not waited for: every line it will ever write is written.
        return _ended ? written.IsSet : written.Wait(FlushBudget);
    }

    // One release for each wake and not for each line. The exchange here and the writer's are both full fences, which
    // the pair needs: this thread queues and then reads the flag, the writer clears the flag and then reads the queue,
    // so one of the two always sees the other.
    private void Wake()
    {
        if (Interlocked.Exchange(ref _wakeAsked, 1) == 0)
        {
            _queued.Release();
        }
    }

    // The lines dropped since the file last said so, said as a line of its own queued where they are missing: ahead of
    // the first line that is taken again, or of a flush.
    private void SayDropped(DateTimeOffset now)
    {
        if (Interlocked.Read(ref _droppedUnsaid) > 0 && Interlocked.Exchange(ref _droppedUnsaid, 0) is > 0 and var dropped)
        {
            Interlocked.Increment(ref _waiting);
            _entries.Enqueue(new Entry(now, "WRN", BannerCategory, $"{dropped} line(s) were dropped here: more than {_mostQueued} waited to be written", null));
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
            // The writer does not flush by itself: a disk that refuses the banner is refused here, inside the catch.
            writer.Flush();
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

    /// <summary>One entry, stamped with this provider's clock as it is logged, queued for the current day's file.</summary>
    /// <param name="pastTheCap">An Error or a Critical line, which is taken past <see cref="MostQueued"/>, up to
    /// <see cref="UrgentHeadroom"/> times it.</param>
    internal void Write(string level, string categoryName, string message, Exception? exception, bool pastTheCap = false)
    {
        // A closed log refuses a line, as its closed writer did: the caller hears of it and the line is not lost in silence.
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Interlocked.Increment(ref _waiting) > (pastTheCap ? (long)_mostQueued * UrgentHeadroom : _mostQueued))
        {
            Interlocked.Decrement(ref _waiting);
            Interlocked.Increment(ref _dropped);
            Interlocked.Increment(ref _droppedUnsaid);
            return;
        }

        // The time is the logging thread's, taken here and not where the line is written.
        var now = _timeProvider.GetLocalNow();
        SayDropped(now);
        _entries.Enqueue(new Entry(now, level, categoryName, message, exception));
        Wake();
    }

    // The writer thread: it waits to be woken, with no time limit of its own (it ends with its last entry, or with the
    // process: a background thread), and writes everything queued at each wake.
    private void WriteQueued()
    {
        do
        {
            _queued.Wait();
            // Cleared before the queue is read: a line queued after this asks for a wake of its own, and one queued
            // before it is in the queue this wake reads (Wake).
            Interlocked.Exchange(ref _wakeAsked, 0);
        }
        while (WriteWhatIsQueued());
    }

    /// <summary>Writes everything queued now and flushes once. False once the last entry was met and the file is
    /// closed. On the writer thread.</summary>
    private bool WriteWhatIsQueued()
    {
        var wrote = false;
        while (_entries.TryDequeue(out var entry))
        {
            if (entry.Written is not { } mark)
            {
                Interlocked.Decrement(ref _waiting);
                WriteOne(entry);
                wrote = true;
                continue;
            }

            TryFlush();
            wrote = false;
            if (entry.Last)
            {
                End(mark);
                return false;
            }
            mark.Set();
        }
        if (wrote)
        {
            TryFlush();
        }
        return true;
    }

    // The writer's last act. The flag goes up first, so that a mark queued from here on is not waited for.
    private void End(ManualResetEventSlim last)
    {
        _ended = true;
        try
        {
            _writer.Dispose();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or UnauthorizedAccessException)
        {
            // Closing writes what the stream still buffers, which a full disk refuses: on this thread nobody could catch it.
            Interlocked.Increment(ref _failed);
        }
        last.Set();
        // What a thread queued behind the last entry, having passed the closed check as the log closed: its line is lost
        // and counted, and its mark is set, since everything that will ever be written is.
        while (_entries.TryDequeue(out var late))
        {
            if (late.Written is { } mark)
            {
                mark.Set();
            }
            else
            {
                Interlocked.Increment(ref _failed);
            }
        }
    }

    private void WriteOne(in Entry entry)
    {
        _beforeWrite?.Invoke();
        try
        {
            var today = DateOnly.FromDateTime(entry.Now.DateTime);
            if (today != _day)
            {
                RollTo(today, entry.Now);
            }

            // Carry the UTC offset (e.g. "12:34:56.789 +02:00") so log timestamps are unambiguous across timezones.
            _writer.WriteLine($"[{entry.Now:HH:mm:ss.fff zzz}] [{entry.Level}] {entry.Category}: {entry.Message}");
            if (entry.Exception is not null)
            {
                _writer.WriteLine(entry.Exception);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or UnauthorizedAccessException)
        {
            // A full disk or a file gone costs the line, never the process: on this thread nobody could catch it.
            Interlocked.Increment(ref _failed);
        }
    }

    private void TryFlush()
    {
        try
        {
            _writer.Flush();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or UnauthorizedAccessException)
        {
            Interlocked.Increment(ref _failed);
        }
    }

    /// <summary>Moves to <paramref name="today"/>'s folder. On the writer thread.</summary>
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

        _logFilePath = ((FileStream)_writer.BaseStream).Name;
        previous.WriteLine($"[{stamp}] [INF] {BannerCategory}: continued in {_logFilePath}");
        previous.Dispose();
    }

    /// <summary>Writes what is queued, ends the writer thread and closes the file. A line logged after this is refused.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Live.TryRemove(this, out _);
        // The writer closes the file itself, after the last line: a writer that does not get there within the budget
        // (a dead disk) is left to its background thread, which ends with the process.
        if (Mark(last: true))
        {
            _thread.Join(FlushBudget);
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

        provider.Write(level, categoryName, formatter(state, exception), exception, pastTheCap: logLevel >= LogLevel.Error);
    }
}
