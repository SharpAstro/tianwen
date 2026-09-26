using System;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TianWen.Hosting.Api;
using TianWen.Hosting.Dto;
using TianWen.Lib.Devices;
using TianWen.Lib.Imaging;
using TianWen.Lib.Sequencing;

namespace TianWen.Hosting;

/// <summary>
/// A dark library taken by the node (P5 part 1 of docs/plans/hardware-in-the-server.md, #934): what the CLI's
/// <c>darks</c> does with a camera of its own, as a run the node owns, so it finishes if the window that asked for it
/// goes away, and nothing else can take the camera while it runs. The frames are <see cref="DarkFrameRun"/>'s, the one
/// capture the CLI uses too.
/// </summary>
/// <remarks>
/// The refusals come in the device plane's order (<see cref="DeviceOperations"/>): a run going on first, then ownership,
/// then the camera itself, then a job working on it. The camera is LEASED for the whole run, so a device command, a job
/// or a session asking for it is refused naming the dark library, and the lease goes as the run ends, however it ends.
/// </remarks>
internal sealed class NodeDarkLibrary(IDeviceHub hub, NodeJobs jobs, IHostedSession hosted, DarkFrameRun capture, ILogger<NodeDarkLibrary> logger)
{
    /// <summary>What the lease on the camera is called, which a refusal names.</summary>
    internal const string LeaseOwner = "dark library";

    public async Task<ResponseEnvelope<DarkLibraryStateDto>> StartAsync(DarkLibraryRequestDto request)
    {
        if (request.Count < 1 || !double.IsFinite(request.ExposureSeconds) || request.ExposureSeconds <= 0 || request.Bin < 1)
        {
            return ResponseEnvelope<DarkLibraryStateDto>.Fail("A dark library takes at least one frame, of a positive exposure, at a binning of 1 or more");
        }
        if (!Uri.TryCreate(request.DeviceUri, UriKind.Absolute, out var uri))
        {
            return ResponseEnvelope<DarkLibraryStateDto>.Fail($"Not a device URI: {request.DeviceUri}");
        }
        if (hosted.RunningKind is { } running)
        {
            return ResponseEnvelope<DarkLibraryStateDto>.Fail(NodeRuns.AlreadyGoingOn(running), 409);
        }

        var ownership = DeviceOwnershipGate.Evaluate(hub, uri, DeviceAction.Actuate);
        if (!ownership.Allowed)
        {
            return ResponseEnvelope<DarkLibraryStateDto>.Fail(ownership.Describe(), 409);
        }
        var name = hub.TryGetDeviceFromUri(uri, out var device) ? device.DisplayName : uri.ToString();
        if (!hub.IsConnected(uri))
        {
            return ResponseEnvelope<DarkLibraryStateDto>.NotFound($"{name} is not connected");
        }
        if (!hub.TryGetConnectedDriver<ICameraDriver>(uri, out var camera))
        {
            return ResponseEnvelope<DarkLibraryStateDto>.Fail($"{name} is not a camera");
        }
        if (jobs.TryGetRunningOn(uri, out var job))
        {
            return ResponseEnvelope<DarkLibraryStateDto>.Fail($"{name} is busy: a {job.Kind} of it is running (job {job.Id})", 409);
        }
        if (!DeviceLeaseSet.TryAcquire(hub, [uri], LeaseOwner, out var claim, out var refusal))
        {
            return ResponseEnvelope<DarkLibraryStateDto>.Fail(refusal.Describe(), 409);
        }

        var options = new DarkFrameRunOptions(TimeSpan.FromSeconds(request.ExposureSeconds), request.Count, request.Gain, request.Offset,
            request.Bin, request.Bias ? FrameType.Bias : FrameType.Dark);
        var run = new DarkLibraryRun(capture, camera, name, uri, options, claim, logger);
        if (!await hosted.TryStartAsync(run, hosted.ActiveProfileId ?? Guid.Empty))
        {
            // Another run won the node between the check above and the start: the lease goes back with this one.
            await run.DisposeAsync();
            return ResponseEnvelope<DarkLibraryStateDto>.Fail(NodeRuns.AlreadyGoingOn(hosted.RunningKind), 409);
        }

        logger.LogInformation("Taking {Count} {Kind} of {Exposure} s with {Camera}", options.Count, options.FrameType, request.ExposureSeconds, name);
        return ResponseEnvelope<DarkLibraryStateDto>.Accepted(run.State);
    }

    /// <summary>The dark library going on, or the last one to end until the node's next run replaces it.</summary>
    public ResponseEnvelope<DarkLibraryStateDto> State()
        => hosted.CurrentRun is DarkLibraryRun run
            ? ResponseEnvelope<DarkLibraryStateDto>.Ok(run.State)
            : ResponseEnvelope<DarkLibraryStateDto>.NotFound("The node has taken no dark library since its last run");

    /// <summary>Stops the dark library going on after the frame in hand, and answers how it ended.</summary>
    public async Task<ResponseEnvelope<DarkLibraryStateDto>> StopAsync(CancellationToken cancellationToken)
    {
        // Only a dark library: a stop meant for one must never abort a session that replaced it.
        if (hosted.CurrentRun is not DarkLibraryRun { IsRunning: true } run || hosted.TryAbort(run) is not { } ended)
        {
            return ResponseEnvelope<DarkLibraryStateDto>.NotFound("No dark library is being taken");
        }

        await ended.WaitAsync(cancellationToken);
        return ResponseEnvelope<DarkLibraryStateDto>.Ok(run.State);
    }
}

/// <summary>One dark library, as the node runs it: the camera it holds, and every frame written so far.</summary>
internal sealed class DarkLibraryRun(DarkFrameRun capture, ICameraDriver camera, string cameraName, Uri deviceUri, DarkFrameRunOptions options,
    DeviceLeaseSet claim, ILogger logger) : INodeRun, IProgress<DarkFrameCaptured>
{
    private ImmutableArray<DarkLibraryFrameDto> _frames = [];
    private int _ended;
    private volatile bool _stopped;
    private volatile string? _failure;

    public NodeRunKind Kind => NodeRunKind.Darks;

    /// <summary>A dark library finishes its count whether or not anyone watches the frames being written.</summary>
    public bool EndsUnwatched => false;

    /// <summary>Until its body has ended, whether or not it has begun: the node releases it at once.</summary>
    public bool IsRunning => Volatile.Read(ref _ended) == 0;

    public DarkLibraryStateDto State => new DarkLibraryStateDto
    {
        Camera = cameraName,
        DeviceUri = deviceUri.ToString(),
        Bias = options.FrameType is FrameType.Bias,
        ExposureSeconds = options.Exposure.TotalSeconds,
        Count = options.Count,
        Frames = [.. _frames],
        Running = IsRunning,
        Stopped = _stopped,
        FailureReason = _failure,
    };

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            await capture.RunAsync(camera, options, this, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _stopped = true;
            throw;
        }
        catch (Exception ex)
        {
            _failure = ex.Message;
            throw;
        }
        finally
        {
            // The camera goes back as the body ends, not when the node's next run replaces this one: an ended run that
            // went on holding it would refuse every command to it in the meantime.
            claim.Dispose();
            Volatile.Write(ref _ended, 1);
        }
    }

    void IProgress<DarkFrameCaptured>.Report(DarkFrameCaptured frame)
        => ImmutableInterlocked.Update(ref _frames, static (frames, added) => frames.Add(added), new DarkLibraryFrameDto
        {
            Path = frame.Path,
            StartedUtc = frame.StartedUtc,
            SensorTemperatureC = double.IsFinite(frame.SensorTemperatureC) ? frame.SensorTemperatureC : null,
        });

    /// <summary>Releases the camera for a run whose body never ran (its start lost the node); the body releases its own.</summary>
    public ValueTask DisposeAsync()
    {
        claim.Dispose();
        logger.LogDebug("The dark library with {Camera} is done with the camera", cameraName);
        return ValueTask.CompletedTask;
    }
}
