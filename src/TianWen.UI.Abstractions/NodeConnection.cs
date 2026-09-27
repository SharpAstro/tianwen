using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Hosting.Dto;
using TianWen.Lib.Devices;
using TianWen.RemoteClient;

namespace TianWen.UI.Abstractions;

/// <summary>
/// A node, live, as a view reads it: its client, its event stream and the <see cref="RemoteSessionMirror"/> that mirrors its
/// session, handed to a <see cref="ViewContext"/> so the tabs render it exactly as they render any session, and the profile
/// and devices the view shows of it. ONE for the machine's own node, over its socket (P6 of
/// docs/plans/hardware-in-the-server.md, #936), and for a rig over the LAN (<see cref="RemoteRigConnection"/>), so the two
/// are read the same way and a gap in one is a gap in both.
/// </summary>
public abstract class NodeConnection : IAsyncDisposable
{
    /// <summary>
    /// How often the node is re-asked which profile it runs, and for that profile. Slow on purpose: it changes when somebody
    /// reconfigures the rig, not during a night, so this is two orders of magnitude rarer than the state poll and its traffic
    /// is noise next to it.
    /// </summary>
    public static readonly TimeSpan ProfileRefreshInterval = TimeSpan.FromMinutes(2);

    /// <summary>
    /// How often an idle view's devices are read, and how often while one moves (a focuser, or the mount slewing): the
    /// cadences this computer's idle view read its own devices at before the cut.
    /// </summary>
    public static readonly TimeSpan DevicesRefreshInterval = TimeSpan.FromSeconds(2);

    /// <inheritdoc cref="DevicesRefreshInterval"/>
    public static readonly TimeSpan DevicesRefreshIntervalMoving = TimeSpan.FromSeconds(1);

    private readonly HttpClient _http;
    private readonly IDisposable _prompts;
    private readonly ITimeProvider _timeProvider;

    private string? _profileName;
    private DateTimeOffset? _profileCheckedUtc;
    private int _profileRefreshInFlight;
    private string? _profileRevision;
    private long _devicesCheckedTicks;
    private int _devicesRefreshInFlight;
    private ImmutableDictionary<string, DeviceStateDto> _devices = ImmutableDictionary<string, DeviceStateDto>.Empty;

    /// <summary>
    /// Connects <paramref name="context"/> to the node at <paramref name="transport"/> and starts mirroring it. No request is
    /// made here: the mirror's first poll is the first word, and <see cref="RemoteSessionMirror.Contact"/> says how it went.
    /// </summary>
    /// <param name="promptsApp">The app a prompt is brought to the front of, or null to leave it on its own view.</param>
    private protected NodeConnection(ViewContext context, NodeTransport transport, GuiAppState? promptsApp,
        ITimeProvider timeProvider, ILogger logger, CancellationToken cancellationToken)
    {
        Context = context;
        Transport = transport;
        _timeProvider = timeProvider;
        Logger = logger;

        // The HTTP client's timeout is the backstop only (NodeTransport): the per-request budgets in NodeTimeouts are what
        // actually bite.
        _http = transport.CreateHttpClient();
        Client = new TianWenNodeClient(_http);
        // A view's frames are asked for while it is on screen (ViewContexts.PollAll), never here: every connected node would
        // otherwise pull full frames, which the mirror's opt-in exists to prevent.
        Mirror = new RemoteSessionMirror(Client, transport.CreateEventStream(timeProvider, logger), timeProvider, logger)
        {
            IsOnThisMachine = transport.SocketPath is not null,
        };

        // The payoff of the ISessionTelemetry split: from here the Live Session and Guider tabs render the node with no
        // knowledge of where it runs.
        context.LiveSession.ActiveSession = Mirror;
        context.Mirror = Mirror;
        // The node's prompts on its own view (and its Home card), answered back to it through the mirror.
        _prompts = LiveSessionPrompts.ShowOn(Mirror, context.LiveSession, promptsApp);
        Mirror.NodeEventNotTheSessions += OnNodeEvent;

        Mirror.Start(cancellationToken);
    }

    /// <summary>
    /// The devices the node holds, connected or leased by a run, as a view knows them between reads (P6): a full read
    /// (<see cref="MaybeRefreshDevicesAsync"/>) replaces them, each <c>DEVICE-STATE</c> push replaces one. Every per-frame
    /// question a view asks of a device (is it connected, does a run hold it, what did it last read) answers from here, never
    /// with a request. Keyed by the hub's identity rule (scheme, host and path, <see cref="DeviceBase.SameDevice"/>).
    /// </summary>
    public ImmutableDictionary<string, DeviceStateDto> Devices => Volatile.Read(ref _devices);

    /// <summary>What the node holds of the device <paramref name="deviceUri"/> names, or null when it holds nothing of it.</summary>
    public DeviceStateDto? Device(Uri deviceUri) => Devices.TryGetValue(KeyOf(deviceUri), out var device) ? device : null;

    /// <summary>Whether the node holds the device connected.</summary>
    public bool IsConnected(Uri deviceUri) => Device(deviceUri) is { Connected: true };

    /// <summary>The identity a device is keyed by: its URI's scheme, host and path, as <see cref="DeviceBase.SameDevice"/> compares.</summary>
    internal static string KeyOf(Uri deviceUri) => deviceUri.GetLeftPart(UriPartial.Path);

    private void OnNodeEvent(object? sender, WebSocketEventDto dto)
    {
        if (!DeviceStateDto.TryFromEvent(dto, out var device) || !Uri.TryCreate(device.DeviceUri, UriKind.Absolute, out var uri))
        {
            return;
        }
        ImmutableInterlocked.AddOrUpdate(ref _devices, KeyOf(uri), device, (_, _) => device);
        LayOutIdleDevices();
    }

    /// <summary>
    /// The view's OTA panels and mount from the devices held, while the node runs no session (a run's state carries its own
    /// devices), and once the view's profile is known, since its OTAs are what the devices are laid out by.
    /// </summary>
    private void LayOutIdleDevices()
    {
        var view = Context.LiveSession;
        if (!view.IsRunning && ProfileOnView?.Data is { } profile)
        {
            RigDevices.Apply(view, profile, [.. Devices.Values]);
        }
        view.NeedsRedraw = true;
    }

    /// <summary>The view context whose <see cref="ViewContext.LiveSession"/> this connection feeds.</summary>
    public ViewContext Context { get; }

    /// <summary>How the node is reached: its socket on this machine, or its address on the LAN.</summary>
    public NodeTransport Transport { get; }

    /// <summary>The node's API.</summary>
    public TianWenNodeClient Client { get; }

    /// <summary>The live mirror. Also the control surface of the node's runs (start, flats, abort, prompts).</summary>
    public RemoteSessionMirror Mirror { get; }

    private protected ILogger Logger { get; }

    /// <summary>What the node is called in this connection's log lines.</summary>
    private protected abstract string Name { get; }

    /// <summary>The profile the view plans with, given the one the node runs: that one, unless a subclass has a choice of its own.</summary>
    private protected virtual Guid? ProfileToPlanWith(Guid? running) => running;

    /// <summary>Where a profile read lands: the view's own profile for a rig; this computer's active profile for its node.</summary>
    private protected abstract void OnProfileRead(Profile profile);

    /// <summary>The profile the view shows, which its devices are laid out by.</summary>
    private protected abstract Profile? ProfileOnView { get; }

    /// <summary>
    /// The profile the node is set up to run, or <see langword="null"/> until it has been learned (and for a node with no
    /// active profile, or one too old to report it). Written by <see cref="MaybeRefreshProfileAsync"/> on a background task
    /// and read on the render thread, hence the volatile reference read.
    /// </summary>
    public string? ProfileName => Volatile.Read(ref _profileName);

    /// <summary>
    /// Whether <see cref="MaybeRefreshProfileAsync"/> would actually do anything. A synchronous predicate so a per-frame caller
    /// can skip the call entirely rather than allocating a completed task per connection per frame; the async path re-checks
    /// it, so this is one rule, not two.
    /// </summary>
    public bool ProfileRefreshDue =>
        _profileCheckedUtc is not { } last || _timeProvider.GetUtcNow() - last >= ProfileRefreshInterval;

    /// <summary>
    /// Re-asks the node which profile it runs, and reads the profile the view plans with whole (<see cref="OnProfileRead"/>,
    /// P5b part 8): its site is where the view's nights are planned and its twilight and clock are drawn, and its sensor the
    /// rectangle drawn at its pointing. At most once every <see cref="ProfileRefreshInterval"/> and never concurrently with
    /// itself. Returns <see langword="true"/> only when the name or the profile <i>changed</i> (by the revision the node reads
    /// it at), so a caller can redraw, and replan, on the transition rather than every tick.
    /// <para>
    /// A failure leaves the previous name in place and is <b>not</b> logged as a warning: the common cause is a node that has
    /// gone quiet, which the view already says plainly. Only a 404 clears the name, because that is the node stating it has
    /// no active profile.
    /// </para>
    /// </summary>
    public async Task<bool> MaybeRefreshProfileAsync(CancellationToken cancellationToken)
    {
        if (!ProfileRefreshDue)
        {
            return false;
        }

        // One in flight at a time. Claimed before the timestamp is written so a slow request cannot be joined by a second one
        // that sees a stale timestamp.
        if (Interlocked.CompareExchange(ref _profileRefreshInFlight, 1, 0) != 0)
        {
            return false;
        }

        try
        {
            _profileCheckedUtc = _timeProvider.GetUtcNow();
            var result = await Client.GetActiveProfileAsync(cancellationToken).ConfigureAwait(false);

            var resolved = result switch
            {
                { IsSuccess: true, Value: { } profile } => profile.Name,
                // The node answered that it has none -- that IS the answer, so drop any stale label.
                { IsNotFound: true } => null,
                // Unreachable or errored: keep what we had rather than blanking a good label.
                _ => ProfileName,
            };
            var changed = !string.Equals(resolved, ProfileName, StringComparison.Ordinal);
            Volatile.Write(ref _profileName, resolved);

            if (ProfileToPlanWith(result is { IsSuccess: true, Value: { } running } ? running.ProfileId : null) is { } profileId)
            {
                var detail = await Client.GetProfileAsync(profileId, cancellationToken).ConfigureAwait(false);
                if (detail is { IsSuccess: true, Value: { Data: { } data } profile }
                    && !string.Equals(profile.Revision, _profileRevision, StringComparison.Ordinal))
                {
                    _profileRevision = profile.Revision;
                    OnProfileRead(new Profile(profile.ProfileId, profile.Name, data));
                    changed = true;
                }
                else if (detail.Error is { } error && !detail.IsSuccess)
                {
                    // A failure keeps the profile held, as a failed name keeps the name: most often the node is quiet.
                    Logger.LogDebug("Could not read profile {ProfileId} of {Name}: {Error}", profileId, Name, error);
                }
            }

            return changed;
        }
        finally
        {
            Volatile.Write(ref _profileRefreshInFlight, 0);
        }
    }

    /// <summary>
    /// Whether <see cref="MaybeRefreshDevicesAsync"/> would read now: a synchronous check, as <see cref="ProfileRefreshDue"/>
    /// is, so the render loop asks it for nothing. Faster while a device moves, which is when the readout is watched.
    /// </summary>
    public bool DevicesRefreshDue
    {
        get
        {
            var last = Volatile.Read(ref _devicesCheckedTicks);
            return last == 0 || _timeProvider.GetElapsedTime(last) >= (AnyDeviceMoving(Context.LiveSession) ? DevicesRefreshIntervalMoving : DevicesRefreshInterval);
        }
    }

    private static bool AnyDeviceMoving(LiveSessionState view)
    {
        if (view.MountState.IsSlewing)
        {
            return true;
        }
        foreach (var ota in view.PreviewOTATelemetry)
        {
            if (ota.FocuserIsMoving)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Reads every device the node holds (<see cref="Devices"/>, the authoritative read the pushes between are hints to) and
    /// lays the view's idle panels out from them (P5b part 9, <see cref="RigDevices"/>): the OTA panels and the mount an idle
    /// Live Session lays out, and the mount its sky map draws. For the host to call while the view needs them, at most every
    /// <see cref="DevicesRefreshInterval"/> and never concurrently with itself. Returns <see langword="true"/> when it read, so
    /// the caller redraws.
    /// </summary>
    public async Task<bool> MaybeRefreshDevicesAsync(CancellationToken cancellationToken)
    {
        if (!DevicesRefreshDue)
        {
            return false;
        }
        if (Interlocked.CompareExchange(ref _devicesRefreshInFlight, 1, 0) != 0)
        {
            return false;
        }

        try
        {
            Volatile.Write(ref _devicesCheckedTicks, _timeProvider.GetTimestamp());
            var result = await Client.GetDeviceStatesAsync(cancellationToken).ConfigureAwait(false);
            if (result is not { IsSuccess: true, Value: { } devices })
            {
                // Most often the node is quiet, which the view already says: keep the last reading shown.
                Logger.LogDebug("Could not read the devices of {Name}: {Error}", Name, result.Error);
                return false;
            }
            Volatile.Write(ref _devices, Keyed(devices));
            LayOutIdleDevices();
            return true;
        }
        finally
        {
            Volatile.Write(ref _devicesRefreshInFlight, 0);
        }
    }

    private static ImmutableDictionary<string, DeviceStateDto> Keyed(IEnumerable<DeviceStateDto> devices)
    {
        var keyed = ImmutableDictionary.CreateBuilder<string, DeviceStateDto>();
        foreach (var device in devices)
        {
            if (Uri.TryCreate(device.DeviceUri, UriKind.Absolute, out var uri))
            {
                keyed[KeyOf(uri)] = device;
            }
        }
        return keyed.ToImmutable();
    }

    /// <summary>What the view forgets of the node as the connection goes, beyond its session: a rig's view, its profile.</summary>
    private protected virtual void OnDetached()
    {
    }

    public async ValueTask DisposeAsync()
    {
        // Detach BEFORE tearing the mirror down: a render pass between dispose and detach would read a mirror whose poll loop
        // has already stopped, and show a frozen session as though live.
        Mirror.NodeEventNotTheSessions -= OnNodeEvent;
        Context.LiveSession.ActiveSession = null;
        Context.Mirror = null;
        OnDetached();
        _prompts.Dispose();
        Context.LiveSession.PendingPrompt = null;

        await Mirror.DisposeAsync().ConfigureAwait(false);
        _http.Dispose();
        GC.SuppressFinalize(this);
    }
}
