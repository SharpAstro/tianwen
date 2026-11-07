using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Devices;
using TianWen.Lib.Sequencing;
using TianWen.RemoteClient;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests.Functional;

/// <summary>
/// P5b's instrument (docs/plans/hardware-in-the-server.md, "P5b: mirror parity, part by part", #935): ONE real session
/// on fake devices, run by an in-process node on a pumped fake clock, read at every phase and at its first frames through
/// both paths the GUI has. The in-process path reads the <see cref="Session"/> itself, as the GUI's Local context does;
/// the mirror path polls the node over HTTP through <see cref="RemoteSessionMirror"/>, as a rig's context does. The two
/// <see cref="LiveSessionState"/>s are compared member by member, and each view's Live Session and Guider tabs and Home card
/// are drawn and compared too (<see cref="TabPictures"/>, part 9), since a tab also reads the session itself.
/// <para>
/// <see cref="KnownGaps"/> names every divergence known today and the part that closes it. The test fails on a
/// divergence the list does not name, and on a listed one that never diverged, so each part deletes its lines and the
/// list reads as the work left.
/// </para>
/// </summary>
[Collection("Session")]
public class MirrorParityTests(ITestOutputHelper output)
{
    /// <summary>A winter night at the fake rig's site (48.2 N, 16.3 E): dark from about 17:30 UTC.</summary>
    private static readonly DateTimeOffset WinterNight = new DateTimeOffset(2025, 12, 15, 17, 30, 0, TimeSpan.Zero);

    /// <summary>How many frames are held for a comparison of their own, beyond the phase boundaries.</summary>
    private const int FramesHeld = 3;

    /// <summary>Every member a mirror renders differently today, with the P5b part that closes it.</summary>
    private static readonly IReadOnlyDictionary<string, string> KnownGaps = new Dictionary<string, string>
    {
        // P6 (#936), the frame on show: a session empties its slot and an in-process view shows nothing, while a mirror
        // keeps the last frame it holds, as ReleaseCapturedImages says a client does. P6 makes the local view a mirror.
        ["LastCapturedImages[0]"] = "P6, the frame on show",
    };

    [Fact(Timeout = 300_000)]
    public async Task ASessionSeenThroughAMirrorMatchesItSeenInProcess()
    {
        // Record ToString formats its numbers in the current culture; both views must be printed alike.
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        var ct = TestContext.Current.CancellationToken;
        // Everything an observation carries is set to something other than its default, so a field the wire drops shows:
        // a catalogued target, a priority, a gain and an offset, and a plan of two filters at different sub-exposures.
        ScheduledObservation[] observations =
        [
            // Ten minutes, 600 ticks of the imaging loop, of which the held frames need about 130: a frame lands every 30 ticks
            // once the pump below steps a tick at a time, where at 5 s steps two fitted in three minutes and a run of the ten
            // held two.
            new ScheduledObservation(new Target(3.7886, 24.1167, "M45", CatalogIndex.M045), WinterNight, TimeSpan.FromMinutes(10),
                AcrossMeridian: false,
                FilterPlan: [new FilterExposure(0, TimeSpan.FromSeconds(20), 2), new FilterExposure(1, TimeSpan.FromSeconds(30), 2)],
                Gain: 120, Offset: 10, Priority: ObservationPriority.High),
        ];
        output.WriteLine($"{DateTimeOffset.Now:HH:mm:ss.fff} building the session");
        // The SkyWatcher fake is the mount that reports its mechanical axis angle, which the wire must carry too. A
        // SkyWatcher board keeps no site, so the rig's profile names it, as a real one does.
        await using var ctx = await SessionTestHelper.CreateSessionAsync(output, observations: observations, now: WinterNight,
            mountPort: "SkyWatcher", withFilterWheel: true, profileSite: new SiteCoordinates(48.2, 16.3, 200),
            cancellationToken: ct);
        output.WriteLine($"{DateTimeOffset.Now:HH:mm:ss.fff} starting the node");
        await using var node = await NodeHarness.StartAsync(output, ct,
            services => services.AddSingleton<ISessionFactory>(new OneSessionFactory(ctx.Session)));

        using var http = node.Transport.CreateHttpClient();
        await using var events = node.Transport.CreateEventStream(SystemTimeProvider.Instance, FakeExternal.CreateLogger(output));
        await using var mirror = new RemoteSessionMirror(new TianWenNodeClient(http), events, SystemTimeProvider.Instance,
            FakeExternal.CreateLogger(output))
        {
            // What the GUI asks of the rig on screen: its frames, the guider's included.
            Previews = new PreviewOptions(IncludeGuider: true),
        };

        // What SessionBootstrapper sets for a local run, and what RemoteRigConnection sets for a rig.
        var local = new LiveSessionState { ActiveSession = ctx.Session, IsRunning = true };
        var remote = new LiveSessionState { ActiveSession = mirror };

        using var holds = new SessionHolds(ct);
        ctx.TimeProvider.ExternalTimePump = true;
        var framesHeld = 0;
        ctx.Session.PhaseChanged += (_, e) => holds.Hold($"entering {e.NewPhase}");
        ctx.Session.FrameWritten += (_, _) =>
        {
            if (Interlocked.Increment(ref framesHeld) <= FramesHeld)
            {
                holds.Hold($"frame {Volatile.Read(ref framesHeld)} written");
            }
        };

        // Not awaited before the pump: the run's first phase can be raised on the request's own thread, and a hold there
        // keeps the request open until the test has read both views.
        output.WriteLine($"{DateTimeOffset.Now:HH:mm:ss.fff} starting the session on the node");
        var start = NodeHarness.EnvelopeStatusAsync(node.Client.PostAsync($"/api/v1/session/start?profileId={NodeHarness.ProfileId}", null, ct), ct);

        var diverged = new HashSet<string>(StringComparer.Ordinal);
        var unexplained = new List<string>();
        var compared = new List<string>();
        // Each view's tabs, drawn as a window draws them (P5b part 9): the snapshot compares what a tab reads off the
        // state, these what it draws, which includes what it reads off the session itself.
        using var localTabs = new TabPictures();
        using var remoteTabs = new TabPictures();

        async Task CompareAsync(string at)
        {
            output.WriteLine($"{DateTimeOffset.Now:HH:mm:ss.fff} comparing {at}, the session's clock at {ctx.TimeProvider.GetUtcNow():HH:mm:ss}, "
                + $"{ctx.Session.TotalFramesWritten} frames written, {ctx.Session.ImagingLoopTicks} imaging ticks, {ctx.Session.FetchesGivenUp} fetches given up");
            compared.Add(at);
            var (members, tabs) = await ReadBothAsync(local, remote, mirror, (localTabs, remoteTabs), ctx.TimeProvider, at, ct);
            foreach (var (member, inProcess, mirrored) in members)
            {
                diverged.Add(member);
                if (!KnownGaps.ContainsKey(member))
                {
                    unexplained.Add($"{at}: {member}: in-process <{inProcess}>, mirrored <{mirrored}>");
                }
            }
            foreach (var (tab, differences) in tabs)
            {
                diverged.Add(tab);
                if (!KnownGaps.ContainsKey(tab))
                {
                    unexplained.Add($"{at}: {tab}: {string.Join("; ", differences.Take(12))}{(differences.Count > 12 ? $" (and {differences.Count - 12} more)" : "")}");
                }
            }
        }

        // The pump. The clock moves only when the test advances it (ExternalTimePump, set before the run starts), so a hold
        // freezes every loop on it (the guider's, the cooling ramp, the imaging loop) and a comparison reads one instant.
        // It is the shared cooperative pump (FakeTimeProviderWrapper.PumpUntilAsync), which advances once the session is
        // parked on the clock and waits for what an advance released, with no wall-clock budget of this test's own. It
        // stops at each hold, which is compared here, and goes on. An advance runs on a thread of its own, since a timer
        // it fires can run the session inline, onto the very handler that holds it, and a hold reached during one is
        // compared from the wait on it (AdvanceAsync).
        //
        // While the session observes, until the frames this test holds are in, no step is longer than one tick of the
        // imaging loop. The loop counts a sub's seconds in the ticks it SEES, and a PeriodicTimer keeps one pending tick: a
        // 5 s step fires five 1 s ticks at once, and the loop saw about four of them (457 ticks in 109 advances locally).
        // Each tick it misses leaves the countdown behind the shutter, so the camera is idle again before the frame is
        // fetched and the loop starts the next exposure over it: with 5 s steps a local run wrote 8 or 9 frames in its 600
        // ticks, exposures 7 to 10 of one each overwritten, and on a slower runner every exposure is. CI run 41968369404
        // held frames 1 and 2 and no more before its observation ran out 530 s later. At one tick a step the loop has a
        // whole pump cycle to see each: stepping a tick throughout wrote 18 or 19 frames in 600 ticks, and with the step
        // relaxed after the held frames the third came by tick 130 in seven runs, none giving a fetch up. After the held
        // frames nothing depends on a frame, and the 5 s step is a fifth of the cost. The tick is the plan's: a sixth of
        // the 10 s gcd of its 20 s and 30 s subs, never under a second (Session.ImagingLoopAsync).
        var tick = TimeSpan.FromSeconds(1);
        var step = TimeSpan.FromSeconds(5);

        async Task AdvanceAsync(TimeSpan by)
        {
            var advance = Task.Run(() => ctx.TimeProvider.Advance(by), ct);
            while (!advance.IsCompleted)
            {
                await Task.WhenAny(advance, holds.WaitForNextAsync()).WaitAsync(ct);
                while (holds.TryTake(out var inside))
                {
                    await CompareAsync(inside);
                    holds.Release();
                }
            }
            await advance;
        }

        // The run has ended once the request that started it has answered and the node holds no run: a state the node gives
        // no signal of, so it is watched the way every wait on a node's own state is (NodeWait).
        var ended = Task.Run(async () =>
        {
            await start;
            await NodeWait.UntilAsync("the node's run to end",
                _ => ValueTask.FromResult((!node.Node.IsRunning, node.Node.IsRunning ? "still running" : "idle")), ct);
        }, ct);
        try
        {
            while (!ended.IsCompleted)
            {
                ct.ThrowIfCancellationRequested();
                // A segment of the pump runs from one hold to the next, and the phase changes only at a hold (each one is
                // raised through PhaseChanged), so the step is chosen here. The stall bound is in fake time, as it is for
                // every pumped test, and far past the longest stretch the imaging loop does not tick (the whole of
                // Finalising is 7 minutes).
                var ticking = ctx.Session.Phase is SessionPhase.Observing && Volatile.Read(ref framesHeld) < FramesHeld;
                await ctx.TimeProvider.PumpUntilAsync(
                    () => holds.HasReached || ended.IsCompleted,
                    maxFakeTime: TimeSpan.FromHours(2),
                    progress: () => ctx.Session.ImagingLoopTicks,
                    finestStep: ticking ? tick : null,
                    coarsestStep: ticking ? tick : step,
                    cancellationToken: ct,
                    advance: AdvanceAsync);
                while (holds.TryTake(out var at))
                {
                    await CompareAsync(at);
                    holds.Release();
                }
            }
            await ended;
        }
        catch (Exception ex)
        {
            // Let the run end, so the node can stop and the failure reports itself rather than a hang: nothing holds it
            // any more, it is aborted, and its Finalise is given the clock it waits on.
            holds.Close();
            await node.Client.PostAsync("/api/v1/session/abort", null, CancellationToken.None);
            while (node.Node.IsRunning && !ct.IsCancellationRequested)
            {
                ctx.TimeProvider.Advance(step);
                await Task.Delay(5, CancellationToken.None);
            }
            if (ex is OperationCanceledException && ct.IsCancellationRequested)
            {
                // Said as a wait says it (NodeWait): what the pump was doing when the test's own timeout ended it.
                throw new OperationCanceledException(
                    $"The test timed out pumping the session: its clock at {ctx.TimeProvider.GetUtcNow():HH:mm:ss} in {ctx.Session.Phase}, "
                    + $"{ctx.TimeProvider.WaiterCount} sleeps parked, {ctx.Session.TotalFramesWritten} frames written in {ctx.Session.ImagingLoopTicks} imaging ticks, "
                    + $"{(holds.IsHolding ? "a hold open" : "no hold open")}; compared at: {string.Join("; ", compared)}; {NodeWait.PoolState()}",
                    ex, ct);
            }
            throw;
        }

        (await start).ShouldBe(200);

        // Once the run has ended, the local bootstrapper has cleared IsRunning.
        local.IsRunning = false;
        await CompareAsync("after the run");

        output.WriteLine($"The session ended {ctx.Session.Phase}{(ctx.Session.FailureReason is { } why ? $": {why}" : "")}");
        output.WriteLine($"Compared at: {string.Join("; ", compared)}");
        output.WriteLine($"Diverged: {string.Join(", ", diverged.Order(StringComparer.Ordinal))}");
        ctx.Session.Phase.ShouldBe(SessionPhase.Complete, "the session ran its whole night");
        compared.ShouldContain("entering Observing");
        compared.Count(c => c.StartsWith("frame ", StringComparison.Ordinal)).ShouldBe(FramesHeld,
            $"the session wrote {ctx.Session.TotalFramesWritten} frames in {ctx.Session.ImagingLoopTicks} imaging ticks ({ctx.Session.FetchesGivenUp} fetches given up), "
            + $"and compared at: {string.Join("; ", compared)}");

        unexplained.ShouldBeEmpty("a mirror renders these differently, and no P5b part names them");
        KnownGaps.Keys.Where(g => !diverged.Contains(g)).ShouldBeEmpty("these agree now: delete them from KnownGaps");
    }

    /// <summary>
    /// Both views read, and their tabs drawn, while the session is held. The in-process view is read before and after the
    /// mirror's poll and the drawing; a session still settling between the two (a continuation already scheduled when the
    /// hold began) is read again.
    /// </summary>
    private static async Task<(List<(string Member, string InProcess, string Mirrored)> Members, List<(string Tab, List<string> Differences)> Tabs)> ReadBothAsync(
        LiveSessionState local, LiveSessionState remote, RemoteSessionMirror mirror,
        (TabPictures Local, TabPictures Remote) tabs, ITimeProvider time, string at, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            local.PollSession();
            var before = Snapshot(local);
            await mirror.PollOnceAsync(ct);
            remote.PollSession();
            var mirrored = Snapshot(remote);
            var mirroredTabs = tabs.Remote.Draw(remote, time);
            local.PollSession();
            var inProcessTabs = tabs.Local.Draw(local, time);
            var after = Snapshot(local);
            if (!before.SequenceEqual(after))
            {
                await Task.Delay(20, ct);
                continue;
            }
            var members = after.Keys.Union(mirrored.Keys)
                .Select(k => (Member: k, InProcess: after.GetValueOrDefault(k, "(absent)"), Mirrored: mirrored.GetValueOrDefault(k, "(absent)")))
                .Where(d => !string.Equals(d.InProcess, d.Mirrored, StringComparison.Ordinal))
                .ToList();
            var tabDifferences = inProcessTabs.Zip(mirroredTabs)
                .Select(pair => (Tab: pair.First.Tab, Differences: TabPictures.Differences(pair.First, pair.Second).ToList()))
                .Where(d => d.Differences.Count > 0)
                .ToList();
            return (members, tabDifferences);
        }
        throw new InvalidOperationException($"The session kept changing while it was held {at}");
    }

    /// <summary>
    /// What a tab reads off a <see cref="LiveSessionState"/>, one entry per member and per field of a composite member,
    /// so a gap names exactly what differs. Collections are listed element by element.
    /// </summary>
    private static SortedDictionary<string, string> Snapshot(LiveSessionState state)
    {
        var view = new SortedDictionary<string, string>(StringComparer.Ordinal);
        void Put(string member, object? value) => view[member] = value switch
        {
            null => "null",
            double d => d.ToString("R", CultureInfo.InvariantCulture),
            float f => f.ToString("R", CultureInfo.InvariantCulture),
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? "null",
        };
        void List<T>(string member, IReadOnlyList<T> items, Action<string, T> each)
        {
            Put($"{member}.Count", items.Count);
            for (var i = 0; i < items.Count; i++)
            {
                each($"{member}[{i}]", items[i]);
            }
        }

        Put(nameof(state.IsRunning), state.IsRunning);
        Put(nameof(state.HasActiveRun), state.HasActiveRun);
        Put(nameof(state.Mode), state.Mode);
        Put(nameof(state.OtaCount), state.OtaCount);
        Put(nameof(state.PendingPrompt), state.PendingPrompt?.Title);
        Put(nameof(state.Phase), state.Phase);
        // The state only: when the node last answered is a mirror's alone, since a session in this process has no node.
        Put("Contact.State", state.Contact.State);
        Put(nameof(state.TotalFramesWritten), state.TotalFramesWritten);
        Put(nameof(state.TotalExposureTime), state.TotalExposureTime);
        Put(nameof(state.CurrentObservationIndex), state.CurrentObservationIndex);
        Put(nameof(state.ObservationCount), state.ObservationCount);
        Put(nameof(state.CurrentActivity), state.CurrentActivity);
        Put(nameof(state.LastFramePath), state.LastFramePath);

        if (state.ActiveObservation is { } observation)
        {
            Put("ActiveObservation.Target.RA", observation.Target.RA);
            Put("ActiveObservation.Target.Dec", observation.Target.Dec);
            Put("ActiveObservation.Target.Name", observation.Target.Name);
            Put("ActiveObservation.Target.CatalogIndex", observation.Target.CatalogIndex);
            Put("ActiveObservation.Start", observation.Start);
            Put("ActiveObservation.Duration", observation.Duration);
            Put("ActiveObservation.AcrossMeridian", observation.AcrossMeridian);
            Put("ActiveObservation.Gain", observation.Gain);
            Put("ActiveObservation.Offset", observation.Offset);
            Put("ActiveObservation.Priority", observation.Priority);
            List("ActiveObservation.FilterPlan", observation.FilterPlan, (m, f) => Put(m, f));
        }
        else
        {
            Put("ActiveObservation", null);
        }

        var mount = state.MountState;
        Put("MountState.RightAscension", mount.RightAscension);
        Put("MountState.Declination", mount.Declination);
        Put("MountState.HourAngle", mount.HourAngle);
        Put("MountState.PierSide", mount.PierSide);
        Put("MountState.IsSlewing", mount.IsSlewing);
        Put("MountState.IsTracking", mount.IsTracking);
        Put("MountState.RaJ2000", mount.RaJ2000);
        Put("MountState.DecJ2000", mount.DecJ2000);
        Put("MountState.Altitude", mount.Altitude);
        Put("MountState.PrimaryAxisAngleDeg", mount.PrimaryAxisAngleDeg);
        Put(nameof(state.MountDisplayName), state.MountDisplayName);
        Put(nameof(state.MeridianFlipUtc), state.MeridianFlipUtc);
        Put(nameof(state.MountLimitVerdict), state.MountLimitVerdict);

        List(nameof(state.CameraStates), state.CameraStates, (m, c) =>
        {
            Put($"{m}.ExposureStart", c.ExposureStart);
            Put($"{m}.SubExposure", c.SubExposure);
            Put($"{m}.FrameNumber", c.FrameNumber);
            Put($"{m}.FilterName", c.FilterName);
            Put($"{m}.FocusPosition", c.FocusPosition);
            Put($"{m}.State", c.State);
            Put($"{m}.FocuserTemperature", c.FocuserTemperature);
            Put($"{m}.FocuserIsMoving", c.FocuserIsMoving);
        });
        List(nameof(state.LastFrameMetrics), state.LastFrameMetrics, (m, f) =>
        {
            Put($"{m}.StarCount", f.StarCount);
            Put($"{m}.MedianHfd", f.MedianHfd);
            Put($"{m}.MedianFwhm", f.MedianFwhm);
            Put($"{m}.Exposure", f.Exposure);
            Put($"{m}.Gain", f.Gain);
            Put($"{m}.FilterPosition", f.FilterPosition);
        });
        List(nameof(state.LastCapturedImages), state.LastCapturedImages,
            (m, image) => Put(m, image is null ? null : $"{image.Width}x{image.Height}x{image.ChannelCount}"));

        List(nameof(state.ExposureLog), state.ExposureLog, (m, e) => Put(m, e));
        List(nameof(state.CoolingSamples), state.CoolingSamples, (m, c) => Put(m, c));
        List(nameof(state.PhaseTimeline), state.PhaseTimeline, (m, p) => Put(m, p));
        List(nameof(state.FocusHistory), state.FocusHistory, (m, run) =>
        {
            Put($"{m}.Timestamp", run.Timestamp);
            Put($"{m}.OtaName", run.OtaName);
            Put($"{m}.FilterName", run.FilterName);
            Put($"{m}.BestPosition", run.BestPosition);
            Put($"{m}.BestHfd", run.BestHfd);
            Put($"{m}.FitA", run.FitA);
            Put($"{m}.FitB", run.FitB);
            List($"{m}.Curve", run.Curve, (n, sample) => Put(n, sample));
        });
        List(nameof(state.ActiveFocusSamples), state.ActiveFocusSamples, (m, sample) => Put(m, sample));

        Put(nameof(state.GuiderState), state.GuiderState);
        Put(nameof(state.GuideExposure), state.GuideExposure);
        Put(nameof(state.GuideStarPosition), state.GuideStarPosition);
        Put(nameof(state.GuideStarSNR), state.GuideStarSNR);
        Put(nameof(state.GuideStarProfile), state.GuideStarProfile is { } profile ? $"{profile.H.Length}+{profile.V.Length}" : null);
        Put(nameof(state.CalibrationOverlay), state.CalibrationOverlay is null ? null : "present");
        Put(nameof(state.GuiderSettleProgress), state.GuiderSettleProgress is { } settle
            ? $"done {settle.Done}, {settle.Distance} of {settle.SettlePx} px, {settle.Time} of {settle.SettleTime} s" : null);
        Put(nameof(state.LastGuideFrame), state.LastGuideFrame is { } frame ? $"{frame.Width}x{frame.Height}" : null);
        if (state.LastGuideStats is { } stats)
        {
            Put("LastGuideStats.TotalRMS", stats.TotalRMS);
            Put("LastGuideStats.RaRMS", stats.RaRMS);
            Put("LastGuideStats.DecRMS", stats.DecRMS);
            Put("LastGuideStats.PeakRa", stats.PeakRa);
            Put("LastGuideStats.PeakDec", stats.PeakDec);
            Put("LastGuideStats.LastRaErr", stats.LastRaErr);
            Put("LastGuideStats.LastDecErr", stats.LastDecErr);
            Put("LastGuideStats.LastRaPulseMs", stats.LastRaPulseMs);
            Put("LastGuideStats.LastDecPulseMs", stats.LastDecPulseMs);
        }
        else
        {
            Put("LastGuideStats", null);
        }
        List(nameof(state.GuideSamples), state.GuideSamples, (m, s) => Put(m, s));
        return view;
    }

    /// <summary>
    /// Holds the session where it raised the event: the handler blocks until the test has read both views, so each
    /// comparison sees one instant of the run.
    /// </summary>
    private sealed class SessionHolds(CancellationToken ct) : IDisposable
    {
        // A channel, not a queue, so that a hold reached is a signal a pump can wait on beside its other signals.
        private readonly Channel<string> _reached = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
        private readonly SemaphoreSlim _released = new SemaphoreSlim(0);
        private int _holding;
        private int _closed;

        public bool IsHolding => Volatile.Read(ref _holding) > 0;

        /// <summary>A hold has been reached and not yet taken.</summary>
        public bool HasReached => _reached.Reader.TryPeek(out _);

        /// <summary>Called on the session's thread: blocks it until <see cref="Release"/>, unless the holds are closed.</summary>
        public void Hold(string at)
        {
            if (Volatile.Read(ref _closed) == 1)
            {
                return;
            }
            Interlocked.Increment(ref _holding);
            _reached.Writer.TryWrite(at);
            try
            {
                _released.Wait(ct);
            }
            finally
            {
                Interlocked.Decrement(ref _holding);
            }
        }

        public bool TryTake([NotNullWhen(true)] out string? at) => _reached.Reader.TryRead(out at);

        /// <summary>Completes once a hold has been reached and not yet taken, at once when one is. Takes nothing.</summary>
        public Task WaitForNextAsync() => _reached.Reader.WaitToReadAsync(ct).AsTask();

        public void Release() => _released.Release();

        /// <summary>Lets every held thread go and holds nothing more: a run ending after the test has stopped comparing.</summary>
        public void Close()
        {
            if (Interlocked.Exchange(ref _closed, 1) == 0)
            {
                _released.Release(int.MaxValue / 2);
            }
        }

        // Closed, not disposed: a thread still inside Wait would see a disposed semaphore throw.
        public void Dispose() => Close();
    }

    /// <summary>The node's session factory, handing out the one session the test built.</summary>
    private sealed class OneSessionFactory(ISession session) : ISessionFactory
    {
        public ValueTask InitializeAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ISession Create(Guid profileId, in SessionConfiguration configuration, ReadOnlySpan<ScheduledObservation> observations)
            => session;
    }
}
