using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TianWen.Lib.Devices;
using TianWen.Lib.Sequencing;

namespace TianWen.UI.Abstractions;

/// <summary>
/// The ONE place that decides which notes become a desktop alert and how they group (#2507). Notes arrive from this
/// computer's own recording (<see cref="GuiAppState.RecordNotification"/>) and from every connected node's mirror
/// (<c>RemoteSessionMirror.NoteReceived</c>, on its socket thread), so the decision state is lock-free and the sink is
/// never called on the caller's thread: <see cref="Offer"/> decides and hands the alert to one consumer
/// (<see cref="RunAsync"/>), which the host starts and cancels at quit.
/// </summary>
/// <remarks>
/// Rules: Info never alerts; Error always (at any level but Off); a Warning only when its kind is on
/// <see cref="IsSafetyKind"/>'s list at <see cref="DesktopAlertLevel.ErrorsAndSafety"/>, always at AllWarnings; nothing while
/// the window is in front, and with no host-supplied window probe nothing at all. A repeat of the same tag and group inside
/// <see cref="GroupingWindow"/>, timed on this client's clock (another node's clock may be skewed, so never the note's
/// stamp), is held back and counted; the next alert of that tag says how many were held back.
/// </remarks>
public sealed class DesktopAlerts
{
    /// <summary>The group of this computer's own notes and its own node's.</summary>
    public const string ThisComputerGroup = "this-computer";

    /// <summary>The title of an alert from this computer.</summary>
    public const string ThisComputerName = "This computer";

    /// <summary>How long a repeat of the same tag and group is held back after an alert.</summary>
    public static readonly TimeSpan GroupingWindow = TimeSpan.FromMinutes(5);

    private readonly IDesktopAlert _sink;
    private readonly ITimeProvider _time;
    private readonly Func<bool>? _windowInFront;
    private readonly ILogger? _logger;
    private readonly ConcurrentDictionary<(string Tag, string Group), GroupState> _groups = new();
    private readonly Channel<DesktopAlert> _queue = Channel.CreateBounded<DesktopAlert>(
        new BoundedChannelOptions(64) { SingleReader = true, FullMode = BoundedChannelFullMode.DropOldest });
    private int _level = (int)DesktopAlertSettings.Default;

    /// <param name="sink">Where alerts go.</param>
    /// <param name="time">This client's clock, which times the grouping.</param>
    /// <param name="windowInFront">Whether the app's window is in front; null (no host) means nothing alerts.</param>
    /// <param name="logger">Where a sink's refusal or failure is logged.</param>
    public DesktopAlerts(IDesktopAlert sink, ITimeProvider time, Func<bool>? windowInFront, ILogger? logger = null)
    {
        _sink = sink;
        _time = time;
        _windowInFront = windowInFront;
        _logger = logger;
    }

    /// <summary>The setting this obeys; set at start from <see cref="DesktopAlertSettings"/> and from the settings UI.</summary>
    public DesktopAlertLevel Level
    {
        get => (DesktopAlertLevel)Volatile.Read(ref _level);
        set => Volatile.Write(ref _level, (int)value);
    }

    /// <summary>
    /// The warnings that matter at the default level: a run that was aborted, and a mount limit. A weather or roof safety
    /// kind joins this list when one exists (there is none today).
    /// </summary>
    public static bool IsSafetyKind(NoteKind kind) => kind is NoteKind.SessionAborted or NoteKind.MountLimit;

    /// <summary>
    /// Decides on a note and, when it alerts, hands it to the consumer; returns at once. Safe from any thread.
    /// </summary>
    /// <param name="note">The note.</param>
    /// <param name="nodeId">The rig's NodeId; null for this computer's own notes and its own node's.</param>
    /// <param name="rigName">The rig's display name; ignored when <paramref name="nodeId"/> is null.</param>
    public void Offer(NotificationEntry note, string? nodeId, string? rigName)
    {
        if (Decide(note, nodeId, rigName) is { } alert)
        {
            _queue.Writer.TryWrite(alert);
        }
    }

    /// <summary>The decision, synchronous and apart from the hand-off: the alert a note earns, or null. Counts a held-back repeat.</summary>
    public DesktopAlert? Decide(NotificationEntry note, string? nodeId, string? rigName)
    {
        var level = Level;
        if (level is DesktopAlertLevel.Off || note.Severity is NotificationSeverity.Info
            || note.Severity is NotificationSeverity.Warning && level is DesktopAlertLevel.ErrorsAndSafety && !IsSafetyKind(note.Kind))
        {
            return null;
        }
        if (_windowInFront is not { } front || front() || !_sink.IsAvailable)
        {
            return null;
        }

        var local = string.IsNullOrEmpty(nodeId);
        var tag = note.Kind is NoteKind.None ? note.Severity.ToString() : note.Kind.ToString();
        var group = local ? ThisComputerGroup : nodeId ?? ThisComputerGroup;
        var held = Claim((tag, group));
        if (held < 0)
        {
            return null;
        }

        var title = local ? ThisComputerName : string.IsNullOrWhiteSpace(rigName) ? group : rigName;
        var body = held == 0 ? note.Message : $"{note.Message} ({held} similar held back)";
        return new DesktopAlert(title, body, tag, group,
            note.Severity is NotificationSeverity.Error ? DesktopAlertUrgency.Urgent : DesktopAlertUrgency.Normal,
            DesktopAlertLink.For(local ? null : nodeId));
    }

    /// <summary>Takes the grouping slot of a key: how many repeats were held back since the last alert (0 for a first), or -1 when
    /// this one is itself held back. A compare-and-swap loop, so concurrent callers each get a consistent answer.</summary>
    private int Claim((string Tag, string Group) key)
    {
        var now = _time.GetUtcNow().UtcTicks;
        while (true)
        {
            if (!_groups.TryGetValue(key, out var current))
            {
                if (_groups.TryAdd(key, new GroupState(now, 0)))
                {
                    return 0;
                }
                continue;
            }
            if (now - current.LastShownTicks < GroupingWindow.Ticks)
            {
                if (_groups.TryUpdate(key, current with { Held = current.Held + 1 }, current))
                {
                    return -1;
                }
                continue;
            }
            if (_groups.TryUpdate(key, new GroupState(now, 0), current))
            {
                return current.Held;
            }
        }
    }

    private sealed record GroupState(long LastShownTicks, int Held);

    /// <summary>The consumer: shows each queued alert on this task until <paramref name="cancellationToken"/> or
    /// <see cref="Complete"/>. A sink that throws is logged and the next alert goes on.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var alert in _queue.Reader.ReadAllAsync(cancellationToken))
            {
                try
                {
                    var result = _sink.Show(alert);
                    if (!result.Shown)
                    {
                        _logger?.LogDebug("Desktop alert {Tag} not shown: {Reason}", alert.Tag, result.Reason);
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Desktop alert {Tag} failed", alert.Tag);
                }
            }
        }
        catch (OperationCanceledException ex)
        {
            _logger?.LogDebug(ex, "Desktop alert consumer stopped");
        }
    }

    /// <summary>Ends the consumer once what is queued has been shown.</summary>
    public void Complete() => _queue.Writer.TryComplete();
}
