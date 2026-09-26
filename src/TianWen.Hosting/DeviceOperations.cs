using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TianWen.Hosting.Api;
using TianWen.Hosting.Dto;
using TianWen.Lib.Devices;

namespace TianWen.Hosting;

/// <summary>
/// What a client does to a device with no session running, the device plane (P2 of
/// docs/plans/hardware-in-the-server.md, #929): connecting and disconnecting (part 2), a camera's cooling and settings
/// (part 3, <c>DeviceOperations.Camera.cs</c>), and moving a focuser, a filter wheel or a mount (part 4,
/// <c>DeviceOperations.Motion.cs</c>). Anything slow is a JOB on the node's token (<see cref="NodeJobs"/>), so a
/// ramp that must finish if the window dies finishes in the node, and a device takes one job at a time.
/// </summary>
/// <remarks>
/// The rules are the Equipment tab's, asked in its order, so a client that switches over at the cut (P6) meets the same
/// answers: ownership FIRST (a run holding the device is the refusal that tells the client what to do, stop the run), then
/// the hardware. A refusal answers at once; a job answers 202 and is followed through <c>GET /api/v1/jobs/{id}</c> or
/// <c>JOB-PROGRESS</c>.
/// </remarks>
internal sealed partial class DeviceOperations(IDeviceHub hub, NodeJobs jobs, IHostedSession hosted, IExternal external, ITimeProvider timeProvider,
    ILogger<DeviceOperations> logger)
{
    /// <summary>The <see cref="JobDto.Kind"/> of each job.</summary>
    internal const string ConnectJob = "connect";
    internal const string DisconnectJob = "disconnect";
    internal const string WarmAndDisconnectJob = "warm-and-disconnect";

    /// <summary>Why a request is refused, before anything is done: the one shape every route answers a refusal in.</summary>
    private readonly record struct Refusal(string Message, int Status)
    {
        public ResponseEnvelope<T> As<T>() => ResponseEnvelope<T>.Fail(Message, Status);
    }

    /// <summary>Connects the device the URI names, built from the URI by the source its host names.</summary>
    public ResponseEnvelope<JobDto> Connect(string deviceUri)
    {
        if (!TryParse(deviceUri, out var uri, out var refused))
        {
            return refused.Value.As<JobDto>();
        }
        if (!hub.TryGetDeviceFromUri(uri, out var device))
        {
            return ResponseEnvelope<JobDto>.NotFound($"No device source on this node knows {uri}");
        }

        var name = device.DisplayName;
        return Start(ConnectJob, device.DeviceUri, name, async (step, ct) =>
        {
            step.Report($"Connecting {name}");
            await hub.ConnectAsync(device, ct);
            return $"Connected {name}";
        });
    }

    /// <summary>
    /// Whether the connected device at <paramref name="deviceUri"/> can be disconnected now, and which run holds it: the
    /// read before a disconnect is offered.
    /// </summary>
    public async Task<ResponseEnvelope<DisconnectCheckDto>> CheckDisconnectAsync(string deviceUri, CancellationToken cancellationToken)
    {
        if (!TryParse(deviceUri, out var uri, out var refused))
        {
            return refused.Value.As<DisconnectCheckDto>();
        }
        if (!hub.IsConnected(uri))
        {
            return ResponseEnvelope<DisconnectCheckDto>.NotFound($"{NameOf(uri)} is not connected");
        }

        return ResponseEnvelope<DisconnectCheckDto>.Ok(new DisconnectCheckDto
        {
            Safety = await hub.GetDisconnectSafetyAsync(uri, cancellationToken),
            LeaseOwner = hub.TryGetLease(uri, out var lease) ? lease.OwnerLabel : null,
        });
    }

    /// <summary>
    /// Disconnects the device, refusing one a run holds and, unless <paramref name="skipWarmUp"/>, a camera that is cold
    /// or at work.
    /// </summary>
    public async Task<ResponseEnvelope<JobDto>> DisconnectAsync(string deviceUri, bool skipWarmUp, CancellationToken cancellationToken)
    {
        if (!TryConnectedAndFree(deviceUri, DeviceAction.Disconnect, out var uri, out var refused))
        {
            return refused.Value.As<JobDto>();
        }

        var name = NameOf(uri);
        if (!skipWarmUp && await hub.GetDisconnectSafetyAsync(uri, cancellationToken) is var safety and not DisconnectSafety.Safe)
        {
            return ResponseEnvelope<JobDto>.Fail($"{name} {Unsafe(safety)}: warm it first (warm-and-disconnect), or disconnect it skipping the warm-up", 409);
        }

        return Start(DisconnectJob, uri, name, async (step, ct) =>
        {
            step.Report($"Disconnecting {name}");
            await hub.DisconnectAsync(uri, force: false, ct);
            return $"Disconnected {name}";
        });
    }

    /// <summary>
    /// Warms a cooled camera through the hub's ramp, turns its cooler off and disconnects it; any other device is simply
    /// disconnected. The ramp runs in the node, so it finishes whatever becomes of the client that asked.
    /// </summary>
    public ResponseEnvelope<JobDto> WarmAndDisconnect(string deviceUri)
    {
        if (!TryConnectedAndFree(deviceUri, DeviceAction.Disconnect, out var uri, out var refused))
        {
            return refused.Value.As<JobDto>();
        }

        var name = NameOf(uri);
        return Start(WarmAndDisconnectJob, uri, name, async (step, ct) =>
        {
            step.Report($"Warming {name}");
            await hub.WarmAndDisconnectAsync(uri, timeProvider, logger, force: false, ct);
            return $"Warmed and disconnected {name}";
        });
    }

    private static bool TryParse(string deviceUri, [NotNullWhen(true)] out Uri? uri, [NotNullWhen(false)] out Refusal? refused)
    {
        if (Uri.TryCreate(deviceUri, UriKind.Absolute, out uri))
        {
            refused = null;
            return true;
        }

        refused = new Refusal($"Not a device URI: {deviceUri}", 400);
        return false;
    }

    /// <summary>
    /// A connected device a run does not hold. Ownership is asked FIRST, before even whether the device is connected:
    /// "a run is using this" is the answer that tells the client what to do (<see cref="ActuationGate"/>'s rule), and it
    /// comes before any job starts, so a refusal is an answer rather than a failed job. The hub refuses again should a run
    /// take the device in between.
    /// </summary>
    private bool TryConnectedAndFree(string deviceUri, DeviceAction action, [NotNullWhen(true)] out Uri? uri, [NotNullWhen(false)] out Refusal? refused)
    {
        if (!TryParse(deviceUri, out uri, out refused))
        {
            return false;
        }

        var ownership = DeviceOwnershipGate.Evaluate(hub, uri, action);
        if (!ownership.Allowed)
        {
            refused = new Refusal(ownership.Describe(), 409);
            uri = null;
            return false;
        }
        if (!hub.IsConnected(uri))
        {
            refused = new Refusal($"{NameOf(uri)} is not connected", 404);
            uri = null;
            return false;
        }

        return true;
    }

    /// <summary>A connected device of the kind a command needs (<typeparamref name="TDriver"/>), which no run holds.</summary>
    private bool TryDriver<TDriver>(string deviceUri, string kind, [NotNullWhen(true)] out Uri? uri, [NotNullWhen(true)] out TDriver? driver,
        [NotNullWhen(false)] out Refusal? refused) where TDriver : class, IDeviceDriver
    {
        driver = null;
        if (!TryConnectedAndFree(deviceUri, DeviceAction.Actuate, out uri, out refused))
        {
            return false;
        }
        if (!hub.TryGetConnectedDriver(uri, out driver))
        {
            refused = new Refusal($"{NameOf(uri)} is not a {kind}", 400);
            uri = null;
            return false;
        }

        return true;
    }

    /// <summary>
    /// As <see cref="TryDriver"/>, and with no job working on the device: what a command asks that would fight a job, an
    /// immediate one (a cooler off under a ramp) or a second move with another target, which must not quietly join the
    /// first.
    /// </summary>
    private bool TryIdle<TDriver>(string deviceUri, string kind, [NotNullWhen(true)] out Uri? uri, [NotNullWhen(true)] out TDriver? driver,
        [NotNullWhen(false)] out Refusal? refused) where TDriver : class, IDeviceDriver
    {
        if (!TryDriver(deviceUri, kind, out uri, out driver, out refused))
        {
            return false;
        }
        if (jobs.TryGetRunningOn(uri, out var job))
        {
            refused = Busy(NameOf(uri), job);
            uri = null;
            driver = null;
            return false;
        }

        return true;
    }

    /// <summary>Starts the job on the device, joins the same one already running, or refuses one of another kind.</summary>
    private ResponseEnvelope<JobDto> Start(string kind, Uri uri, string name, Func<NodeJobs.JobStep, CancellationToken, Task<string?>> work)
        => jobs.TryStartOrJoin(kind, uri, work, out var job)
            ? ResponseEnvelope<JobDto>.Accepted(job)
            : Busy(name, job).As<JobDto>();

    private static Refusal Busy(string name, JobDto job) => new Refusal($"{name} is busy: a {job.Kind} of it is running (job {job.Id})", 409);

    private string NameOf(Uri uri) => hub.TryGetDeviceFromUri(uri, out var device) ? device.DisplayName : uri.ToString();

    private static string Unsafe(DisconnectSafety safety) => safety switch
    {
        DisconnectSafety.CoolerOn => "has its cooler on",
        DisconnectSafety.Busy => "is exposing or downloading",
        DisconnectSafety.BusyAndCool => "is exposing with its cooler on",
        _ => "could not say whether its cooler is on",
    };
}
