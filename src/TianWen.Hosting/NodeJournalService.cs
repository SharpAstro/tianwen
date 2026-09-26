using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TianWen.Hosting.Dto;
using TianWen.Lib.Devices;
using TianWen.Lib.Sequencing;

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
/// Keeps the node's crash journal (<see cref="NodeJournal"/>) in step with what the node holds, and reports and acts on
/// the one a node before it left behind (P1 of docs/plans/hardware-in-the-server.md, #917).
/// </summary>
/// <remarks>
/// <para>
/// <b>Written as the holdings change</b>: a device connecting or going, a camera's cooler intent, a run starting or
/// ending each write it at once, and a poll every <see cref="PollInterval"/> catches what raises nothing (the target a
/// run moves on to). Only a CHANGE is written, atomically, by one writer: the task that also recovers.
/// </para>
/// <para>
/// <b>A node that holds nothing leaves no journal</b>, so one that stops cleanly, having released everything, leaves
/// none, and one whose stop ran out of time leaves the state it got to: a camera still warming is a warm-up to finish.
/// The journal a node before it left is kept until this node first holds something of its own (or it is dismissed, or
/// this node stops cleanly), so a node that dies before it holds anything leaves the next one the same report.
/// </para>
/// <para>
/// <b>A journal it can believe, it acts on as it starts</b> (not stale, not a crash loop): it reconnects the devices,
/// the mount first, which restores mount-limit enforcement, naming each in its own journal BEFORE the connect, so a
/// driver that crashes the node there is named for the next one; then it re-establishes each camera's cooling from its
/// intent, through the session's own ramp (<see cref="DeviceHubCameraSafetyExtensions"/>): a cool-down to the target,
/// a warm-up from wherever the sensor now is, never a jump. It never resumes a run. Those ramps stop when a run starts
/// (the session cools its own cameras) and as the host starts to stop (its stop warms them).
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
    IHostApplicationLifetime lifetime,
    ILogger<NodeJournalService> logger) : IHostedService, IDisposable
{
    /// <summary>How often the journal is checked for what changed without raising anything.</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    /// <summary>How long one device gets to reconnect from a journal before it is reported as not reconnected.</summary>
    public static readonly TimeSpan ReconnectBudget = TimeSpan.FromSeconds(60);

    // One pending "look again" at most: a burst of changes is one write of where they ended up.
    private readonly Channel<bool> _changed = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });
    private readonly CancellationTokenSource _stopping = new CancellationTokenSource();
    // The ramps a recovery started: ended by a run starting, and by the host starting to stop.
    private readonly CancellationTokenSource _recooling = new CancellationTokenSource();
    private CancellationTokenRegistration _onStopping;
    private Task _writer = Task.CompletedTask;
    private Task _poll = Task.CompletedTask;

    // Only the writer task touches these, and StopAsync once it has ended.
    private NodeJournal? _written;
    private bool _predecessorOnDisk;
    private ImmutableArray<DateTimeOffset> _crashes = [];
    private ImmutableArray<NodeJournalDevice> _pending = [];
    private string? _touching;
    private NodeJournalRun? _carriedRun;
    private readonly List<Task> _ramps = [];

    private NodeRecoveryDto? _recovery;
    private int _dismissed;
    private int _rampsRunning;

    /// <summary>How many cooling ramps a recovery has running: the tests' view of a ramp ending when a run starts.</summary>
    internal int RampsRunning => Volatile.Read(ref _rampsRunning);

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

        NodeJournal? believed = null;
        if (await external.TryReadJsonAsync(path, NodeJournalJsonContext.Default.NodeJournal, logger, cancellationToken) is { } found)
        {
            _predecessorOnDisk = true;
            var foundUtc = options.WallClock.GetUtcNow();
            var recovery = found.Recover(options.AfterCrashOf, foundUtc, found.ProcessId == options.AfterCrashOf ? null : options.LastBoot());
            Volatile.Write(ref _recovery, recovery);
            logger.LogWarning(
                "The node before this one (pid {Pid}) died holding {Devices} device(s){Run}; its journal was last written {Written:o}{Why}",
                found.ProcessId, recovery.Devices.Length,
                recovery.InterruptedRun is { } run ? $" and a {run.Kind} run started {run.StartedUtc:o}" : "",
                found.WrittenUtc,
                recovery.AfterCrash ? ", just before it crashed" : recovery.Stale ? ", before the machine last booted, so it describes a rig nobody knows the state of" : "");

            if (!recovery.Stale)
            {
                // A boot wipes the crash history with the rig's state; otherwise this crash joins it.
                _crashes = found.CrashesWith(foundUtc);
                if (recovery.CrashLoop)
                {
                    // Nothing carried either: the journal on disk stays the one that names the suspect, until dismissed.
                    logger.LogError("The node has crashed {Crashes} times within {Window}; reconnecting nothing{Suspect}",
                        _crashes.Length, NodeKeeper.CrashLoopWindow, found.Touching is { } suspect ? $", as it died reconnecting {suspect}" : "");
                }
                else
                {
                    believed = found;
                    // The run it died in stays in this node's journal (it resumes nothing) until the report is dismissed.
                    _carriedRun = found.Run;
                }
            }
        }

        hub.DeviceStateChanged += OnDeviceStateChanged;
        hub.CoolerIntentChanged += OnChanged;
        hosted.RunChanged += OnRunChanged;
        _onStopping = lifetime.ApplicationStopping.Register(EndRecooling);

        _writer = Task.Run(() => RecoverThenWriteAsync(path, believed, _stopping.Token), CancellationToken.None);
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
        await _onStopping.DisposeAsync();
        EndRecooling();
        await _stopping.CancelAsync();
        await AwaitEndedAsync(_poll);
        await AwaitEndedAsync(_writer);
        foreach (var ramp in _ramps)
        {
            await AwaitEndedAsync(ramp);
        }

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

    public void Dispose()
    {
        _stopping.Dispose();
        _recooling.Dispose();
    }

    private void OnDeviceStateChanged(object? sender, DeviceConnectedEventArgs e) => _changed.Writer.TryWrite(true);

    private void OnChanged(object? sender, EventArgs e) => _changed.Writer.TryWrite(true);

    private void OnRunChanged()
    {
        // A run cools its own cameras: a recovery's ramp on the same camera would fight it.
        if (hosted.IsRunning)
        {
            EndRecooling();
        }
        _changed.Writer.TryWrite(true);
    }

    private void EndRecooling()
    {
        try
        {
            _recooling.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Disposed with the service: nothing left to end.
        }
    }

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

    private async Task RecoverThenWriteAsync(string path, NodeJournal? believed, CancellationToken cancellationToken)
    {
        try
        {
            if (believed is not null)
            {
                await RecoverAsync(path, believed, cancellationToken);
            }

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

    /// <summary>
    /// Reconnects what the node before this one held, the mount first (it restores mount-limit enforcement), then the
    /// cameras (their cooling is next), then the rest, and re-establishes each camera's cooling from its intent.
    /// </summary>
    private async Task RecoverAsync(string path, NodeJournal found, CancellationToken cancellationToken)
    {
        var devices = found.Devices.IsDefault ? [] : found.Devices;
        var ordered = devices.Select(held => (Held: held, Device: hub.TryGetDeviceFromUri(new Uri(held.DeviceUri), out var device) ? device : null))
            .OrderBy(static d => d.Device?.DeviceType switch { DeviceType.Mount => 0, DeviceType.Camera => 1, _ => 2 })
            .ToList();
        _pending = [.. devices];
        logger.LogInformation("Recovering: reconnecting {Count} device(s) the node before this one held", ordered.Count);

        foreach (var (held, device) in ordered)
        {
            // Named BEFORE the connect: a driver that crashes the node here is the next node's suspect.
            _touching = held.DeviceUri;
            await WriteIfChangedAsync(path, stopping: false, cancellationToken);

            var error = device is null ? "No device source on this node answers for it" : await ReconnectAsync(device, cancellationToken);
            _touching = null;
            _pending = _pending.Remove(held);
            Report(held.DeviceUri, error);
            if (error is not null || device is null)
            {
                logger.LogWarning("Recovering: could not reconnect {Device}: {Error}", held.DeviceUri, error);
                continue;
            }

            logger.LogInformation("Recovering: reconnected {Device}", device.DisplayName);
            if (held.Cooler is { } cooler)
            {
                Recool(device.DeviceUri, cooler, held.CoolerSetpointC);
            }
        }

        await WriteIfChangedAsync(path, stopping: false, cancellationToken);
    }

    private async Task<string?> ReconnectAsync(DeviceBase device, CancellationToken cancellationToken)
    {
        using var budget = new CancellationTokenSource(ReconnectBudget, options.WallClock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, budget.Token);
        try
        {
            await hub.ConnectAsync(device, linked.Token);
            return null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return $"It did not connect within {ReconnectBudget.TotalSeconds:0} s";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ex.Message;
        }
    }

    /// <summary>Re-establishes a reconnected camera's cooler from its intent, in the background.</summary>
    private void Recool(Uri camera, CoolerIntentKind cooler, double? setpointC)
    {
        switch (cooler)
        {
            case CoolerIntentKind.Cool when setpointC is { } setpoint:
                logger.LogInformation("Recovering: cooling {Camera} back to {Setpoint} C through the session's ramp", camera, setpoint);
                _ramps.Add(RampAsync(camera, ct => hub.CoolToSetpointAsync(camera, setpoint, new SessionConfiguration().CooldownRampInterval, timeProvider, logger, ct)));
                break;

            case CoolerIntentKind.Warm:
                logger.LogInformation("Recovering: going on warming {Camera} from where the sensor is now", camera);
                _ramps.Add(RampAsync(camera, async ct =>
                {
                    await hub.WarmAndCoolerOffAsync(camera, timeProvider, logger, ct);
                    return true;
                }));
                break;

            default:
                // Off stays off; the hub records it so the journal says so.
                hub.SetCoolerIntent(camera, CoolerIntent.Off);
                break;
        }
    }

    private async Task RampAsync(Uri camera, Func<CancellationToken, ValueTask<bool>> ramp)
    {
        Interlocked.Increment(ref _rampsRunning);
        try
        {
            await Task.Yield();
            await ramp(_recooling.Token);
        }
        catch (OperationCanceledException) when (_recooling.IsCancellationRequested)
        {
            logger.LogInformation("Recovering: the ramp on {Camera} ended, as a run started or the node began to stop", camera);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Recovering: the ramp on {Camera} failed", camera);
        }
        finally
        {
            Interlocked.Decrement(ref _rampsRunning);
        }
    }

    /// <summary>Records how reconnecting one device went in the report, unless it has been dismissed meanwhile.</summary>
    private void Report(string deviceUri, string? error)
    {
        var current = Volatile.Read(ref _recovery);
        if (current is null)
        {
            return;
        }

        var updated = new NodeRecoveryDto
        {
            JournalWrittenUtc = current.JournalWrittenUtc,
            FoundUtc = current.FoundUtc,
            AfterCrash = current.AfterCrash,
            Stale = current.Stale,
            CrashLoop = current.CrashLoop,
            SuspectDevice = current.SuspectDevice,
            InterruptedRun = current.InterruptedRun,
            Devices = [.. current.Devices.Select(d => d.DeviceUri != deviceUri ? d : new NodeHeldDeviceDto
            {
                DeviceUri = d.DeviceUri,
                DisplayName = d.DisplayName,
                Cooler = d.Cooler,
                CoolerSetpointC = d.CoolerSetpointC,
                Reconnected = error is null,
                ReconnectError = error,
            })],
        };
        Interlocked.CompareExchange(ref _recovery, updated, current);
    }

    /// <summary>
    /// What the node holds now: its hub and its run, the devices a recovery has yet to reconnect, the run the node before
    /// it died in until that is dismissed, the crash history and the device being reconnected.
    /// </summary>
    private NodeJournal Snapshot()
    {
        var now = NodeJournal.Of(hub, hosted.CurrentRunRecord, hosted.CurrentSession, options.WallClock.GetUtcNow(), Environment.ProcessId);
        if (Volatile.Read(ref _dismissed) == 1)
        {
            _carriedRun = null;
        }

        var devices = now.Devices;
        if (!_pending.IsDefaultOrEmpty)
        {
            var held = devices.Select(static d => new Uri(d.DeviceUri).DeviceKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
            devices = [.. devices.Concat(_pending.Where(p => !held.Contains(new Uri(p.DeviceUri).DeviceKey)))
                .OrderBy(static d => d.DeviceUri, StringComparer.Ordinal)];
        }
        return now with { Devices = devices, Run = now.Run ?? _carriedRun, Crashes = _crashes, Touching = _touching };
    }

    private async Task WriteIfChangedAsync(string path, bool stopping, CancellationToken cancellationToken)
    {
        var now = Snapshot();

        // A clean stop holds no device and no run of its own; a run carried from the node before it is no holding then.
        var holds = stopping ? !now.Devices.IsDefaultOrEmpty || hosted.CurrentRunRecord is not null : now.HoldsAnything;
        if (holds)
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
