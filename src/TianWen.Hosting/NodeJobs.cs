using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TianWen.Hosting.Dto;
using TianWen.Lib.Devices;

namespace TianWen.Hosting;

/// <summary>
/// The node's slow operations, one model for all of them: a request STARTS a job and answers at once, the job
/// runs on the node's token, <see cref="TryGet"/> is authoritative, <see cref="Changed"/> is the latency hint
/// (<c>JOB-PROGRESS</c>), and <see cref="TryCancel"/> stops it (docs/plans/hardware-in-the-server.md, "Slow
/// operations are JOBS"; discovery is the first, P0b item 17, #752).
/// </summary>
/// <remarks>
/// <para><b>A job is the NODE's, never a request's</b>, the rule a run already follows (hosting-api.md,
/// invariant 7): it runs on the host's lifetime, so a client's short budget running out, or its connection
/// closing, never cancels a serial probe half-way. Only <see cref="TryCancel"/> and the host stopping do.</para>
/// <para><b>One of a kind at a time.</b> A second start of a kind already running JOINS it: a discovery is one
/// sweep of the ports, and a second would fight the first for them.</para>
/// <para><b>One job per device.</b> A job on a device (<see cref="TryStartOrJoin"/>) holds the device rather than its
/// kind: another start of the same kind joins it, one of another kind is refused (a disconnect half-way through a
/// connect would race it for the driver), and two devices run their jobs side by side.</para>
/// <para>A job that has ended stays answerable for a while (the last <see cref="EndedKept"/>), so a client that
/// reconnects after it ended still learns how.</para>
/// </remarks>
internal sealed class NodeJobs(IHostApplicationLifetime lifetime, ITimeProvider timeProvider, ILogger<NodeJobs> logger)
{
    internal const int EndedKept = 32;

    private readonly ConcurrentDictionary<string, Job> _jobs = new ConcurrentDictionary<string, Job>(StringComparer.Ordinal);
    // The running job in each slot: a kind for a job on no device, the device's key for a job on one. A device key is a
    // URI's left part ("Camera://FakeDevice/1"), so it can never be a kind's name.
    private readonly ConcurrentDictionary<string, Job> _running = new ConcurrentDictionary<string, Job>(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> _ended = new ConcurrentQueue<string>();

    /// <summary>A job started, moved on, or ended. Raised on the job's own thread.</summary>
    public event EventHandler<JobDto>? Changed;

    /// <summary>
    /// Starts a job of <paramref name="kind"/>, or answers the one of that kind already running.
    /// </summary>
    /// <param name="work">The job's body. It reports steps through the <see cref="JobStep"/> it is given and
    /// returns what it came to (the job's last step), or null.</param>
    public JobDto StartOrJoin(string kind, Func<JobStep, CancellationToken, Task<string?>> work)
    {
        // The slot is the kind itself, so whatever holds it is of this kind, and this only ever starts or joins.
        TryStartOrJoin(kind, slot: kind, deviceUri: null, work, joins: true, out var job);
        return job;
    }

    /// <summary>
    /// Starts a job of <paramref name="kind"/> on the device at <paramref name="deviceUri"/>, or answers the one already
    /// running on it. A device takes one job at a time: a start of the same kind JOINS the running one (a second connect
    /// of a camera is the same connect), and one of another kind is refused.
    /// </summary>
    /// <returns>False when a job of another kind holds the device; <paramref name="job"/> is then that job, which a
    /// refusal names.</returns>
    public bool TryStartOrJoin(string kind, Uri deviceUri, Func<JobStep, CancellationToken, Task<string?>> work, out JobDto job)
        => TryStartOrJoin(kind, deviceUri.DeviceKey, deviceUri.ToString(), work, joins: true, out job);

    /// <summary>
    /// Starts a job of <paramref name="kind"/> on the device, and ONLY starts one: any job already running on it, of this
    /// kind too, refuses it. For a job whose body owns something taken before it starts (a preview's lease on its camera),
    /// which a join would never run, and so never give back; and for one whose parameters make two of a kind different
    /// jobs (a preview of another exposure).
    /// </summary>
    /// <returns>False when a job holds the device; <paramref name="job"/> is then that job, which a refusal names.</returns>
    public bool TryStart(string kind, Uri deviceUri, Func<JobStep, CancellationToken, Task<string?>> work, out JobDto job)
        => TryStartOrJoin(kind, deviceUri.DeviceKey, deviceUri.ToString(), work, joins: false, out job);

    /// <summary>
    /// As <see cref="TryStart(string, Uri, Func{JobStep, CancellationToken, Task{string?}}, out JobDto)"/>, on a slot that
    /// is not a device: what a job holds that no device is, such as the frame of one OTA a solve reads.
    /// </summary>
    public bool TryStart(string kind, string slot, Func<JobStep, CancellationToken, Task<string?>> work, out JobDto job)
        => TryStartOrJoin(kind, slot, deviceUri: null, work, joins: false, out job);

    private bool TryStartOrJoin(string kind, string slot, string? deviceUri, Func<JobStep, CancellationToken, Task<string?>> work, bool joins, out JobDto job)
    {
        var started = new Job(Guid.NewGuid().ToString("N"), kind, slot, deviceUri, timeProvider.GetUtcNow());

        // The job is published before any of its work runs, so of two racing starts only the one whose job got in runs
        // anything, and the other answers with that job. A loop, not a retry by recursion: the job holding the slot can
        // end between the two looks, and then the slot is simply tried again.
        while (!_running.TryAdd(slot, started))
        {
            if (_running.TryGetValue(slot, out var running))
            {
                job = running.Snapshot;
                return joins && running.Kind == kind;
            }
        }

        // Only RunAsync gives the slot back, so from here to the body owning the job a throw would leave the device held by
        // a job that never runs (#1088): every later command on it answered a 409 naming a ghost, DELETE cancelled a token
        // nothing observed, and GET answered Running for ever. The body owns the job once Task.Run has returned, and not
        // before, so a throw up to then ends the job here as Failed, with its reason, and is the caller's to see.
        try
        {
            // The host stopping stops it. A registration rather than a linked source, so the source never needs
            // disposing and a cancel racing the job's end is always safe; the registration goes when the job does.
            started.StopsWithTheHost = lifetime.ApplicationStopping.Register(static state => (state as CancellationTokenSource)?.Cancel(), started.Cancellation);
            _jobs[started.Id] = started;
            Changed?.Invoke(this, started.Snapshot);
            _ = Task.Run(() => RunAsync(started, work), CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Job {Kind} {Id} failed to start", started.Kind, started.Id);
            End(started, JobState.Failed, step: null, error: ex.Message);
            TellChangedOfAFailedStart(started);
            throw;
        }

        job = started.Snapshot;
        return true;
    }

    /// <summary>
    /// Tells <see cref="Changed"/> a job that failed to start has ended, for whoever was told it began. Best effort: a
    /// subscriber whose throw failed the start may throw again, and that must not replace the failure being reported.
    /// </summary>
    private void TellChangedOfAFailedStart(Job job)
    {
        try
        {
            Changed?.Invoke(this, job.Snapshot);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "A Changed subscriber threw for job {Kind} {Id} failing to start", job.Kind, job.Id);
        }
    }

    /// <summary>
    /// The job running on the device, if one is: what a command that is not a job asks first, since switching a cooler
    /// off under a running ramp, or rebinning a camera a job is exposing, fights the job for the device.
    /// </summary>
    public bool TryGetRunningOn(Uri deviceUri, [NotNullWhen(true)] out JobDto? job)
    {
        job = _running.TryGetValue(deviceUri.DeviceKey, out var running) ? running.Snapshot : null;
        return job is not null;
    }

    /// <summary>How the job stands, running or ended; false once it has been forgotten, or never was.</summary>
    public bool TryGet(string id, [NotNullWhen(true)] out JobDto? job)
    {
        job = _jobs.TryGetValue(id, out var found) ? found.Snapshot : null;
        return job is not null;
    }

    /// <summary>Every job the node knows about, the running ones and the ended ones it still keeps, newest first.</summary>
    public JobDto[] List() => [.. _jobs.Values.Select(static job => job.Snapshot).OrderByDescending(static job => job.StartedUtc)];

    /// <summary>
    /// Asks the job to stop. It ends as <see cref="JobState.Cancelled"/> once its work notices; one that has
    /// already ended is left as it ended.
    /// </summary>
    public bool TryCancel(string id, [NotNullWhen(true)] out JobDto? job)
    {
        if (!_jobs.TryGetValue(id, out var found))
        {
            job = null;
            return false;
        }

        found.Cancellation.Cancel();
        job = found.Snapshot;
        return true;
    }

    private async Task RunAsync(Job job, Func<JobStep, CancellationToken, Task<string?>> work)
    {
        var token = job.Cancellation.Token;
        JobState state;
        string? step = null;
        string? error = null;
        try
        {
            step = await work(new JobStep(this, job), token);
            state = JobState.Succeeded;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            state = JobState.Cancelled;
            logger.LogInformation("Job {Kind} {Id} cancelled", job.Kind, job.Id);
        }
        catch (Exception ex)
        {
            state = JobState.Failed;
            error = ex.Message;
            logger.LogError(ex, "Job {Kind} {Id} failed", job.Kind, job.Id);
        }

        End(job, state, step, error);
        Changed?.Invoke(this, job.Snapshot);
    }

    /// <summary>
    /// Ends a job: its final snapshot, its slot given back, its host registration dropped, and its place among the ended
    /// jobs it keeps. The one place a job ends, whether its body ran (<see cref="RunAsync"/>) or never did.
    /// </summary>
    private void End(Job job, JobState state, string? step, string? error)
    {
        job.Snapshot = job.Snapshot with
        {
            State = state,
            Step = step ?? job.Snapshot.Step,
            Error = error,
            EndedUtc = timeProvider.GetUtcNow()
        };

        _running.TryRemove(new KeyValuePair<string, Job>(job.Slot, job));
        job.StopsWithTheHost.Dispose();
        _ended.Enqueue(job.Id);
        while (_ended.Count > EndedKept && _ended.TryDequeue(out var forgotten))
        {
            _jobs.TryRemove(forgotten, out _);
        }
    }

    private void Report(Job job, string step)
    {
        var current = job.Snapshot;
        if (current.State is not JobState.Running)
        {
            return;
        }

        job.Snapshot = current with { Step = step };
        Changed?.Invoke(this, job.Snapshot);
    }

    /// <summary>What a job's body reports through: the step it is on, pushed as <c>JOB-PROGRESS</c>.</summary>
    internal sealed class JobStep
    {
        private readonly NodeJobs _jobs;
        private readonly Job _job;

        internal JobStep(NodeJobs jobs, Job job)
        {
            _jobs = jobs;
            _job = job;
        }

        public void Report(string step) => _jobs.Report(_job, step);
    }

    internal sealed class Job(string id, string kind, string slot, string? deviceUri, DateTimeOffset startedUtc)
    {
        // A reference, so a write is atomic and a reader always sees a whole snapshot.
        private volatile JobDto _snapshot = new JobDto { Id = id, Kind = kind, DeviceUri = deviceUri, State = JobState.Running, StartedUtc = startedUtc };

        public string Id { get; } = id;

        public string Kind { get; } = kind;

        /// <summary>What the job holds while it runs: its kind, or the key of the device it acts on.</summary>
        public string Slot { get; } = slot;

        /// <summary>Never disposed: a source with no timer and no link holds nothing to release.</summary>
        public CancellationTokenSource Cancellation { get; } = new CancellationTokenSource();

        public CancellationTokenRegistration StopsWithTheHost { get; set; }

        public JobDto Snapshot
        {
            get => _snapshot;
            set => _snapshot = value;
        }
    }
}
