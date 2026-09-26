using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TianWen.Hosting.Api;
using TianWen.Hosting.Dto;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Devices;
using TianWen.Lib.Sequencing;

namespace TianWen.Hosting;

/// <summary>
/// A preview frame outside a session, taken and saved by the node (P5 part 2 of docs/plans/hardware-in-the-server.md,
/// #934): what the Live Session tab's preview does with the camera of an OTA of the active profile, as a JOB the node
/// finishes, whose frame lands in <see cref="NodeFrames"/> and is served like any other.
/// </summary>
/// <remarks>
/// <para>The refusals come first and are answers, never failed jobs, in the device plane's order: the run going on, the
/// profile and its OTA, ownership, the camera itself, then a job working on it. The capture is
/// <see cref="PreviewCapture"/>, the one the GUI uses, stamped as the GUI stamps it.</para>
/// <para><b>The camera is LEASED while it exposes</b>, which the GUI's preview never was: a job on the node is not a hub
/// lease, so without one a session starting mid-exposure would take the camera the preview is reading. The job owns the
/// lease and gives it back as it ends, which is why a preview never JOINS another (<see cref="NodeJobs.TryStart"/>).</para>
/// </remarks>
internal sealed class NodePreviews(IDeviceHub hub, NodeJobs jobs, IHostedSession hosted, NodeFrames frames, IExternal external,
    ICelestialObjectDB catalog, ITimeProvider timeProvider, ILogger<NodePreviews> logger)
{
    /// <summary>The <see cref="JobDto.Kind"/> of a preview exposure.</summary>
    internal const string ExposureJob = "preview";

    /// <summary>What the lease on the camera is called while it exposes, which a refusal names.</summary>
    internal const string LeaseOwner = "preview exposure";

    public async Task<ResponseEnvelope<JobDto>> StartExposureAsync(int otaIndex, PreviewExposureRequestDto request, CancellationToken cancellationToken)
    {
        if (!double.IsFinite(request.ExposureSeconds) || request.ExposureSeconds <= 0 || request.Binning < 1)
        {
            return ResponseEnvelope<JobDto>.Fail("A preview takes a positive exposure, at a binning of 1 or more");
        }
        if (hosted.RunningKind is { } running)
        {
            return ResponseEnvelope<JobDto>.Fail(NodeRuns.AlreadyGoingOn(running), 409);
        }
        if (hosted.ActiveProfileId is not { } profileId || await Profile.TryReadDataAsync(external, profileId, cancellationToken) is not { } data)
        {
            return ResponseEnvelope<JobDto>.Fail("The node has no active profile to take a preview with", 409);
        }
        if (otaIndex < 0 || otaIndex >= data.OTAs.Length)
        {
            return ResponseEnvelope<JobDto>.NotFound($"The active profile has no OTA {otaIndex + 1}");
        }

        var ota = data.OTAs[otaIndex];
        var uri = ota.Camera;
        var ownership = DeviceOwnershipGate.Evaluate(hub, uri, DeviceAction.Actuate);
        if (!ownership.Allowed)
        {
            return ResponseEnvelope<JobDto>.Fail(ownership.Describe(), 409);
        }
        var name = hub.TryGetDeviceFromUri(uri, out var device) ? device.DisplayName : uri.ToString();
        if (!hub.IsConnected(uri) || !hub.TryGetConnectedDriver<ICameraDriver>(uri, out var camera))
        {
            return ResponseEnvelope<JobDto>.NotFound($"{name} is not connected");
        }
        if (jobs.TryGetRunningOn(uri, out var busy))
        {
            return ResponseEnvelope<JobDto>.Fail($"{name} is busy: a {busy.Kind} of it is running (job {busy.Id})", 409);
        }
        if (!DeviceLeaseSet.TryAcquire(hub, [uri], LeaseOwner, out var claim, out var refusal))
        {
            return ResponseEnvelope<JobDto>.Fail(refusal.Describe(), 409);
        }

        var (focuser, filterWheel, mount) = PreviewCapture.ResolveOtaCaptureDevices(hub, data, otaIndex);
        var exposure = TimeSpan.FromSeconds(request.ExposureSeconds);
        var started = jobs.TryStart(ExposureJob, uri, async (step, ct) =>
        {
            using (claim)
            {
                step.Report($"Exposing {name} for {exposure.TotalSeconds:0.###} s");
                // Stamped as every other capture path stamps, so the frame's headers (and the fake camera's
                // synthetic field) match a session's and the GUI's.
                await CameraExposureActions.StampDenormAsync(camera, ota.Name, ota.FocalLength, ota.Aperture, focuser, filterWheel, mount,
                    targetName: mount is not null ? "Preview" : null, catalogDb: catalog,
                    logger: logger, ct: ct);
                var image = await PreviewCapture.CaptureAsync(camera, exposure, request.Gain, request.Binning, timeProvider, ct)
                    ?? throw new InvalidOperationException($"{name} finished its exposure but gave no frame");
                frames.PublishPreview(otaIndex, image);
                return $"Preview captured: OTA {otaIndex + 1}";
            }
        }, out var job);

        if (!started)
        {
            // A job got onto the camera between the look above and this start: the lease goes back unused.
            claim.Dispose();
            return ResponseEnvelope<JobDto>.Fail($"{name} is busy: a {job.Kind} of it is running (job {job.Id})", 409);
        }
        return ResponseEnvelope<JobDto>.Accepted(job);
    }

    /// <summary>
    /// Saves the frame OTA <paramref name="otaIndex"/> shows as a snapshot FITS in the node's output folder, and answers its
    /// path: a preview's, or a session's last sub, whichever <see cref="NodeFrames"/> shows.
    /// </summary>
    public async Task<ResponseEnvelope<string>> SaveSnapshotAsync(int otaIndex)
    {
        if (frames.Ota(otaIndex).Frame is not { } shown)
        {
            return ResponseEnvelope<string>.NotFound($"OTA {otaIndex + 1} has no frame to save");
        }
        if (!shown.TryLease(out var lease))
        {
            return ResponseEnvelope<string>.NotFound($"OTA {otaIndex + 1}'s frame was replaced while it was read; ask again");
        }

        using (lease)
        {
            var path = await PreviewCapture.SaveSnapshotAsync(lease.Image, otaIndex, external, timeProvider);
            logger.LogInformation("Snapshot of OTA {Ota} saved: {Path}", otaIndex + 1, path);
            return ResponseEnvelope<string>.Ok(path);
        }
    }
}
