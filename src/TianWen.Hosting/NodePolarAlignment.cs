using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TianWen.Hosting.Api;
using TianWen.Hosting.Dto;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Astrometry.PlateSolve;
using TianWen.Lib.Devices;
using TianWen.Lib.Imaging;
using TianWen.Lib.Sequencing.PolarAlignment;

namespace TianWen.Hosting;

/// <summary>
/// Polar alignment as the node's run (P5 part 4 of docs/plans/hardware-in-the-server.md, #934): the GUI's own routine,
/// <see cref="PolarAlignmentRun"/>, through the node's devices, so a client anywhere can align the rig and the claim on the
/// mount and the camera is the node's. Its probe and refinement frames become the OTA's (<see cref="NodeFrames"/>), so a
/// client watches them through <c>/frames</c> like any other.
/// </summary>
/// <remarks>
/// <para>The refusals come in the device plane's order: a run going on first, then the profile, then what the run itself
/// refuses (the mount, the site, the capture source, and the claim, which names whoever holds a device).</para>
/// <para>A stop is Done and Cancel alike, and it is ANSWERED AT ONCE: the restore reverses the Phase A rotation, tens of
/// seconds, which no request budget allows; <c>GET /api/v1/polar</c> says when it has ended.</para>
/// <para>It ends by itself once no client has watched it for the detach grace (<see cref="NodeRunWatch"/>): a window that
/// went away must not leave the mount rotating for nobody.</para>
/// </remarks>
internal sealed class NodePolarAlignment(IDeviceHub hub, IHostedSession hosted, NodeFrames frames, IExternal external,
    ICelestialObjectDB catalog, IPlateSolverFactory solver, ITimeProvider timeProvider, ILogger<NodePolarAlignment> logger)
{
    public async Task<ResponseEnvelope<PolarStateDto>> StartAsync(PolarAlignmentRequestDto request, CancellationToken cancellationToken)
    {
        if (hosted.RunningKind is { } running)
        {
            return ResponseEnvelope<PolarStateDto>.Fail(NodeRuns.AlreadyGoingOn(running), 409);
        }
        if (hosted.ActiveProfileId is not { } profileId || await Profile.TryReadDataAsync(external, profileId, cancellationToken) is not { } data)
        {
            return ResponseEnvelope<PolarStateDto>.Fail("The node has no active profile to align", 409);
        }

        var configuration = request.Configuration?.ToConfiguration() ?? PolarAlignmentConfiguration.Default;
        var shown = new PolarFrames(frames);
        if (!PolarAlignmentRun.TryCreate(new PolarAlignmentRequest(request.OtaIndex, request.UseGuider, configuration), data, hub, external,
            catalog, solver, timeProvider, logger, shown.Captured, shown.Solved, out var alignment, out var refusal))
        {
            return ResponseEnvelope<PolarStateDto>.Fail(refusal, 409);
        }

        var run = new NodePolarRun(alignment, shown);
        if (!await hosted.TryStartAsync(run, profileId))
        {
            // Another run won the node between the check above and the start: the claim goes back with this one.
            await run.DisposeAsync();
            return ResponseEnvelope<PolarStateDto>.Fail(NodeRuns.AlreadyGoingOn(hosted.RunningKind), 409);
        }

        logger.LogInformation("Polar alignment through {Source}, rotating {Rotation} deg", alignment.SourceName, configuration.RotationDeg);
        return ResponseEnvelope<PolarStateDto>.Accepted(run.State);
    }

    /// <summary>The polar alignment going on, or the last one to end until the node's next run replaces it.</summary>
    public ResponseEnvelope<PolarStateDto> State()
        => hosted.CurrentRun is NodePolarRun run
            ? ResponseEnvelope<PolarStateDto>.Ok(run.State)
            : ResponseEnvelope<PolarStateDto>.NotFound("The node has run no polar alignment since its last run");

    /// <summary>Ends the polar alignment going on, Done and Cancel alike: answered at once, with the mount still to restore.</summary>
    public ResponseEnvelope<PolarStateDto> Stop()
    {
        // Only a polar alignment: a stop meant for one must never abort a run that replaced it.
        if (hosted.CurrentRun is not NodePolarRun { IsRunning: true } run || hosted.TryAbort(run) is null)
        {
            return ResponseEnvelope<PolarStateDto>.NotFound("No polar alignment is running");
        }
        return ResponseEnvelope<PolarStateDto>.Accepted(run.State);
    }
}

/// <summary>
/// Where a node's polar alignment puts what it captures: each frame becomes its OTA's, and each solve is kept with the
/// token of the frame it is of, which it was captured just before on the same thread.
/// </summary>
internal sealed class PolarFrames(NodeFrames frames)
{
    private sealed record Solution(WcsDto Wcs, int? FrameNumber);

    private volatile Solution? _latest;

    // The OTA the last frame came from; -1 until one did, and for the guider, whose frames are not shown.
    private volatile int _otaIndex = -1;

    public (WcsDto? Wcs, int? FrameNumber) Latest => _latest is { } latest ? (latest.Wcs, latest.FrameNumber) : (null, null);

    public void Captured(int otaIndex, Image frame)
    {
        _otaIndex = otaIndex;
        frames.PublishPreview(otaIndex, frame);
    }

    public void Solved(PlateSolveResult result)
    {
        // A failed solve drops the last one: a grid kept over a frame it no longer fits is worse than none.
        _latest = null;
        if (result.Solution is { } wcs)
        {
            _latest = new Solution(WcsDto.From(wcs), _otaIndex is >= 0 and var index ? frames.Ota(index).Number : null);
        }
    }
}

/// <summary>One polar alignment, as the node runs it.</summary>
internal sealed class NodePolarRun : INodeRun
{
    private readonly PolarAlignmentRun _run;
    private readonly PolarFrames _frames;
    private int _ended;

    public NodePolarRun(PolarAlignmentRun run, PolarFrames frames)
    {
        _run = run;
        _frames = frames;
    }

    public NodeRunKind Kind => NodeRunKind.Polar;

    /// <summary>Interactive: the user turns the knobs while watching it, so nobody watching is nobody aligning.</summary>
    public bool EndsUnwatched => true;

    /// <summary>Until its body has ended, whether or not it has begun: the node releases it at once.</summary>
    public bool IsRunning => Volatile.Read(ref _ended) == 0;

    public PolarStateDto State
    {
        get
        {
            var state = _run.State;
            var (wcs, frameNumber) = _frames.Latest;
            return new PolarStateDto
            {
                OtaIndex = _run.OtaIndex,
                Source = _run.SourceName,
                Phase = state.Phase,
                StatusMessage = state.StatusMessage,
                Running = IsRunning,
                FailureReason = state.FailureReason,
                PhaseA = state.PhaseA is { } phaseA ? PolarPhaseADto.From(phaseA) : null,
                LastSolve = state.LastSolve is { } live ? PolarLiveSolveDto.From(live) : null,
                Wcs = wcs,
                FrameNumber = frameNumber,
            };
        }
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            // Returns on the token (Done, Cancel, the detach grace, the host stopping), with the mount restored and the
            // devices given back; throws a failure it has recorded.
            await _run.RunAsync(cancellationToken);
        }
        finally
        {
            Volatile.Write(ref _ended, 1);
        }
    }

    /// <summary>Restores the mount and gives the devices back for a run whose body never ran; the body does its own.</summary>
    public ValueTask DisposeAsync() => _run.DisposeAsync();
}
