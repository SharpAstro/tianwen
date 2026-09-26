using Meziantou.Extensions.Logging.Xunit.v3;
using Microsoft.Extensions.Logging;
using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Hosting;
using TianWen.Lib.IO;
using Xunit;
using Xunit.v3;

namespace TianWen.Lib.Tests.Functional;

/// <summary>
/// A test waiting on a node: for what a node's crash journal holds, or for any other state the node reaches in its own
/// time. Bounded by the test's own <c>[Fact(Timeout)]</c> and nothing shorter, and saying what it saw as it waits.
/// </summary>
/// <remarks>
/// <para>These waits had budgets of their own, 10 s in process and 30 s against a real node: wall-clock limits inside
/// the test's own, which CLAUDE.md warns cause flakes. A node's first journal write is about 40 ms of work on a quiet
/// machine, and on this one, stalled by other work (a training run, a dataset bake, another repository's suite), tests
/// that take under a second have been measured taking 40 to 127 s; the 10 s budget ran out while the node had not yet
/// been scheduled (#940). A wait now lasts until the test's cancellation token, which xunit cancels at the test's own
/// timeout, and refuses to run in a test that declares none.</para>
/// <para>What a wait sees goes to the test's output whenever it changes, and every few seconds while it does not, with
/// the thread pool's state, through the same logger as the node's own log (<see cref="NodeHarness"/>), so the two
/// interleave by time. A test that times out then says whether the node was stuck or starved.</para>
/// <para>A journal is read the way a node reads it, through <see cref="SharedFile"/>: sharing read, write AND delete,
/// and believing the file gone only after looking again. The tests read it with <c>File.ReadAllText</c>, which shares
/// read only, and a reader that does not share delete refuses the node's replace for as long as it holds the file:
/// measured, a test polling every 20 ms made the median replace retry once, and a reader held 200 ms at a time (a
/// thread preempted on a loaded machine) stretched each replace to about a second. Through <see cref="SharedFile"/> the
/// same reader costs the replace nothing.</para>
/// </remarks>
internal static class NodeWait
{
    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(20);
    private static readonly TimeSpan Heartbeat = TimeSpan.FromSeconds(5);
    private static readonly object Reached = new object();

    /// <summary>Waits until the node's journal at <paramref name="path"/> <paramref name="holds"/>, and returns it.</summary>
    public static Task<NodeJournal> UntilTheJournalAsync(string path, Func<NodeJournal, bool> holds, CancellationToken ct) =>
        UntilAsync($"the journal {path}", async token =>
        {
            var (journal, seen) = await TryReadJournalAsync(path, token);
            return (journal is not null && holds(journal) ? journal : null, seen);
        }, ct);

    /// <summary>Waits until the node's journal at <paramref name="path"/> is gone, as a node that holds nothing leaves it.</summary>
    public static Task UntilTheJournalIsGoneAsync(string path, CancellationToken ct) =>
        UntilAsync($"the journal {path} to go", async token =>
        {
            var (journal, seen) = await TryReadJournalAsync(path, token);
            return (journal is null && seen == NoJournal, seen);
        }, ct);

    /// <summary>
    /// Waits until <paramref name="look"/> says it is <c>Done</c>; <c>Seen</c> is what it found, for the output.
    /// </summary>
    public static Task UntilAsync(string waitingFor, Func<CancellationToken, ValueTask<(bool Done, string Seen)>> look, CancellationToken ct) =>
        UntilAsync<object>(waitingFor, async token =>
        {
            var (done, seen) = await look(token);
            return (done ? Reached : null, seen);
        }, ct);

    /// <summary>
    /// Waits until <paramref name="look"/> finds a value, and returns it; <c>Seen</c> is what it found, for the output.
    /// </summary>
    public static async Task<T> UntilAsync<T>(string waitingFor, Func<CancellationToken, ValueTask<(T? Value, string Seen)>> look, CancellationToken ct)
        where T : class
    {
        if ((TestContext.Current.Test as ICoreTest)?.Timeout is not > 0)
        {
            throw new InvalidOperationException($"Waiting for {waitingFor} lasts as long as the test's own timeout, and this test declares none: give it [Fact(Timeout = ...)]");
        }

        var logger = TestContext.Current.TestOutputHelper is { } output
            ? XUnitLogger.CreateLogger(output, new XUnitLoggerOptions { IncludeLogLevel = true, TimestampFormat = "HH:mm:ss.fff" })
            : null;
        var clock = Stopwatch.StartNew();
        var nextHeartbeat = Heartbeat;
        string? lastSeen = null;
        while (true)
        {
            var (value, seen) = await look(ct);
            if (value is not null)
            {
                return value;
            }

            if (seen != lastSeen)
            {
                logger?.LogInformation("Waiting for {What}: {Seen}", waitingFor, seen);
                lastSeen = seen;
            }
            else if (clock.Elapsed >= nextHeartbeat)
            {
                logger?.LogInformation("Still waiting for {What} after {Seconds:0} s: {Seen}; thread pool {Threads} threads, {Queued} queued",
                    waitingFor, clock.Elapsed.TotalSeconds, seen, ThreadPool.ThreadCount, ThreadPool.PendingWorkItemCount);
                nextHeartbeat += Heartbeat;
            }

            // The test's own token: xunit cancels it at the test's timeout, which is what bounds the wait.
            await Task.Delay(Poll, ct);
        }
    }

    private const string NoJournal = "no journal";

    /// <summary>The journal at <paramref name="path"/>, read as a node reads it, or null and why there is none to read.</summary>
    public static async Task<(NodeJournal? Journal, string Seen)> TryReadJournalAsync(string path, CancellationToken ct)
    {
        try
        {
            await using var stream = await SharedFile.TryOpenReadAsync(path, ct);
            if (stream is null)
            {
                return (null, NoJournal);
            }

            var journal = await JsonSerializer.DeserializeAsync(stream, NodeJournalJsonContext.Default.NodeJournal, ct);
            return journal is null
                ? (null, "an empty journal")
                : (journal, $"pid {journal.ProcessId}'s journal, written {journal.WrittenUtc:HH:mm:ss.fff} UTC, reconnecting {journal.Touching ?? "nothing"}, holding {journal.Devices.Length} device(s)");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return (null, $"{ex.GetType().Name}: {ex.Message}");
        }
    }
}
