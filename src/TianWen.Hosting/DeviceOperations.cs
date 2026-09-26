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
/// Connecting and disconnecting a device with no session running, the device plane's second part (P2 of
/// docs/plans/hardware-in-the-server.md, #929). Each is a JOB on the node's token (<see cref="NodeJobs"/>), so a warm-up
/// that must finish if the window dies finishes in the node, and a device takes one job at a time.
/// </summary>
/// <remarks>
/// The rules are the Equipment tab's, asked in its order, so a client that switches over at the cut (P6) meets the same
/// answers: ownership FIRST (a run holding the device is the refusal that tells the client what to do, stop the run), then
/// the hardware (a cooled or busy camera is not disconnected cold unless the client says so). A refusal answers at once;
/// a job answers 202 and is followed through <c>GET /api/v1/jobs/{id}</c> or <c>JOB-PROGRESS</c>.
/// </remarks>
internal sealed class DeviceOperations(IDeviceHub hub, NodeJobs jobs, ITimeProvider timeProvider, ILogger<DeviceOperations> logger)
{
    /// <summary>The <see cref="JobDto.Kind"/> of each job.</summary>
    internal const string ConnectJob = "connect";
    internal const string DisconnectJob = "disconnect";
    internal const string WarmAndDisconnectJob = "warm-and-disconnect";

    /// <summary>Connects the device the URI names, built from the URI by the source its host names.</summary>
    public ResponseEnvelope<JobDto> Connect(string deviceUri)
    {
        if (!Uri.TryCreate(deviceUri, UriKind.Absolute, out var uri))
        {
            return NotAUri(deviceUri);
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
        if (!Uri.TryCreate(deviceUri, UriKind.Absolute, out var uri))
        {
            return ResponseEnvelope<DisconnectCheckDto>.Fail($"Not a device URI: {deviceUri}");
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
        if (!TryConnected(deviceUri, out var uri, out var refused))
        {
            return refused;
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
        if (!TryConnected(deviceUri, out var uri, out var refused))
        {
            return refused;
        }

        var name = NameOf(uri);
        return Start(WarmAndDisconnectJob, uri, name, async (step, ct) =>
        {
            step.Report($"Warming {name}");
            await hub.WarmAndDisconnectAsync(uri, timeProvider, logger, force: false, ct);
            return $"Warmed and disconnected {name}";
        });
    }

    /// <summary>
    /// A connected device a run does not hold. Ownership is asked BEFORE the hardware, and before the job starts, so the
    /// refusal is an answer rather than a failed job; the hub refuses again should a run take the device in between.
    /// </summary>
    private bool TryConnected(string deviceUri, [NotNullWhen(true)] out Uri? uri, [NotNullWhen(false)] out ResponseEnvelope<JobDto>? refused)
    {
        uri = null;
        if (!Uri.TryCreate(deviceUri, UriKind.Absolute, out var parsed))
        {
            refused = NotAUri(deviceUri);
            return false;
        }
        if (!hub.IsConnected(parsed))
        {
            refused = ResponseEnvelope<JobDto>.NotFound($"{NameOf(parsed)} is not connected");
            return false;
        }

        var ownership = DeviceOwnershipGate.Evaluate(hub, parsed, DeviceAction.Disconnect);
        if (!ownership.Allowed)
        {
            refused = ResponseEnvelope<JobDto>.Fail(ownership.Describe(), 409);
            return false;
        }

        uri = parsed;
        refused = null;
        return true;
    }

    /// <summary>Starts the job on the device, joins the same one already running, or refuses one of another kind.</summary>
    private ResponseEnvelope<JobDto> Start(string kind, Uri uri, string name, Func<NodeJobs.JobStep, CancellationToken, Task<string?>> work)
        => jobs.TryStartOrJoin(kind, uri, work, out var job)
            ? ResponseEnvelope<JobDto>.Accepted(job)
            : ResponseEnvelope<JobDto>.Fail($"{name} is busy: a {job.Kind} of it is running (job {job.Id})", 409);

    private string NameOf(Uri uri) => hub.TryGetDeviceFromUri(uri, out var device) ? device.DisplayName : uri.ToString();

    private static ResponseEnvelope<JobDto> NotAUri(string deviceUri) => ResponseEnvelope<JobDto>.Fail($"Not a device URI: {deviceUri}");

    private static string Unsafe(DisconnectSafety safety) => safety switch
    {
        DisconnectSafety.CoolerOn => "has its cooler on",
        DisconnectSafety.Busy => "is exposing or downloading",
        DisconnectSafety.BusyAndCool => "is exposing with its cooler on",
        _ => "could not say whether its cooler is on",
    };
}
