using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using TianWen.Lib.Logging;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The file logger: one file per process and day. Two processes of one app started within the same second used to
/// want the same log file, and the second died at start-up before it had logged a line (a launcher running
/// <c>tianwen --version</c> and then <c>tianwen stack</c> lost the stack, 2026-09-07); the name gets the process id
/// only on collision. And a process running past local midnight used to go on writing into the day it started,
/// which for a node that runs all week is every night's log in one evening's folder (P1 of
/// docs/plans/hardware-in-the-server.md, #917).
/// <para>
/// A line is queued by the thread that logs it and written by a thread of the provider's own (#541), so a test that
/// reads the file calls <see cref="FileLoggerProvider.Flush"/> first. The tests from the third on are that change's:
/// who writes, in what order and with which time, what closing, a crash and a full queue do.
/// </para>
/// </summary>
public class FileLoggerProviderTests : IDisposable
{
    private readonly TempFolders _folders = new TempFolders();

    public void Dispose() => _folders.Dispose();

    [Fact]
    public void TwoProvidersInOneSecondGetTwoFilesAndBothLog()
    {
        var root = NewLogsRoot();
        using var first = new FileLoggerProvider("Test", root, TimeProvider.System);
        using var second = new FileLoggerProvider("Test", root, TimeProvider.System);

        first.LogFilePath.ShouldNotBe(second.LogFilePath);
        Path.GetFileName(first.LogFilePath).ShouldMatch(@"^Test_\d{8}T\d{2}_\d{2}_\d{2}\.log$");
        Path.GetFileName(second.LogFilePath).ShouldMatch(@"^Test_\d{8}T\d{2}_\d{2}_\d{2}(_\d+)?\.log$");

        second.CreateLogger("t").LogInformation("second is alive");
        first.CreateLogger("t").LogInformation("first is alive");
        first.Flush().ShouldBeTrue();
        second.Flush().ShouldBeTrue();
        ReadOpenFile(second.LogFilePath).ShouldContain("second is alive");
        ReadOpenFile(first.LogFilePath).ShouldContain("first is alive");
    }

    [Fact]
    public void ALineLoggedAfterLocalMidnightGoesToTheNewDaysFolder()
    {
        // A local zone ahead of UTC, so the local and the UTC date differ around its midnight: the day is LOCAL.
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 9, 25, 13, 59, 58, TimeSpan.Zero));
        clock.SetLocalTimeZone(TimeZoneInfo.CreateCustomTimeZone("UTC+10", TimeSpan.FromHours(10), "UTC+10", "UTC+10"));
        using var provider = new FileLoggerProvider("Node", NewLogsRoot(), clock);
        var logger = provider.CreateLogger("t");

        logger.LogInformation("the evening's last line");
        provider.Flush().ShouldBeTrue();
        var evening = provider.LogFilePath;
        clock.Advance(TimeSpan.FromSeconds(4));
        logger.LogInformation("the first line after midnight");
        // The file follows the day of the last line WRITTEN, so the line is written before the file is asked for.
        provider.Flush().ShouldBeTrue();
        var morning = provider.LogFilePath;

        Path.GetFileName(Path.GetDirectoryName(evening)).ShouldBe("20260925");
        Path.GetFileName(Path.GetDirectoryName(morning)).ShouldBe("20260926");
        var eveningText = ReadOpenFile(evening);
        eveningText.ShouldContain("the evening's last line");
        eveningText.ShouldNotContain("the first line after midnight");
        eveningText.ShouldContain($"continued in {morning}");
        var morningText = ReadOpenFile(morning);
        morningText.ShouldContain("the first line after midnight");
        // The new day's file opens as every log does, with where the program came from.
        morningText.Split('\n').First().ShouldContain("TianWen.Build: Node");
    }

    // #541: a line is written by the provider's own thread, never by the thread that logs it, which only queues it;
    // its time is the logging thread's, and the lines of one thread stay in the order they were logged.
    [Fact]
    public void ALineIsWrittenByTheProvidersOwnThreadInTheOrderAndAtTheTimeItWasLogged()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 9, 25, 3, 0, 0, TimeSpan.Zero));
        var writers = new ConcurrentBag<(int Id, string? Name)>();
        using var provider = new FileLoggerProvider("Test", NewLogsRoot(), clock,
            beforeWrite: () => writers.Add((Environment.CurrentManagedThreadId, Thread.CurrentThread.Name)));
        var logger = provider.CreateLogger("t");

        for (var i = 0; i < 200; i++)
        {
            logger.LogInformation("line {Number}", i);
            clock.Advance(TimeSpan.FromMilliseconds(1));
        }
        provider.Flush().ShouldBeTrue();

        writers.Count.ShouldBe(200);
        writers.Select(static w => w.Name).Distinct().ShouldBe(["Test log writer"]);
        writers.ShouldNotContain(w => w.Id == Environment.CurrentManagedThreadId, "the thread that logs does not write");

        var lines = LinesAfterTheBanner(provider.LogFilePath, withTime: true);
        lines.Length.ShouldBe(200);
        lines[0].ShouldBe("[03:00:00.000 +00:00] [INF] t: line 0");
        lines[199].ShouldBe("[03:00:00.199 +00:00] [INF] t: line 199", "the 200th line carries the time it was logged at, and comes last");
    }

    // A provider that is closed writes what it still holds first, and refuses a line after that.
    [Fact]
    public void ClosingTheLogWritesWhatIsQueuedAndALineAfterThatIsRefused()
    {
        var provider = new FileLoggerProvider("Test", NewLogsRoot(), TimeProvider.System);
        var logger = provider.CreateLogger("t");
        for (var i = 0; i < 500; i++)
        {
            logger.LogInformation("queued {Number}", i);
        }
        var path = provider.LogFilePath;

        provider.Dispose();

        LinesAfterTheBanner(path).ShouldBe(Enumerable.Range(0, 500).Select(static i => $"[INF] t: queued {i}"));
        Should.Throw<ObjectDisposedException>(() => logger.LogInformation("after it closed"));
        provider.Flush().ShouldBeFalse("a closed log has no writer to wait for");
        provider.LinesFailed.ShouldBe(0);
        provider.Dispose(); // a second close is nothing
    }

    // #541, the crash story: the unhandled-exception handler logs its line and then WAITS for the file log to write it.
    // The writer is held at its first line, so the handler cannot have returned on luck: it is still waiting when the
    // writer has the line in hand, and returns once the line is on disk.
    [Fact(Timeout = 30_000)]
    public async Task TheCrashHandlerReturnsOnlyOnceTheFileLogHasWrittenItsLine()
    {
        var ct = TestContext.Current.CancellationToken;
        var writerHasTheLine = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        using var provider = new FileLoggerProvider("Test", NewLogsRoot(), TimeProvider.System, beforeWrite: () =>
        {
            writerHasTheLine.TrySetResult();
            release.Wait(); // set by this test, in its finally at the latest
        });
        using var factory = LoggerFactory.Create(builder => builder.AddProvider(provider));
        var crash = UnhandledExceptionLog.Crash(factory.CreateLogger("TianWen.Test"));
        try
        {
            var handler = Task.Factory.StartNew(() => crash(UnhandledExceptions.Ending, new InvalidOperationException("boom")),
                ct, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            await writerHasTheLine.Task.WaitAsync(ct);

            handler.IsCompleted.ShouldBeFalse("the handler waits for the line to be written, and the writer is held");
            release.Set();
            await handler.WaitAsync(ct);

            var text = ReadOpenFile(provider.LogFilePath);
            text.ShouldContain($"[CRT] TianWen.Test: {UnhandledExceptions.Ending}");
            text.ShouldContain("System.InvalidOperationException: boom");
        }
        finally
        {
            release.Set();
        }
    }

    // A writer that is held up (a disk that has stopped taking writes) does not hold up the thread that logs, which is
    // the point of #541; past the cap a line is dropped, and the file says how many at the place they are missing.
    // An Error or a Critical line is taken all the same: it is the line a reader of a log that fell behind is looking
    // for, a crash's own line among them, which the lock this replaced would have waited for and written.
    [Fact(Timeout = 30_000)]
    public async Task PastItsCapALineIsDroppedAndTheFileSaysHowManyWhereTheyAreMissing()
    {
        var ct = TestContext.Current.CancellationToken;
        var writerHolds = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        using var provider = new FileLoggerProvider("Test", NewLogsRoot(), TimeProvider.System, beforeWrite: () =>
        {
            writerHolds.TrySetResult();
            release.Wait(); // set by this test, in its finally at the latest
        }, mostQueued: 10);
        var logger = provider.CreateLogger("t");
        try
        {
            // The writer has the first line in hand and is held there.
            logger.LogInformation("first");
            await writerHolds.Task.WaitAsync(ct);

            // The thread that logs is not held with it: ten lines wait, and three more find the queue full.
            for (var i = 0; i < 10; i++)
            {
                logger.LogInformation("kept {Number}", i);
            }
            for (var i = 0; i < 3; i++)
            {
                logger.LogInformation("lost {Number}", i);
            }
            provider.LinesDropped.ShouldBe(3);
            logger.LogError("an error is kept");
            logger.LogCritical("and so is the line of a crash");
            logger.LogWarning("a warning is not");
            provider.LinesDropped.ShouldBe(4);

            release.Set();
            provider.Flush().ShouldBeTrue();
            logger.LogInformation("after");
            provider.Flush().ShouldBeTrue();

            string[] expected =
            [
                "[INF] t: first",
                .. Enumerable.Range(0, 10).Select(static i => $"[INF] t: kept {i}"),
                "[WRN] TianWen.Build: 3 line(s) were dropped here: more than 10 waited to be written",
                "[ERR] t: an error is kept",
                "[CRT] t: and so is the line of a crash",
                "[WRN] TianWen.Build: 1 line(s) were dropped here: more than 10 waited to be written",
                "[INF] t: after",
            ];
            LinesAfterTheBanner(provider.LogFilePath).ShouldBe(expected);
            provider.LinesDropped.ShouldBe(4, "the count is the provider's whole life's, said or not");
        }
        finally
        {
            release.Set();
        }
    }

    // The writer thread that dies of an exception of its own runs the crash handler itself: a flush asked there cannot
    // wait for the writer, which is the thread asking, so it writes the queue where it stands.
    [Fact(Timeout = 30_000)]
    public async Task AFlushAskedOnTheWriterThreadWritesTheQueueItself()
    {
        var ct = TestContext.Current.CancellationToken;
        FileLoggerProvider? made = null;
        ILogger? crashLogger = null;
        var asked = 0;
        var flushedOnTheWriter = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var provider = new FileLoggerProvider("Test", NewLogsRoot(), TimeProvider.System, beforeWrite: () =>
        {
            // Once, as the first line is written: what the crash handler does, a last line and then the drain.
            if (Interlocked.Exchange(ref asked, 1) == 0 && made is { } log && crashLogger is { } logger)
            {
                logger.LogCritical("the writer's own last line");
                flushedOnTheWriter.SetResult(log.Flush());
            }
        });
        crashLogger = provider.CreateLogger("t");
        made = provider;

        crashLogger.LogInformation("an ordinary line");

        (await flushedOnTheWriter.Task.WaitAsync(ct)).ShouldBeTrue(
            "the flush wrote the queue itself, and did not use its budget up waiting for its own thread");
        // Asked only now: a mark queued earlier is set by the writer's own drain, with the ordinary line still in its hand.
        provider.Flush().ShouldBeTrue();
        // The line the writer had in hand when it asked comes after the one it wrote from inside.
        LinesAfterTheBanner(provider.LogFilePath).ShouldBe(["[CRT] t: the writer's own last line", "[INF] t: an ordinary line"]);
    }

    // A disk that refuses the banner (full, or the file gone) must not stop a program from starting: the banner is a
    // diagnostic. The provider opens over a file whose every write fails, which is what its constructor meets on such a
    // disk; a line logged then is counted as failed, and closing does not throw either.
    [Fact]
    public void ADiskThatRefusesTheBannerDoesNotStopTheLogFromOpening()
    {
        FileLoggerProvider? provider = null;
        Should.NotThrow(() => provider = new FileLoggerProvider("Test", NewLogsRoot(), TimeProvider.System,
            createFile: static path => new FullDiskFile(path)));
        var opened = provider.ShouldNotBeNull();
        try
        {
            File.Exists(opened.LogFilePath).ShouldBeTrue();
            opened.CreateLogger("t").LogInformation("a line the disk refuses too");
            opened.Flush().ShouldBeTrue("the writer got there; what it could not write is counted, not waited for");
            opened.LinesFailed.ShouldBeGreaterThan(0);
        }
        finally
        {
            Should.NotThrow(opened.Dispose);
        }
    }

    // The lines that pass the cap have a bound of their own: an Error logged in a loop against a writer that is stuck must
    // not grow the queue for ever, which is what the cap is for. Ten times the cap, then they are dropped and counted too.
    [Fact(Timeout = 30_000)]
    public async Task AnErrorPastTenTimesTheCapIsDroppedToo()
    {
        var ct = TestContext.Current.CancellationToken;
        var writerHolds = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        using var provider = new FileLoggerProvider("Test", NewLogsRoot(), TimeProvider.System, beforeWrite: () =>
        {
            writerHolds.TrySetResult();
            release.Wait(); // set by this test, in its finally at the latest
        }, mostQueued: 2);
        var logger = provider.CreateLogger("t");
        try
        {
            logger.LogInformation("first");
            await writerHolds.Task.WaitAsync(ct);

            // Twenty errors wait (ten times the cap of two); the twenty-first is one too many.
            for (var i = 0; i < 20; i++)
            {
                logger.LogError("error {Number}", i);
            }
            provider.LinesDropped.ShouldBe(0);
            logger.LogError("error 20");
            provider.LinesDropped.ShouldBe(1);

            release.Set();
            provider.Flush().ShouldBeTrue();

            string[] expected =
            [
                "[INF] t: first",
                .. Enumerable.Range(0, 20).Select(static i => $"[ERR] t: error {i}"),
                "[WRN] TianWen.Build: 1 line(s) were dropped here: more than 2 waited to be written",
            ];
            LinesAfterTheBanner(provider.LogFilePath).ShouldBe(expected);
        }
        finally
        {
            release.Set();
        }
    }

    /// <summary>A log file whose every write fails, as a full disk's does. It is made, so it has a name.</summary>
    private sealed class FullDiskFile(string path) : FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read)
    {
        private static IOException Full() => new IOException("There is not enough space on the disk.");

        public override void Write(byte[] buffer, int offset, int count) => throw Full();
        public override void Write(ReadOnlySpan<byte> buffer) => throw Full();
        public override void WriteByte(byte value) => throw Full();
    }

    private string NewLogsRoot() => _folders.Create("filelog").FullName;

    /// <summary>The provider holds its file for writing, so a reader has to permit that.</summary>
    private static string ReadOpenFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>The file's lines after the two the banner takes, without their time unless asked for.</summary>
    private static string[] LinesAfterTheBanner(string path, bool withTime = false)
        => [.. ReadOpenFile(path).Split('\n', StringSplitOptions.RemoveEmptyEntries).Skip(2)
            .Select(static line => line.TrimEnd('\r'))
            .Select(line => withTime ? line : line[(line.IndexOf("] [", StringComparison.Ordinal) + 2)..])];
}
