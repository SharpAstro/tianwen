using System;
using System.IO;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TianWen.Hosting.Dto;
using TianWen.Lib.Devices;

namespace TianWen.Hosting;

/// <summary>
/// Where a node keeps its crash journal, and what it knows about the node before it. A host with no socket (a test's
/// node on TCP alone) keeps none: <see cref="None"/>.
/// </summary>
/// <param name="Path">The journal file (<see cref="NodeJournal.PathFor"/>), or null for no journal.</param>
/// <param name="AfterCrashOf">The node its keeper saw crash just before starting this one (<c>--after-crash</c>).</param>
/// <param name="LastBoot">When the machine last booted (<see cref="MachineBoot.LastBootUtc"/>), asked only when a
/// journal is found.</param>
/// <param name="WallClock">The machine's real clock, which a journal's age is judged on; never the node's own clock,
/// which a simulated <c>TIANWEN_NOW</c> shifts.</param>
public sealed record NodeJournalOptions(string? Path, int? AfterCrashOf, Func<DateTimeOffset?> LastBoot, TimeProvider WallClock)
{
    public static NodeJournalOptions None { get; } = new NodeJournalOptions(null, null, static () => null, TimeProvider.System);

    /// <summary>The journal of the node at <paramref name="socketPath"/>, as <c>tianwen-server</c> keeps it.</summary>
    public static NodeJournalOptions For(string socketPath, int? afterCrashOf) =>
        new NodeJournalOptions(NodeJournal.PathFor(socketPath), afterCrashOf, MachineBoot.LastBootUtc, TimeProvider.System);
}

/// <summary>
/// Keeps the node's crash journal (<see cref="NodeJournal"/>) in step with what the node holds, and reports the one a
/// node before it left behind (P1 of docs/plans/hardware-in-the-server.md, #917).
/// </summary>
/// <remarks>
/// <para>
/// <b>Written as the holdings change</b>: a device connecting or going, a camera's cooler intent, a run starting or
/// ending each write it at once, and a poll every <see cref="PollInterval"/> catches what raises nothing (the target a
/// run moves on to). Only a CHANGE is written, atomically, one writer at a time.
/// </para>
/// <para>
/// <b>A node that holds nothing leaves no journal</b>, so one that stops cleanly, having released everything, leaves
/// none, and one whose stop ran out of time leaves the state it got to: a camera still warming is a warm-up to finish.
/// The journal a node before it left is kept until this node first holds something of its own (or it is dismissed, or
/// this node stops cleanly), so a node that dies before it holds anything leaves the next one the same report.
/// </para>
/// <para>
/// Registered to stop AFTER <see cref="HostedSession"/> (hosted services stop in reverse order), so it sees the rig
/// come down before it writes the last of it.
/// </para>
/// </remarks>
internal sealed class NodeJournalService(
    NodeJournalOptions options,
    HostedSession hosted,
    IDeviceHub hub,
    IExternal external,
    ITimeProvider timeProvider,
    ILogger<NodeJournalService> logger) : IHostedService, IDisposable
{
    /// <summary>How often the journal is checked for what changed without raising anything.</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    // One pending "look again" at most: a burst of changes is one write of where they ended up.
    private readonly Channel<bool> _changed = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });
    private readonly CancellationTokenSource _stopping = new CancellationTokenSource();
    private Task _writer = Task.CompletedTask;
    private Task _poll = Task.CompletedTask;

    // Only the writer loop touches these, and StopAsync once the loop has ended.
    private NodeJournal? _written;
    private bool _predecessorOnDisk;

    private NodeRecoveryDto? _recovery;
    private int _dismissed;

    /// <summary>What the node before this one left when it died, until a client dismisses it.</summary>
    public NodeRecoveryDto? Recovery => Volatile.Read(ref _recovery);

    /// <summary>
    /// A client has seen the report: it goes from <c>GET /api/v1/node</c>, and so does the journal it came from, unless
    /// this node has written its own over it already.
    /// </summary>
    public void DismissRecovery()
    {
        Volatile.Write(ref _recovery, null);
        Volatile.Write(ref _dismissed, 1);
        _changed.Writer.TryWrite(true);
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (options.Path is not { } path)
        {
            return;
        }

        if (await external.TryReadJsonAsync(path, NodeJournalJsonContext.Default.NodeJournal, logger, cancellationToken) is { } found)
        {
            _predecessorOnDisk = true;
            var recovery = found.Recover(options.AfterCrashOf, options.WallClock.GetUtcNow(), found.ProcessId == options.AfterCrashOf ? null : options.LastBoot());
            Volatile.Write(ref _recovery, recovery);
            logger.LogWarning(
                "The node before this one (pid {Pid}) died holding {Devices} device(s){Run}; its journal was last written {Written:o}{Why}",
                found.ProcessId, recovery.Devices.Length,
                recovery.InterruptedRun is { } run ? $" and a {run.Kind} run started {run.StartedUtc:o}" : "",
                found.WrittenUtc,
                recovery.AfterCrash ? ", just before it crashed" : recovery.Stale ? ", before the machine last booted, so it describes a rig nobody knows the state of" : "");
        }

        hub.DeviceStateChanged += OnDeviceStateChanged;
        hub.CoolerIntentChanged += OnChanged;
        hosted.RunChanged += OnRunChanged;

        _writer = Task.Run(() => WriteAsync(path, _stopping.Token), CancellationToken.None);
        _poll = Task.Run(() => PollAsync(_stopping.Token), CancellationToken.None);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (options.Path is not { } path)
        {
            return;
        }

        hub.DeviceStateChanged -= OnDeviceStateChanged;
        hub.CoolerIntentChanged -= OnChanged;
        hosted.RunChanged -= OnRunChanged;
        await _stopping.CancelAsync();
        await AwaitEndedAsync(_poll);
        await AwaitEndedAsync(_writer);

        // The last word: what the node holds as it exits, which after a clean stop is nothing.
        try
        {
            await WriteIfChangedAsync(path, stopping: true, CancellationToken.None);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogError(ex, "Could not write the node's journal as it stopped");
        }
    }

    public void Dispose() => _stopping.Dispose();

    private void OnDeviceStateChanged(object? sender, DeviceConnectedEventArgs e) => _changed.Writer.TryWrite(true);

    private void OnChanged(object? sender, EventArgs e) => _changed.Writer.TryWrite(true);

    private void OnRunChanged() => _changed.Writer.TryWrite(true);

    private async Task PollAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await timeProvider.SleepAsync(PollInterval, cancellationToken);
                _changed.Writer.TryWrite(true);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Stopping.
        }
    }

    private async Task WriteAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var _ in _changed.Reader.ReadAllAsync(cancellationToken))
            {
                try
                {
                    await WriteIfChangedAsync(path, stopping: false, cancellationToken);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    logger.LogError(ex, "Could not write the node's journal {Path}; trying again at the next change", path);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Stopping: StopAsync writes the last of it.
        }
    }

    private async Task WriteIfChangedAsync(string path, bool stopping, CancellationToken cancellationToken)
    {
        var now = NodeJournal.Of(hub, hosted.CurrentRunRecord, hosted.CurrentSession, options.WallClock.GetUtcNow(), Environment.ProcessId);

        if (now.HoldsAnything)
        {
            if (_written is { } last && last.HoldsTheSameAs(now))
            {
                return;
            }
            await external.AtomicWriteJsonAsync(path, now, NodeJournalJsonContext.Default.NodeJournal, cancellationToken);
            _written = now;
            _predecessorOnDisk = false;
            logger.LogDebug("Journal: {Devices} device(s){Run}", now.Devices.Length, now.Run is { } run ? $", a {run.Kind} run" : "");
            return;
        }

        // Holding nothing: this node's own journal goes, and the one it found goes once it is dismissed or this node
        // stops cleanly, never before, so a node that dies holding nothing leaves the next one the same report.
        var ownOnDisk = _written is not null;
        var dropPredecessor = _predecessorOnDisk && (stopping || Volatile.Read(ref _dismissed) == 1);
        if (ownOnDisk || dropPredecessor)
        {
            File.Delete(path);
            _written = null;
            _predecessorOnDisk = false;
        }
    }

    private async Task AwaitEndedAsync(Task task)
    {
        try
        {
            await task;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "The node's journal loop failed");
        }
    }
}
