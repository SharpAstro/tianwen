using System;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Hosting.Api;
using TianWen.Hosting.Dto;
using TianWen.Lib.Devices;

namespace TianWen.Hosting;

/// <summary>
/// Moving a focuser, a filter wheel or a mount with no session running, the device plane's fourth part (P2 of
/// docs/plans/hardware-in-the-server.md, #929). A move is a job that ends when the device has settled, and cancelling it
/// halts the device; a stop ends the job it stops, so it is the one command a running job does not refuse. What only the
/// device can answer (a goto below the horizon, a pier side it cannot reach) fails the job with the device's reason.
/// </summary>
internal sealed partial class DeviceOperations
{
    internal const string MoveJob = "move";
    internal const string FilterJob = "filter";
    internal const string SlewJob = "slew";
    internal const string ParkJob = "park";
    internal const string UnparkJob = "unpark";

    /// <summary>How often a job asks a moving device whether it has settled.</summary>
    private static readonly TimeSpan SettlePoll = TimeSpan.FromMilliseconds(250);

    /// <summary>The longest a filter wheel or a park may take before the job gives it up.</summary>
    private static readonly TimeSpan FilterMoveTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan ParkTimeout = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Moves the focuser to a position, or by a number of steps from where it is, as a job that ends when it has stopped.
    /// A second move while one runs is refused rather than joined: a move to another position is not the same move.
    /// </summary>
    public async Task<ResponseEnvelope<JobDto>> MoveFocuserAsync(FocuserMoveRequestDto request, CancellationToken cancellationToken)
    {
        if (!TryIdle<IFocuserDriver>(request.DeviceUri, "focuser", out var uri, out var focuser, out var refused))
        {
            return refused.Value.As<JobDto>();
        }
        var name = NameOf(uri);
        int target;
        if (request is { Position: { } position, Steps: null })
        {
            target = position;
        }
        else if (request is { Position: null, Steps: { } steps })
        {
            target = await focuser.GetPositionAsync(cancellationToken) + steps;
        }
        else
        {
            return ResponseEnvelope<JobDto>.Fail("A move names a position or a number of steps, one of the two");
        }
        if (target < 0 || focuser.MaxStep > 0 && target > focuser.MaxStep)
        {
            return ResponseEnvelope<JobDto>.Fail($"{name} moves between 0 and {focuser.MaxStep}");
        }

        return Start(MoveJob, uri, name, async (step, ct) =>
        {
            step.Report($"Moving {name} to {target}");
            try
            {
                await focuser.BeginMoveAsync(target, ct);
                while (await focuser.GetIsMovingAsync(ct))
                {
                    await timeProvider.SleepAsync(SettlePoll, ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                await focuser.BeginHaltAsync(CancellationToken.None);
                throw;
            }
            return $"{name} at {await focuser.GetPositionAsync(ct)}";
        });
    }

    /// <summary>Halts the focuser, ending the move job that drives it, if one does.</summary>
    public async Task<ResponseEnvelope<string>> StopFocuserAsync(string deviceUri, CancellationToken cancellationToken)
    {
        if (!TryDriver<IFocuserDriver>(deviceUri, "focuser", out var uri, out var focuser, out var refused))
        {
            return refused.Value.As<string>();
        }

        CancelTheJobOn(uri);
        await focuser.BeginHaltAsync(cancellationToken);
        return ResponseEnvelope<string>.Ok($"{NameOf(uri)}: stopped");
    }

    /// <summary>Turns the filter wheel to a position, counted from 0, as a job that ends when the wheel is there.</summary>
    public ResponseEnvelope<JobDto> ChangeFilter(FilterChangeRequestDto request)
    {
        if (!TryIdle<IFilterWheelDriver>(request.DeviceUri, "filter wheel", out var uri, out var wheel, out var refused))
        {
            return refused.Value.As<JobDto>();
        }
        var name = NameOf(uri);
        var filters = wheel.Filters;
        var position = request.Position;
        if (position < 0 || position >= filters.Count)
        {
            return ResponseEnvelope<JobDto>.Fail(filters.Count == 0 ? $"{name} names no filters" : $"{name} has positions 0 to {filters.Count - 1}");
        }

        var filter = filters[position].DisplayName ?? $"position {position}";
        return Start(FilterJob, uri, name, async (step, ct) =>
        {
            step.Report($"Turning {name} to {filter}");
            await wheel.BeginMoveAsync(position, ct);
            var deadline = timeProvider.GetUtcNow() + FilterMoveTimeout;
            while (await wheel.GetPositionAsync(ct) != position)
            {
                if (timeProvider.GetUtcNow() > deadline)
                {
                    throw new TimeoutException($"{name} did not reach {filter} within {FilterMoveTimeout.TotalMinutes:F0} minutes");
                }
                await timeProvider.SleepAsync(SettlePoll, ct);
            }
            return $"{name} at {filter}";
        });
    }

    /// <summary>
    /// Slews the mount to a J2000 position through the one goto every host uses (<see cref="MountGoto"/>: unpark, the
    /// horizon limit, the destination pier side, the tracking rate), as a job that ends when the mount has landed. The
    /// site comes from the active profile, as the GUI's goto takes it from its own; with none there is no goto.
    /// </summary>
    public async Task<ResponseEnvelope<JobDto>> GotoAsync(MountGotoRequestDto request, CancellationToken cancellationToken)
    {
        if (!TryIdle<IMountDriver>(request.DeviceUri, "mount", out var uri, out var mount, out var refused))
        {
            return refused.Value.As<JobDto>();
        }
        var (ra, dec) = (request.RaJ2000, request.DecJ2000);
        if (!(ra >= 0 && ra < 24) || !(dec >= -90 && dec <= 90))
        {
            return ResponseEnvelope<JobDto>.Fail("A goto names a J2000 right ascension from 0 to 24 hours and a declination from -90 to 90 degrees");
        }
        if (await ActiveProfileAsync(cancellationToken) is not { } profile)
        {
            return ResponseEnvelope<JobDto>.Fail("A goto needs the site, which comes from the active profile, and the node has none", 409);
        }

        var name = NameOf(uri);
        var target = request.Name ?? $"RA {ra:F3} h, Dec {dec:F2} deg";
        var minAltitude = request.MinAltitudeDegrees ?? 10;
        return Start(SlewJob, uri, name, async (step, ct) =>
        {
            step.Report($"Slewing to {target}");
            try
            {
                var (post, message) = await MountGoto.SlewToJ2000Async(mount, target, ra, dec, request.Index, profile, timeProvider, minAltitude, logger, ct);
                if (post is not SlewPostCondition.Slewing)
                {
                    throw new InvalidOperationException(message);
                }
                step.Report(message);

                var (landed, how) = await MountGoto.AwaitSlewCompletionAsync(mount, target, timeProvider, logger: logger, cancellationToken: ct);
                return landed is MountGoto.SlewCompletion.Reached ? how : throw new InvalidOperationException(how);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                await mount.AbortSlewAsync(CancellationToken.None);
                throw;
            }
        });
    }

    /// <summary>Parks the mount, as a job that ends when it reports itself parked.</summary>
    public ResponseEnvelope<JobDto> Park(string deviceUri)
    {
        if (!TryIdle<IMountDriver>(deviceUri, "mount", out var uri, out var mount, out var refused))
        {
            return refused.Value.As<JobDto>();
        }
        var name = NameOf(uri);
        if (!mount.CanPark)
        {
            return ResponseEnvelope<JobDto>.Fail($"{name} cannot park");
        }

        return Start(ParkJob, uri, name, async (step, ct) =>
        {
            step.Report($"Parking {name}");
            try
            {
                await mount.ParkAsync(ct);
                var deadline = timeProvider.GetUtcNow() + ParkTimeout;
                while (!await mount.AtParkAsync(ct))
                {
                    if (timeProvider.GetUtcNow() > deadline)
                    {
                        throw new TimeoutException($"{name} did not report itself parked within {ParkTimeout.TotalMinutes:F0} minutes");
                    }
                    await timeProvider.SleepAsync(SettlePoll, ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                await mount.AbortSlewAsync(CancellationToken.None);
                throw;
            }
            return $"{name} parked";
        });
    }

    /// <summary>Unparks the mount, as a job.</summary>
    public ResponseEnvelope<JobDto> Unpark(string deviceUri)
    {
        if (!TryIdle<IMountDriver>(deviceUri, "mount", out var uri, out var mount, out var refused))
        {
            return refused.Value.As<JobDto>();
        }
        var name = NameOf(uri);
        if (!mount.CanUnpark)
        {
            return ResponseEnvelope<JobDto>.Fail($"{name} cannot unpark");
        }

        return Start(UnparkJob, uri, name, async (step, ct) =>
        {
            step.Report($"Unparking {name}");
            await mount.UnparkAsync(ct);
            return $"{name} unparked";
        });
    }

    /// <summary>Switches the mount's tracking on or off, at once; refused while a job is moving the mount.</summary>
    public async Task<ResponseEnvelope<string>> SetTrackingAsync(MountTrackingRequestDto request, CancellationToken cancellationToken)
    {
        if (!TryIdle<IMountDriver>(request.DeviceUri, "mount", out var uri, out var mount, out var refused))
        {
            return refused.Value.As<string>();
        }
        var name = NameOf(uri);
        if (!mount.CanSetTracking)
        {
            return ResponseEnvelope<string>.Fail($"{name} cannot switch its tracking");
        }

        await mount.SetTrackingAsync(request.On, cancellationToken);
        return ResponseEnvelope<string>.Ok($"{name}: tracking {(request.On ? "on" : "off")}");
    }

    /// <summary>Stops the mount where it is, ending the goto or park job that drives it, if one does.</summary>
    public async Task<ResponseEnvelope<string>> StopMountAsync(string deviceUri, CancellationToken cancellationToken)
    {
        if (!TryDriver<IMountDriver>(deviceUri, "mount", out var uri, out var mount, out var refused))
        {
            return refused.Value.As<string>();
        }

        CancelTheJobOn(uri);
        await mount.AbortSlewAsync(cancellationToken);
        return ResponseEnvelope<string>.Ok($"{NameOf(uri)}: stopped");
    }

    /// <summary>A stop ends the job it stops: whatever job is moving the device is cancelled, and halts it on its way out.</summary>
    private void CancelTheJobOn(Uri uri)
    {
        if (jobs.TryGetRunningOn(uri, out var job))
        {
            jobs.TryCancel(job.Id, out _);
        }
    }

    /// <summary>
    /// The mount a session-scoped route means (<c>/api/v1/mount/*</c>, from before the device plane): the running
    /// session's, else the active profile's. Null when neither names one.
    /// </summary>
    public async Task<string?> RigMountAsync(CancellationToken cancellationToken)
    {
        if (hosted.CurrentSession is { } session)
        {
            return session.Setup.Mount.Device.DeviceUri.ToString();
        }
        return await ActiveProfileAsync(cancellationToken) is { Data: { Mount: { Scheme: not "none" } mount } } ? mount.ToString() : null;
    }

    /// <summary>
    /// The focuser and filter wheel of the OTA at <paramref name="index"/> that a session-scoped route means
    /// (<c>/api/v1/ota/{index}/*</c>): the running session's, else the active profile's. Null when there is no such OTA.
    /// </summary>
    public async Task<(string? Focuser, string? FilterWheel)?> RigOtaAsync(int index, CancellationToken cancellationToken)
    {
        if (hosted.CurrentSession is { } session)
        {
            return index >= 0 && index < session.Setup.Telescopes.Length && session.Setup.Telescopes[index] is var ota
                ? (ota.Focuser?.Device.DeviceUri.ToString(), ota.FilterWheel?.Device.DeviceUri.ToString())
                : null;
        }
        return await ActiveProfileAsync(cancellationToken) is { Data: { OTAs: var otas } } && index >= 0 && index < otas.Length
            ? (otas[index].Focuser?.ToString(), otas[index].FilterWheel?.ToString())
            : null;
    }

    /// <summary>The node's active profile, read as it is now; null when there is none.</summary>
    private async Task<Profile?> ActiveProfileAsync(CancellationToken cancellationToken)
        => hosted.ActiveProfileId is { } id && await Profile.TryReadDataAsync(external, id, cancellationToken) is { } data
            ? new Profile(id, "active", data)
            : null;
}
