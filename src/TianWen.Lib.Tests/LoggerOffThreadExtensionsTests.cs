using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Shouldly;
using TianWen.Lib.Extensions;
using TianWen.Lib.Logging;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// A line written off the calling thread (<see cref="LoggerOffThreadExtensions.LogDebugOffThread"/>) whose write throws is
/// lost, never the process. #1621 moved the camera stream's and the capture reader's Debug lines onto the thread pool, and a
/// write that threw there (the file log closed, a full disk) came out of a pool work item, which ends the process, where the
/// same call made on the thread itself had thrown into that thread's catch (#1628). Each test finishing
/// at all is the process alive; the count is the lines it lost.
/// </summary>
public class LoggerOffThreadExtensionsTests : IDisposable
{
    private readonly TempFolders _folders = new TempFolders();

    public void Dispose() => _folders.Dispose();

    /// <summary>A logger whose every write throws, as a full disk's would, counting the writes it was asked for.</summary>
    private sealed class ThrowingLogger : ILogger
    {
        private int _writes;

        public int Writes => Volatile.Read(ref _writes);

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Interlocked.Increment(ref _writes);
            throw new IOException("There is not enough space on the disk.");
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task ALineWhoseWriteThrowsIsLostNotTheProcess()
    {
        var ct = TestContext.Current.CancellationToken;
        var logger = new ThrowingLogger();
        var lostBefore = LoggerOffThreadExtensions.LinesLost;

        logger.LogDebugOffThread("{Camera}'s stream is at gain {Gain}", "the camera", 2252);
        logger.LogDebugOffThread("Capture heartbeat ({Capture}): received {Received}", "the capture", 250);

        await UntilAsync(() => logger.Writes == 2 && LoggerOffThreadExtensions.LinesLost - lostBefore >= 2,
            () => $"{logger.Writes} writes, {LoggerOffThreadExtensions.LinesLost - lostBefore} lost", ct);
        logger.Writes.ShouldBe(2, "each line was written once, on the pool");
    }

    [Fact(Timeout = 30_000)]
    public async Task ALineQueuedAsTheFileLogClosesIsLostNotTheProcess()
    {
        // A host's own logger over its file log, the provider closed while a line it was handed is still queued: the logger
        // collects the provider's throw (a closed StreamWriter's) and rethrows it as an AggregateException, as the issue saw.
        var ct = TestContext.Current.CancellationToken;
        var provider = new FileLoggerProvider("Test", _folders.Create("offthread").FullName, TimeProvider.System);
        using var factory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Debug).AddProvider(provider));
        var logger = factory.CreateLogger("TianWen.Lib.Tests.OffThread");
        logger.LogDebug("the file log is open");
        provider.Dispose();
        Should.Throw<AggregateException>(() => logger.LogDebug("a line written after it closed")).InnerException.ShouldBeOfType<ObjectDisposedException>();
        var lostBefore = LoggerOffThreadExtensions.LinesLost;

        logger.LogDebugOffThread("{Camera}'s stream window is {Width}x{Height} after a pan", "the camera", 640, 480);

        await UntilAsync(() => LoggerOffThreadExtensions.LinesLost - lostBefore >= 1,
            () => $"{LoggerOffThreadExtensions.LinesLost - lostBefore} lost", ct);
    }

    /// <summary>
    /// Waits until <paramref name="condition"/> holds, bounded by the test's own timeout (its token) and nothing shorter, saying
    /// what it sees as it changes (<c>EventHubTests</c>' shape).
    /// </summary>
    private static async Task UntilAsync(Func<bool> condition, Func<string> seen, CancellationToken ct)
    {
        string? last = null;
        while (!condition())
        {
            if (seen() is var now && now != last)
            {
                TestContext.Current.TestOutputHelper?.WriteLine($"Waiting: {now}");
                last = now;
            }
            await Task.Delay(10, ct);
        }
    }
}
