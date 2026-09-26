using System;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TianWen.Hosting.Api;
using TianWen.Hosting.Dto;
using TianWen.Lib;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Astrometry.PlateSolve;
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
    ICelestialObjectDB catalog, IPlateSolverFactory solver, ITimeProvider timeProvider, ILogger<NodePreviews> logger)
{
    /// <summary>The <see cref="JobDto.Kind"/> of a preview exposure.</summary>
    internal const string ExposureJob = "preview";

    /// <summary>The <see cref="JobDto.Kind"/> of a plate solve of the frame an OTA shows.</summary>
    internal const string SolveJob = "solve";

    /// <summary>The <see cref="JobDto.Kind"/> of a solve and sync.</summary>
    internal const string SolveSyncJob = "solve-sync";

    /// <summary>What the lease on the mount and camera is called while a solve and sync runs, which a refusal names.</summary>
    internal const string SolveSyncLeaseOwner = "solve and sync";

    // The last solution of each OTA's frame, by OTA index: where a solve's result lives (a job carries none).
    private ImmutableDictionary<int, PlateSolutionDto> _solutions = ImmutableDictionary<int, PlateSolutionDto>.Empty;

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
    /// Plate-solves the frame OTA <paramref name="otaIndex"/> shows, as a job, and keeps the solution where
    /// <see cref="Solution"/> reads it. A read of a frame, so a run holding the camera does not refuse it; one solve of an
    /// OTA at a time.
    /// </summary>
    public ResponseEnvelope<JobDto> StartSolve(int otaIndex)
    {
        if (frames.Ota(otaIndex).Frame is null)
        {
            return ResponseEnvelope<JobDto>.NotFound($"OTA {otaIndex + 1} has no frame to solve");
        }

        var started = jobs.TryStart(SolveJob, $"solve/ota/{otaIndex}", async (step, ct) =>
        {
            // The frame is resolved again inside the job: it may have moved on since the request.
            var shown = frames.Ota(otaIndex);
            if (shown.Frame is not { } frame || !frame.TryLease(out var lease))
            {
                throw new InvalidOperationException($"OTA {otaIndex + 1}'s frame was replaced before it was solved; solve again");
            }

            using (lease)
            {
                step.Report($"Solving OTA {otaIndex + 1}'s frame");
                var (result, message, solved) = await PreviewCapture.SolveAsync(solver, lease.Image, ct);
                Keep(otaIndex, shown.Number, result, message, solved);
                return message;
            }
        }, out var job);

        return started
            ? ResponseEnvelope<JobDto>.Accepted(job)
            : ResponseEnvelope<JobDto>.Fail($"OTA {otaIndex + 1}'s frame is being solved already (job {job.Id})", 409);
    }

    /// <summary>The last solution of OTA <paramref name="otaIndex"/>'s frame, from a solve or a solve and sync.</summary>
    public ResponseEnvelope<PlateSolutionDto> Solution(int otaIndex)
        => _solutions.TryGetValue(otaIndex, out var solution)
            ? ResponseEnvelope<PlateSolutionDto>.Ok(solution)
            : ResponseEnvelope<PlateSolutionDto>.NotFound($"No frame of OTA {otaIndex + 1} has been solved");

    /// <summary>
    /// Takes a frame with OTA <paramref name="otaIndex"/>'s camera, solves it and syncs the mount to where it points, as a
    /// job (<see cref="MountSolveSync"/>, the sky map's own). The mount and the camera are LEASED for the whole of it; the
    /// frame becomes the OTA's, and its solution the OTA's solution. It ends Succeeded only when the mount synced, and
    /// otherwise Failed with the outcome's own words.
    /// </summary>
    public async Task<ResponseEnvelope<JobDto>> StartSolveSyncAsync(int otaIndex, PreviewExposureRequestDto request, CancellationToken cancellationToken)
    {
        if (!double.IsFinite(request.ExposureSeconds) || request.ExposureSeconds <= 0 || request.Binning < 1)
        {
            return ResponseEnvelope<JobDto>.Fail("A solve and sync takes a positive exposure, at a binning of 1 or more");
        }
        if (hosted.RunningKind is { } running)
        {
            return ResponseEnvelope<JobDto>.Fail(NodeRuns.AlreadyGoingOn(running), 409);
        }
        if (hosted.ActiveProfileId is not { } profileId || await Profile.TryReadDataAsync(external, profileId, cancellationToken) is not { } data)
        {
            return ResponseEnvelope<JobDto>.Fail("The node has no active profile to solve and sync with", 409);
        }
        if (otaIndex < 0 || otaIndex >= data.OTAs.Length)
        {
            return ResponseEnvelope<JobDto>.NotFound($"The active profile has no OTA {otaIndex + 1}");
        }
        if (data.Mount is not { Scheme: not "none" } mountUri)
        {
            return ResponseEnvelope<JobDto>.Fail("The active profile has no mount to sync", 409);
        }

        var ota = data.OTAs[otaIndex];
        foreach (var uri in new[] { mountUri, ota.Camera })
        {
            var ownership = DeviceOwnershipGate.Evaluate(hub, uri, DeviceAction.Actuate);
            if (!ownership.Allowed)
            {
                return ResponseEnvelope<JobDto>.Fail(ownership.Describe(), 409);
            }
            if (jobs.TryGetRunningOn(uri, out var busy))
            {
                return ResponseEnvelope<JobDto>.Fail($"{NameOf(uri)} is busy: a {busy.Kind} of it is running (job {busy.Id})", 409);
            }
        }
        if (!hub.TryGetConnectedDriver<IMountDriver>(mountUri, out var mount))
        {
            return ResponseEnvelope<JobDto>.NotFound($"{NameOf(mountUri)} is not connected");
        }
        if (!mount.CanSync)
        {
            return ResponseEnvelope<JobDto>.Fail($"{NameOf(mountUri)} does not support sync", 409);
        }
        if (!hub.TryGetConnectedDriver<ICameraDriver>(ota.Camera, out var camera))
        {
            return ResponseEnvelope<JobDto>.NotFound($"{NameOf(ota.Camera)} is not connected");
        }
        if (!DeviceLeaseSet.TryAcquire(hub, [mountUri, ota.Camera], SolveSyncLeaseOwner, out var claim, out var refusal))
        {
            return ResponseEnvelope<JobDto>.Fail(refusal.Describe(), 409);
        }

        var (focuser, filterWheel, _) = PreviewCapture.ResolveOtaCaptureDevices(hub, data, otaIndex);
        var profile = new Profile(profileId, "active", data);
        var started = jobs.TryStart(SolveSyncJob, mountUri, async (step, ct) =>
        {
            using (claim)
            {
                step.Report($"Exposing {NameOf(ota.Camera)} to solve and sync {NameOf(mountUri)}");
                var outcome = await MountSolveSync.SolveAndSyncAsync(mount, camera, ota.Name, ota.FocalLength, ota.Aperture, focuser, filterWheel,
                    catalog, solver, profile, timeProvider, TimeSpan.FromSeconds(request.ExposureSeconds), request.Gain, request.Binning, logger, ct);

                // The frame is the OTA's now, whatever came of it, and its solution is the OTA's solution.
                if (outcome.CapturedImage is { } image)
                {
                    frames.PublishPreview(otaIndex, image);
                }
                if (outcome.SolveResult is { } result)
                {
                    Keep(otaIndex, frames.Ota(otaIndex).Number, result, outcome.StatusMessage, result.Solution is not null);
                }

                return outcome.Result is MountSolveSync.SolveSyncResult.Synced
                    ? outcome.StatusMessage
                    : throw new InvalidOperationException(outcome.StatusMessage);
            }
        }, out var job);

        if (!started)
        {
            claim.Dispose();
            return ResponseEnvelope<JobDto>.Fail($"{NameOf(mountUri)} is busy: a {job.Kind} of it is running (job {job.Id})", 409);
        }
        return ResponseEnvelope<JobDto>.Accepted(job);
    }

    private void Keep(int otaIndex, int frameNumber, PlateSolveResult result, string message, bool solved)
    {
        var kept = new PlateSolutionDto
        {
            FrameNumber = frameNumber,
            Solved = solved,
            Message = message,
            ElapsedSeconds = result.Elapsed.TotalSeconds,
            Solution = result.Solution is { } wcs ? WcsDto.From(wcs) : null,
        };
        ImmutableInterlocked.AddOrUpdate(ref _solutions, otaIndex, kept, (_, _) => kept);
    }

    private string NameOf(Uri uri) => hub.TryGetDeviceFromUri(uri, out var device) ? device.DisplayName : uri.ToString();

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
