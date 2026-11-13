using System;
using System.Diagnostics;
using System.Threading;
using Xunit;

namespace TianWen.Lib.Tests.Functional;

/// <summary>
/// Says into a test's output when the test process, or only its thread pool, stopped running for a second or more, and
/// what it did meanwhile: a timeout otherwise says that 30 s passed and nothing of why (#2023).
/// <para>
/// CI run 41939984127 and a local run on a busy machine each lost 8 to 16 s with no line of the node's log between two
/// that were milliseconds apart in every other run, and a wait that logs only when its own loop runs cannot say whether
/// the process was starved, its pool was clogged, or the code was stuck. Two probes tell them apart. A dedicated thread (never
/// the pool's) asks to wake every <see cref="Tick"/>: late means the process as a whole was not scheduled, and the CPU time and
/// GC pause it spent over the gap say whether the operating system withheld it (neither moved) or the process spent it itself
/// (one did). A work item queued to the pool each time the last one ran: late while the dedicated thread is on time means
/// the pool is held by work that does not yield, and the threads and the queue say how much.
/// </para>
/// </summary>
internal sealed class StallWatch : IDisposable
{
    /// <summary>How often the dedicated thread wakes.</summary>
    internal static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(100);

    /// <summary>The lateness worth a line: shorter ones are an ordinary busy runner.</summary>
    internal static readonly TimeSpan Threshold = TimeSpan.FromSeconds(1);

    private readonly Action<string> _say;
    private readonly bool _probePool;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly ManualResetEventSlim _stop = new();
    private readonly Thread? _thread;

    private TimeSpan _last;
    private TimeSpan _lastCpu;
    private TimeSpan _lastGcPause;
    private PoolProbe? _probe;

    /// <summary>Watches until disposed, writing each stall to <paramref name="output"/>, which a timed-out test closes first.</summary>
    public StallWatch(ITestOutputHelper output)
    {
        _say = line => Write(output, line);
        _probePool = true;
        Seed();
        _thread = new Thread(Run) { Name = "StallWatch", IsBackground = true };
        _thread.Start();
    }

    /// <summary>The watch with no thread: a test calls <see cref="Observe"/> with the times it chooses.</summary>
    internal StallWatch(Action<string> say, bool probePool)
    {
        _say = say;
        _probePool = probePool;
        Seed();
    }

    private void Seed()
    {
        _lastCpu = Environment.CpuUsage.TotalTime;
        _lastGcPause = GC.GetTotalPauseDuration();
    }

    /// <summary>
    /// Writes one line to the test's output. A test that outran its timeout is abandoned by xunit with its body still running,
    /// and its output is closed under it: the next write throws "There is no currently active test", on THIS thread, which
    /// ends the whole test host (iterations of j4184 lost 5 of 13 tests to it). <see cref="TestOutputLine"/> takes that answer, and a
    /// line it could not write stops the watch, which has nothing left to say it to.
    /// </summary>
    private void Write(ITestOutputHelper output, string line)
    {
        if (!TestOutputLine.Write(output, $"{DateTime.Now:HH:mm:ss.fff} warn [StallWatch] {line}"))
        {
            _stop.Set();
        }
    }

    private void Run()
    {
        while (!_stop.Wait(Tick))
        {
            Observe(_clock.Elapsed, Environment.CpuUsage.TotalTime, GC.GetTotalPauseDuration());
        }
    }

    /// <summary>
    /// One look: <paramref name="now"/> is the watch's own clock, <paramref name="cpu"/> the process's CPU time so far and
    /// <paramref name="gcPause"/> the total time the runtime has held every thread for a collection.
    /// </summary>
    internal void Observe(TimeSpan now, TimeSpan cpu, TimeSpan gcPause)
    {
        var slept = now - _last;
        if (slept - Tick >= Threshold)
        {
            _say(FormattableString.Invariant($"the test process did not run for {slept.TotalSeconds:0.0} s (this thread asked for {Tick.TotalSeconds:0.0} s): ")
                + FormattableString.Invariant($"cpu +{(cpu - _lastCpu).TotalSeconds:0.0} s, gc pause +{(gcPause - _lastGcPause).TotalMilliseconds:0} ms, {PoolState()}"));
        }
        _last = now;
        _lastCpu = cpu;
        _lastGcPause = gcPause;

        if (!_probePool)
        {
            return;
        }

        var probe = _probe;
        if (probe is { Ran: true })
        {
            if (probe.Reported)
            {
                _say(FormattableString.Invariant($"the thread pool ran a work item after {(probe.RanAt - probe.QueuedAt).TotalSeconds:0.0} s"));
            }
            _probe = probe = null;
        }
        else if (probe is { Reported: false } && now - probe.QueuedAt >= Threshold)
        {
            probe.Reported = true;
            _say(FormattableString.Invariant($"the thread pool has run no work item for {(now - probe.QueuedAt).TotalSeconds:0.0} s while this thread ran: {PoolState()}"));
        }

        if (probe is null)
        {
            _probe = new PoolProbe(now, _clock);
            ThreadPool.UnsafeQueueUserWorkItem(static probe => probe.Run(), _probe, preferLocal: false);
        }
    }

    private static string PoolState() =>
        $"thread pool {ThreadPool.ThreadCount} threads, {ThreadPool.PendingWorkItemCount} queued, {ThreadPool.CompletedWorkItemCount} done";

    public void Dispose()
    {
        _stop.Set();
        // Short, and before the test's output closes: a line written after it would be lost, or thrown. A thread that
        // did not stop in time is left its event, which it still waits on.
        if (_thread is null || _thread.Join(TimeSpan.FromSeconds(2)))
        {
            _stop.Dispose();
        }
    }

    private sealed class PoolProbe(TimeSpan queuedAt, Stopwatch clock)
    {
        private TimeSpan _ranAt;
        private int _ran;

        public TimeSpan QueuedAt { get; } = queuedAt;

        /// <summary>Set by the watch's thread alone, once it has said the probe is late.</summary>
        public bool Reported { get; set; }

        public bool Ran => Volatile.Read(ref _ran) == 1;

        public TimeSpan RanAt => _ranAt;

        public void Run()
        {
            _ranAt = clock.Elapsed;
            Volatile.Write(ref _ran, 1);
        }
    }
}
