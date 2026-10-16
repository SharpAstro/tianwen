using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using TianWen.Hosting.Dto;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.ToupTek;
using TianWen.Lib.Imaging;
using TianWen.Lib.Tests;
using TianWen.UI.Abstractions;
using Xunit;
using static TianWen.Lib.Tests.ToupTekFrameRateProbe;

namespace TianWen.Lib.Tests.Functional;

/// <summary>
/// The bar TIA2's node leg holds the GUI's planetary capture to on the attached ToupTek G3M678M: a recording made THROUGH the
/// node (a real <c>tianwen-server</c>, local-only, on a scratch data root and a socket of its own, holding the camera in a
/// profile) at the Saturn window (640 x 480 at 1716, 546, 8 bits, 5 ms, gain 2252), started by the GUI's own signal handler
/// and watched by its planetary view, which reads the camera's live frame through shared memory the whole time (RAW on show,
/// its render thread driven at 60 Hz). Each recording is judged by its SER read back: at least 190 fps (frame count over its
/// own timestamp span), nothing replaced by the hand-off, nothing dropped by the body or the disk, no step between frames
/// over <see cref="ToupTekFrameRateProbe.LongStepBound"/>; a recovered stall is reported apart and the run made again.
/// </summary>
/// <remarks>
/// <para>Beside the gate, in one session and in this order, so that each comparison is adjacent in time: #1596's in-process
/// recording (<see cref="ToupTekFrameRateProbe.CaptureAsync"/>), a node recording with no live view attached, the gated
/// recordings with the view, and the in-process recording again. Each recording with the view must be within 3 % of the
/// in-process rate, so a slow box cannot pass on the absolute number alone. The host's load is printed beside every run,
/// and before each one the probe waits while another worktree named by <see cref="OtherWorktreeBuilds.QuietForVar"/> builds
/// or tests. Through the whole node part it samples the node's, its keeper's and this process's TCP listeners and UDP
/// endpoints, and every one must be on loopback.</para>
/// <para>The capture is started by <see cref="StartVideoCaptureSignal"/>, as the planetary panel's Start posts it, and the
/// recording by <see cref="StartPlanetaryRecordingSignal"/>, as the panel's Record control posts it (#1344), followed on the
/// recording's own state as the panel reads it. The baseline with no view records on a client of its own, the GUI not being in
/// it. The gate measures the node's path with the GUI's own client, not the renderer's draw.</para>
/// <para>Set <c>TIANWEN_TOUPTEK_RATE_PROBE=1</c> with the camera attached and no other program holding it; the recordings
/// are <c>TIANWEN_TOUPTEK_RECORD_RUNS</c> (3) of <c>TIANWEN_TOUPTEK_RECORD_SECONDS</c> (60). Each SER (about 3.6 GB a
/// minute) is deleted once read back. The node's own counts (the hand-off's replaced frames, its stalls) are read from its
/// log once it has exited, since the wire does not carry them.</para>
/// </remarks>
[Collection("NodeProcesses")]
public class NodePlanetaryRateProbe(ITestOutputHelper output)
{
    private static readonly Guid ProfileId = Guid.Parse("7e57ab1e-0b0e-4e5d-9a5e-000000000813");

    /// <summary>Runs made again for a stall, at most, unless <c>TIANWEN_TOUPTEK_EXTRA_RUNS</c> says how many: the camera stalls in periods.</summary>
    private const int ExtraRuns = 2;

    /// <summary>How often the probe drives the view, as a render loop would.</summary>
    private static readonly TimeSpan RenderFrame = TimeSpan.FromMilliseconds(16);

    /// <summary>From the jog to the first frame measured: the pan (32 to 285 ms at 5 ms), the control's send, and a warm-up.</summary>
    private static readonly TimeSpan PanAndWarm = TimeSpan.FromSeconds(3);

    private static readonly TimeSpan EndpointSampleEvery = TimeSpan.FromSeconds(1);

    private static PlanetaryRequestDto Request => new PlanetaryRequestDto
    {
        OtaIndex = 0,
        ExposureMs = SaturnExposure.TotalMilliseconds,
        Gain = SaturnGain,
        RoiWidth = RoiWidth,
        RoiHeight = RoiHeight,
        BitDepth = BitDepth.Int8,
    };

    /// <summary>One recording: its arm, its SER read back against the time it was asked for, the counts over it, and what the box was doing.</summary>
    private sealed record Run(string Arm, SerRateReadBack Ser, TimeSpan Asked, int BodyDropped, int DiskDropped, string Load, string Quiet, string? View = null,
        long? Replaced = null, int? LoggedStalls = null, string? Stream = null, string? Answers = null,
        IReadOnlyList<(DateTimeOffset Asked, int Gain)>? GainsAsked = null, string? GainsReached = null, TimeSpan? SlowestGain = null)
    {
        /// <summary>
        /// Where it stands (<see cref="RateGate.StandingOf"/>): set aside only for a stall its stream logged (a node's, known once
        /// the node has exited), and made again also when its SER only looks stalled.
        /// </summary>
        public RateGate.Standing Standing => RateGate.StandingOf(LoggedStalls, Ser, Asked);
    }

    /// <summary>
    /// One video stream of the node's camera, from its log: where it started, its own end-of-stream counts, and the window the
    /// camera had after its last pan (the DAL's own line once the body has the window), null when it never panned.
    /// </summary>
    private sealed record NodeStream(int X, int Y, string Counts, long Replaced, int Stalls, int Recovered, List<string> Recoveries,
        int Width = 0, int Height = 0, RoiRect? Panned = null, IReadOnlyList<(DateTimeOffset At, int Gain)>? Gains = null);

    [Fact(Timeout = 3_600_000)]
    public async Task TheGuisCaptureRecordsAtTheCamerasRateThroughTheNode()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable(GateVar) == "1", $"{GateVar} is not 1 (this drives the attached ToupTek camera)");
        var ct = TestContext.Current.CancellationToken;
        var device = new ToupTekDeviceSource().RegisteredDevices(DeviceType.Camera).FirstOrDefault();
        Assert.SkipWhen(device is null, "no ToupTek camera attached");
        var camera = device.DeviceUri;
        var wanted = IntFromEnvironment("TIANWEN_TOUPTEK_RECORD_RUNS", 3);
        var duration = TimeSpan.FromSeconds(IntFromEnvironment("TIANWEN_TOUPTEK_RECORD_SECONDS", 60));
        var extra = IntFromEnvironment("TIANWEN_TOUPTEK_EXTRA_RUNS", ExtraRuns);
        SamplerSeesThisProcesssOwnSockets();

        var (inProcessBefore, centred) = await InProcessAsync("in process, before", device, duration, ct);

        var runs = new List<Run>();
        var endpoints = new HashSet<OwnedEndpoints.Endpoint>();
        List<NodeStream> streams;
        string[] streamLines;
        int[] watched;
        var kept = await KeptNode.StartAsync(ct, dataRoot => SeedProfileAsync(dataRoot, camera, ct), fakeDevicesOnly: false);
        try
        {
            var nodePid = (await kept.Client.GetNodeAsync(ct)).Value.ShouldNotBeNull("the node answers").ProcessId;
            watched = [Environment.ProcessId, kept.Keeper.Id, nodePid];
            output.WriteLine($"node pid {nodePid}, keeper pid {kept.Keeper.Id}, this client's pid {Environment.ProcessId}, data root {kept.DataRoot}"
                + $"{(Environment.GetEnvironmentVariable("DOTNET_gcServer") is { } gcServer ? $", DOTNET_gcServer={gcServer} (the node inherits it)" : "")}");

            var gui = GuiNodeHarness.ComposeClient(output);
            try
            {
                var pump = new Pump(gui, watched, endpoints);
                await gui.ConnectAsync(kept.SocketPath, LocalOnlyMode.On, includeFake: false, ct);
                var local = gui.AppState.LocalNode.ShouldNotBeNull();

                // Connected as the Equipment tab's row connects it: the node's job.
                gui.Bus.Post(new ConnectDeviceSignal(camera));
                await pump.UntilAsync(_ => ValueTask.FromResult(!gui.Equipment.PendingTransitions.ContainsKey(camera) && local.IsConnected(camera)), ct);

                var jog = (X: RoiX - centred.X, Y: RoiY - centred.Y);
                runs.Add(await NoViewAsync(kept, pump, jog, duration, ct));
                var planetary = gui.Services.GetRequiredService<PlanetaryCaptureController>();
                for (var run = 1; runs.Count(static r => r.Arm.StartsWith("node, view", StringComparison.Ordinal) && !r.Standing.MakesAnother) < wanted
                    && run <= wanted + extra; run++)
                {
                    runs.Add(await WithViewAsync($"node, view {run}", gui, local, planetary, pump, jog, duration, ct));
                }

                await NodeWait.UntilTheJobSucceedsAsync(kept.Client, await kept.Client.DisconnectDeviceAsync(camera, skipWarmUp: true, ct), ct);
            }
            finally
            {
                await DisposeAsync(gui);
            }

            // The node's own counts are in its log, read once it has exited: never a file a running process writes.
            (await kept.Client.ShutdownNodeAsync(ct)).IsSuccess.ShouldBeTrue("the node takes its own safe stop");
            await kept.KeeperExitAsync(ct);
            streams = ReadStreams(kept.DataRoot);
            streamLines = StreamLines(kept.DataRoot);
        }
        finally
        {
            await kept.DisposeAsync();
        }

        // The comparison the gate is for needs a clean in-process recording: made again while none is, as the view's are.
        var inProcessRuns = new List<Run> { inProcessBefore, (await InProcessAsync("in process, after", device, duration, ct)).Run };
        for (var again = 1; inProcessRuns.All(static r => r.Standing.SetAside) && again <= extra; again++)
        {
            inProcessRuns.Add((await InProcessAsync($"in process, again {again}", device, duration, ct)).Run);
        }

        // The node's streams are its runs, in order.
        streams.Count.ShouldBe(runs.Count, "one video stream of the node's camera for each node recording");
        for (var i = 0; i < runs.Count; i++)
        {
            var stream = streams[i];
            (stream.X, stream.Y).ShouldBe((centred.X, centred.Y), "the node's stream starts centred, where the jog to the Saturn window is counted from");
            // The window recorded is the one asked for (review round 1 of #1609, N4): the window the camera had after the pan, as
            // the DAL logs it once the body has it, never the jog the capture loop asked for (review round 2 of #1609), and the
            // SER's own header.
            stream.Panned.ShouldBe(new RoiRect(RoiX, RoiY, RoiWidth, RoiHeight), $"{runs[i].Arm}: the node's camera streamed {RoiWidth}x{RoiHeight} at ({RoiX}, {RoiY})");
            (runs[i].Ser.Width, runs[i].Ser.Height, runs[i].Ser.PixelDepth).ShouldBe((RoiWidth, RoiHeight, 8), $"{runs[i].Arm}: the SER holds 640x480 at 8 bits");
            var (gainsReached, slowestGain) = runs[i].GainsAsked is { Count: > 0 } gainsAsked ? GainsReached(gainsAsked, stream.Gains ?? []) : (null, null);
            runs[i] = runs[i] with
            {
                Replaced = stream.Replaced,
                LoggedStalls = stream.Stalls,
                Stream = stream.Counts + (stream.Recoveries.Count > 0 ? $"; {string.Join("; ", stream.Recoveries)}" : ""),
                GainsReached = gainsReached,
                SlowestGain = slowestGain,
            };
        }

        Run[] all = [inProcessRuns[0], .. runs, .. inProcessRuns.Skip(1)];
        // Only an in-process recording that fails nothing by itself is the baseline (review round 2 of #1609).
        var baseline = RateGate.InProcessBaseline(inProcessRuns.Where(static r => !r.Standing.SetAside)
            .Select(static r => new RateGate.InProcessRun(r.Ser, r.Replaced, r.BodyDropped, r.DiskDropped)));
        var against = baseline is { } rate ? $"this run's in-process {rate:F1} fps" : "NO clean in-process recording in this run";
        output.WriteLine("");
        output.WriteLine($"The ToupTek at the Saturn window ({RoiWidth}x{RoiHeight} at {RoiX}, {RoiY}), 8 bits, {SaturnExposure.TotalMilliseconds} ms, gain {SaturnGain}, "
            + $"{duration.TotalSeconds:F0} s a recording; the camera's own rate at this window is about 196.8 fps (200 at most at 5 ms)");
        foreach (var run in all)
        {
            output.WriteLine("");
            var standing = run.Standing.SetAside ? $", STALLED ({run.LoggedStalls} logged): reported apart"
                : run.Standing.MakesAnother ? ", a gap with no stall logged: JUDGED" : "";
            output.WriteLine($"{run.Arm}: {run.Ser.Fps:F1} fps, {run.Ser.Fps / (baseline ?? double.NaN):P1} of {against}{standing}");
            output.WriteLine($"  {run.Ser}");
            output.WriteLine($"  replaced {(run.Replaced is { } replaced ? replaced.ToString(CultureInfo.InvariantCulture) : "?")}"
                + $"{(run.Arm.StartsWith("node", StringComparison.Ordinal) ? " (the node's whole stream, from its log)" : "")}, "
                + $"body dropped {run.BodyDropped} and disk dropped {run.DiskDropped} in the recording");
            if (run.View is { } view)
            {
                output.WriteLine($"  {view}");
            }
            if (run.Answers is { } answers)
            {
                output.WriteLine($"  {answers}");
            }
            if (run.GainsReached is { } gainsReached)
            {
                output.WriteLine($"  {gainsReached}");
            }
            if (run.Stream is { } streamCounts)
            {
                output.WriteLine($"  the node's stream: {streamCounts}");
            }
            output.WriteLine($"  {run.Load}");
            output.WriteLine($"  before it: {run.Quiet}");
        }
        output.WriteLine("");
        output.WriteLine($"TCP listeners and UDP endpoints of the node, its keeper and this client (pids {string.Join(", ", watched)}), sampled every "
            + $"{EndpointSampleEvery.TotalSeconds:F0} s through the node part:");
        foreach (var endpoint in endpoints.OrderBy(static e => e.Pid).ThenBy(static e => e.Protocol).ThenBy(static e => e.Port))
        {
            output.WriteLine($"  {endpoint}");
        }
        if (endpoints.Count == 0)
        {
            output.WriteLine("  (none: the node listens on its socket alone)");
        }
        // The node's own lines, with their times: when each stream started, stalled and came back (its log goes with its data root).
        output.WriteLine("");
        output.WriteLine("The node's camera streams, from its log:");
        foreach (var line in streamLines)
        {
            output.WriteLine($"  {line}");
        }

        endpoints.ShouldAllBe(static e => e.IsLoopback, "no socket off loopback (TIANWEN_LOCAL_ONLY): nothing the firewall would ask about");
        baseline.ShouldNotBeNull("a same-session in-process recording with no stall to compare with: the comparison is the gate's point");
        // Every recording with the view whose stream logged no stall is judged, a gap in it included.
        var gated = runs.Where(static r => r.Arm.StartsWith("node, view", StringComparison.Ordinal) && !r.Standing.SetAside).ToArray();
        gated.Length.ShouldBeGreaterThanOrEqualTo(wanted, "enough recordings with the view whose stream logged no stall");
        foreach (var run in gated)
        {
            var failures = RateGate.Failures(run.Ser, run.Replaced, run.BodyDropped, run.DiskDropped, baseline);
            failures.ShouldBeEmpty($"{run.Arm}: {string.Join("; ", failures)}");
        }
    }

    /// <summary>Set to 1 (beside <see cref="ToupTekFrameRateProbe.GateVar"/>) for the real GUI's evidence run, which a person drives.</summary>
    private const string GuiEvidenceVar = "TIANWEN_TOUPTEK_GUI_EVIDENCE";

    /// <summary>The <c>tianwen-gui</c> executable the evidence run opens.</summary>
    private const string GuiUnderTestVar = "TIANWEN_GUI_UNDER_TEST";

    /// <summary>Where the evidence run writes its PNG.</summary>
    private const string GuiEvidenceOutVar = "TIANWEN_GUI_EVIDENCE_OUT";

    private static readonly TimeSpan ClicksRepeatEvery = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Evidence, not a gate: the REAL GUI window, local-only, on its own node (as the gate's), placed on the left half of the
    /// screen, its planetary mode showing the ToupTek live while the node records a minute; the window captured by
    /// <c>PrintWindow</c> by its pid to a PNG, and the SER's rate read back. The gate does not cover the renderer's draw cost;
    /// this run does. A person makes the clicks the GUI has no other way to take (the Live Session tab, the Planetary mode
    /// pill, the panel's settings and Start, then the Record control's Record, #1344), which this prints; only the jog to the
    /// Saturn window is sent from here, between the two. The picture is taken halfway through the recording, so it shows the
    /// Record control while it records. <c>tools/planetary-gui-evidence.ps1</c> builds and runs it.
    /// </summary>
    [Fact(Timeout = 1_800_000)]
    public async Task TheRealGuiWindowShowsTheCaptureLiveWhileTheNodeRecords()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable(GateVar) == "1", $"{GateVar} is not 1 (this drives the attached ToupTek camera)");
        // "check" runs everything a person is not needed for (the node, the window placed, the clicks printed, one picture of the
        // window, its endpoints) and stops there, so the launcher can be proven before someone is at the desk.
        var mode = Environment.GetEnvironmentVariable(GuiEvidenceVar);
        Assert.SkipUnless(mode is "1" or "check", $"{GuiEvidenceVar} is not 1 or check (a person drives this one: tools/planetary-gui-evidence.ps1)");
        var checkOnly = mode == "check";
        var guiPath = Environment.GetEnvironmentVariable(GuiUnderTestVar) is { Length: > 0 } named && File.Exists(named)
            ? named : throw new InvalidOperationException($"{GuiUnderTestVar} names no tianwen-gui executable");
        var evidence = Directory.CreateDirectory(Environment.GetEnvironmentVariable(GuiEvidenceOutVar) is { Length: > 0 } folder
            ? folder : throw new InvalidOperationException($"{GuiEvidenceOutVar} names no folder for the PNG")).FullName;
        var ct = TestContext.Current.CancellationToken;
        var device = new ToupTekDeviceSource().RegisteredDevices(DeviceType.Camera).FirstOrDefault();
        Assert.SkipWhen(device is null, "no ToupTek camera attached");
        var camera = device.DeviceUri;
        var duration = TimeSpan.FromSeconds(IntFromEnvironment("TIANWEN_TOUPTEK_RECORD_SECONDS", 60));

        // Where a 640 x 480 stream starts (centred), read off the camera in this process before the node holds it.
        RoiRect centred;
        {
            var (probeCamera, _, services) = await ToupTekFrameRateProbe.ConnectAsync(output, LogLevel.Warning, device);
            await using (services)
            {
                try
                {
                    var constraints = probeCamera.RoiConstraints;
                    centred = constraints.Snap(RoiRect.Centered(constraints.MaxWidth, constraints.MaxHeight, RoiWidth, RoiHeight));
                }
                finally
                {
                    await probeCamera.DisconnectAsync(ct);
                }
            }
        }

        var endpoints = new HashSet<OwnedEndpoints.Endpoint>();
        string[] streamLines;
        Process? gui = null;
        SerRateReadBack ser;
        int bodyDropped, diskDropped;
        string load, quiet, placed, png;
        TimeSpan recorded;
        var kept = await KeptNode.StartAsync(ct, dataRoot => SeedProfileAsync(dataRoot, camera, ct), fakeDevicesOnly: false);
        try
        {
            var client = kept.Client;
            var nodePid = (await client.GetNodeAsync(ct)).Value.ShouldNotBeNull("the node answers").ProcessId;
            // Active BEFORE the camera connects: a window that attaches to a node running no profile makes one active, and
            // a node with a device connected refuses the switch ("Disconnect equipment first").
            (await client.SetActiveProfileAsync(ProfileId, ct)).IsSuccess.ShouldBeTrue("the node runs the probe's profile");
            await NodeWait.UntilTheJobSucceedsAsync(client, await client.ConnectDeviceAsync(camera, ct), ct);

            var start = new ProcessStartInfo(guiPath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(guiPath),
            };
            start.Environment[TianWenDataRoot.EnvironmentVariable] = kept.DataRoot;
            start.Environment[TianWen.Hosting.Api.NodeSocket.EnvironmentVariable] = kept.SocketPath;
            start.Environment["TIANWEN_LOCAL_ONLY"] = "1";
            start.Environment["TIANWEN_GUI_SINGLE_INSTANCE"] = "0";
            gui = Process.Start(start) ?? throw new InvalidOperationException($"{guiPath} did not start");
            // What the window says on its stdout and stderr shows here, in the launcher's own window.
            gui.OutputDataReceived += (_, line) => Echo("gui", line.Data);
            gui.ErrorDataReceived += (_, line) => Echo("gui stderr", line.Data);
            gui.BeginOutputReadLine();
            gui.BeginErrorReadLine();
            int[] watched = [Environment.ProcessId, kept.Keeper.Id, nodePid, gui.Id];
            output.WriteLine($"tianwen-gui pid {gui.Id} on node pid {nodePid} (keeper {kept.Keeper.Id}), local-only, data root {kept.DataRoot}");

            var window = await GuiWindow.PlaceOnLeftHalfAsync(gui, ct) ?? throw new InvalidOperationException($"the GUI exited ({gui.ExitCode}) without showing a window");
            placed = window.Placed;
            output.WriteLine($"the GUI's window is {placed}");

            if (checkOnly)
            {
                // The window taken at 5, 15 and 30 s, so a first frame that comes late is told from one PrintWindow cannot
                // see; its endpoints sampled meanwhile.
                for (var second = 1; second <= 30; second++)
                {
                    gui.HasExited.ShouldBeFalse("the GUI is still open");
                    OwnedEndpoints.Sample(watched, endpoints);
                    await SystemTimeProvider.Instance.SleepAsync(TimeSpan.FromSeconds(1), ct);
                    if (second is 5 or 15 or 30)
                    {
                        var check = Path.Combine(evidence, $"gui-check-{second:00}s-{DateTime.Now:yyyyMMdd-HHmmss}.png");
                        var size = GuiWindow.PrintToPng(window.Window, gui.Id, check);
                        output.WriteLine($"CHECK ONLY: the window at {second} s ({size.Width}x{size.Height}) by PrintWindow: {check}");
                    }
                }
                var attached = (await client.GetNodeAsync(ct)).Value?.ClientsAttached ?? 0;
                output.WriteLine($"CHECK ONLY: {attached} client(s) attached to the node");
                output.WriteLine("  TCP listeners and UDP endpoints of the node, its keeper, the GUI and this process:");
                foreach (var endpoint in endpoints.OrderBy(static e => e.Pid).ThenBy(static e => e.Protocol).ThenBy(static e => e.Port))
                {
                    output.WriteLine($"    {endpoint}");
                }
                attached.ShouldBeGreaterThanOrEqualTo(1, "the GUI attached to the probe's node");
                endpoints.ShouldAllBe(static e => e.IsLoopback, "no socket off loopback (TIANWEN_LOCAL_ONLY)");
                return;
            }

            // The person's part: until the node streams the Saturn window's size and depth, started from the GUI.
            TimeSpan? asked = null;
            var clock = Stopwatch.StartNew();
            PlanetaryStateDto? running = null;
            while (running is null)
            {
                gui.HasExited.ShouldBeFalse("the GUI is still open");
                OwnedEndpoints.Sample(watched, endpoints);
                var state = (await client.GetPlanetaryAsync(ct)).Value;
                if (state is { Running: true, FramesReceived: > 0 } on && (on.RoiWidth, on.RoiHeight, on.BitDepth) == (RoiWidth, RoiHeight, BitDepth.Int8))
                {
                    running = on;
                }
                else if (ClickPrompt.Due(asked, clock.Elapsed, ClicksRepeatEvery))
                {
                    asked = clock.Elapsed;
                    output.WriteLine("");
                    output.WriteLine("PLEASE, in the TianWen GUI window on the LEFT half of the screen:");
                    output.WriteLine("  1. open the Live Session tab;");
                    output.WriteLine("  2. pick Planetary on its mode pill;");
                    output.WriteLine($"  3. in the planetary panel set Exp {SaturnExposure.TotalMilliseconds} ms, Gain {SaturnGain}, ROI {RoiWidth}x{RoiHeight}, depth 8 bit;");
                    output.WriteLine("  4. press Start. The jog to the Saturn window is sent from here; then this asks for Record.");
                    if (state is { Running: true } wrong)
                    {
                        output.WriteLine($"  The capture runs at {wrong.RoiWidth}x{wrong.RoiHeight}, {wrong.BitDepth}: press Stop, set {RoiWidth}x{RoiHeight} and 8 bit, and Start again.");
                    }
                }
                await SystemTimeProvider.Instance.SleepAsync(TimeSpan.FromSeconds(1), ct);
            }
            output.WriteLine($"the GUI started the capture: {running.RoiWidth}x{running.RoiHeight}, {running.BitDepth}; "
                + $"setting {SaturnExposure.TotalMilliseconds} ms and gain {SaturnGain} and jogging to ({RoiX}, {RoiY})");

            // The panel's own values are what the person set; these make sure of them, and the jog moves the window there.
            (await client.SetPlanetaryControlsAsync(new PlanetaryControlsDto
            {
                ExposureMs = SaturnExposure.TotalMilliseconds,
                Gain = SaturnGain,
                JogX = RoiX - centred.X,
                JogY = RoiY - centred.Y,
            }, ct)).IsSuccess.ShouldBeTrue();
            await SystemTimeProvider.Instance.SleepAsync(PanAndWarm, ct);

            quiet = await OtherWorktreeBuilds.WaitUntilQuietAsync(ct);

            // The person's second part: the Record control's Record (#1344), the control this picture is of. Until the node records.
            asked = null;
            PlanetaryRecordingDto? started = null;
            var droppedFrom = 0;
            while (started is null)
            {
                gui.HasExited.ShouldBeFalse("the GUI is still open");
                OwnedEndpoints.Sample(watched, endpoints);
                var state = (await client.GetPlanetaryAsync(ct)).Value;
                if (state is { Recording: { Recording: true } going })
                {
                    started = going;
                    droppedFrom = state.DroppedFrames;
                }
                else if (ClickPrompt.Due(asked, clock.Elapsed, ClicksRepeatEvery))
                {
                    asked = clock.Elapsed;
                    output.WriteLine("");
                    output.WriteLine("PLEASE, in the same window's planetary panel, under RECORD:");
                    output.WriteLine($"  5. make Secs say {duration.TotalSeconds:F0} (it says 60 until typed over) and press Record. Nothing else: "
                        + "the window's picture is taken from here halfway through the recording.");
                }
                await SystemTimeProvider.Instance.SleepAsync(TimeSpan.FromMilliseconds(250), ct);
            }
            var cpu = HostCpuLoad.Start();
            recorded = started.EndsUtc - started.StartedUtc;
            output.WriteLine($"the GUI's Record started {recorded.TotalSeconds:F0} s to {started.Path}");
            png = Path.Combine(evidence, $"gui-planetary-live-{DateTime.Now:yyyyMMdd-HHmmss}.png");
            var shot = (Width: 0, Height: 0);
            int? droppedTo = null;
            PlanetaryRecordingDto? written = null;
            var recordClock = Stopwatch.StartNew();
            while (written is null)
            {
                OwnedEndpoints.Sample(watched, endpoints);
                if (shot.Width == 0 && recordClock.Elapsed >= recorded / 2)
                {
                    shot = GuiWindow.PrintToPng(window.Window, gui.Id, png);
                    output.WriteLine($"the window, mid-recording, by PrintWindow: {png} ({shot.Width}x{shot.Height})");
                }
                var now = (await client.GetPlanetaryAsync(ct)).Value;
                if (now?.Recording is { Recording: false } ended)
                {
                    droppedTo ??= now.DroppedFrames;
                    written = ended.Written ? ended : null;
                }
                await SystemTimeProvider.Instance.SleepAsync(TimeSpan.FromMilliseconds(500), ct);
            }
            load = cpu.Describe();
            written.FailureReason.ShouldBeNull();
            bodyDropped = (droppedTo ?? droppedFrom) - droppedFrom;
            diskDropped = written.FramesDropped;
            ser = ReadBackAndDelete(written);
            (await client.StopPlanetaryAsync(ct)).IsSuccess.ShouldBeTrue("the capture stops");
        }
        finally
        {
            // Everything started here stops by its pid: the window first, then the camera and the node.
            if (gui is { HasExited: false })
            {
                gui.Kill();
                await gui.WaitForExitAsync(CancellationToken.None);
            }
            gui?.Dispose();
            try
            {
                await NodeWait.UntilTheJobSucceedsAsync(kept.Client, await kept.Client.DisconnectDeviceAsync(camera, skipWarmUp: true, ct), ct);
                await kept.Client.ShutdownNodeAsync(ct);
                await kept.KeeperExitAsync(ct);
            }
            finally
            {
                streamLines = kept.Keeper.HasExited ? StreamLines(kept.DataRoot) : [];
                // The window's and the node's own logs go with the data root: kept beside the picture.
                foreach (var day in Directory.EnumerateDirectories(Path.Combine(kept.DataRoot, "Logs")))
                {
                    foreach (var file in Directory.EnumerateFiles(day, "*.log"))
                    {
                        File.Copy(file, Path.Combine(evidence, Path.GetFileName(file)), overwrite: true);
                    }
                }
                await kept.DisposeAsync();
            }
        }

        output.WriteLine("");
        output.WriteLine($"The real GUI on the node, at the Saturn window ({RoiWidth}x{RoiHeight} at {RoiX}, {RoiY}), 8 bits, {SaturnExposure.TotalMilliseconds} ms, "
            + $"gain {SaturnGain}, {recorded.TotalSeconds:F0} s by the GUI's Record: {ser.Fps:F1} fps off the SER, which the gate's bar ({RateGate.Bar}) would {(ser.Fps >= RateGate.Bar ? "pass" : "MISS")}");
        output.WriteLine($"  {ser}");
        output.WriteLine($"  body dropped {bodyDropped} and disk dropped {diskDropped} in the recording");
        output.WriteLine($"  {load}");
        output.WriteLine($"  before it: {quiet}");
        output.WriteLine($"  the window was {placed}; its picture: {png}");
        output.WriteLine("  the node's streams, from its log:");
        foreach (var line in streamLines)
        {
            output.WriteLine($"    {line}");
        }
        output.WriteLine("  TCP listeners and UDP endpoints of the node, its keeper, the GUI and this process:");
        foreach (var endpoint in endpoints.OrderBy(static e => e.Pid).ThenBy(static e => e.Protocol).ThenBy(static e => e.Port))
        {
            output.WriteLine($"    {endpoint}");
        }
        endpoints.ShouldAllBe(static e => e.IsLoopback, "no socket off loopback (TIANWEN_LOCAL_ONLY): nothing the firewall would ask about");
    }

    private void Echo(string what, string? line)
    {
        if (line is null)
        {
            return;
        }
        try
        {
            output.WriteLine($"{what}: {line}");
        }
        catch (InvalidOperationException)
        {
            // The test has ended; the window's last words go nowhere.
        }
    }

    /// <summary>The node's camera stream lines (starts, recoveries, ends) from every server log under its data root.</summary>
    private static string[] StreamLines(string dataRoot)
        => [.. ServerLogLines(dataRoot).Select(static entry => entry.Line).Where(static line => line.Contains("streaming video", StringComparison.Ordinal)
            || line.Contains("stream is back", StringComparison.Ordinal) || StreamEnd.IsMatch(line))];

    /// <summary>Every line of the node's logs under its data root, with the day its folder is named for (yyyyMMdd), which a line's own time leaves out.</summary>
    private static IEnumerable<(string Day, string Line)> ServerLogLines(string dataRoot)
    {
        foreach (var day in Directory.EnumerateDirectories(Path.Combine(dataRoot, "Logs")).Order(StringComparer.Ordinal))
        {
            foreach (var file in Directory.EnumerateFiles(day, "Server_*.log").Order(StringComparer.Ordinal))
            {
                foreach (var line in File.ReadLines(file))
                {
                    yield return (Path.GetFileName(day), line);
                }
            }
        }
    }

    private static readonly Regex LineTime = new Regex(@"^\[(?<time>\d\d:\d\d:\d\d\.\d{3}) (?<zone>[+-]\d\d:\d\d)\]", RegexOptions.CultureInvariant);

    /// <summary>When a node log line was written, from its own time and its folder's day; null for a line without one (an exception's).</summary>
    private static DateTimeOffset? WrittenAt(string day, string line)
        => LineTime.Match(line) is { Success: true } time
           && DateTimeOffset.TryParseExact($"{day} {time.Groups["time"].Value} {time.Groups["zone"].Value}", "yyyyMMdd HH:mm:ss.fff zzz",
               CultureInfo.InvariantCulture, DateTimeStyles.None, out var at)
            ? at
            : null;

    /// <summary>
    /// When each gain the GUI's client staged reached the camera: the DAL's own line once the body has a staged gain, the first
    /// after it was asked for, and the time between; and the longest of them, null when one never arrived.
    /// </summary>
    private static (string Said, TimeSpan? Longest) GainsReached(IReadOnlyList<(DateTimeOffset Asked, int Gain)> asked, IReadOnlyList<(DateTimeOffset At, int Gain)> reached)
    {
        var said = new List<string>();
        TimeSpan? longest = TimeSpan.Zero;
        foreach (var (at, gain) in asked)
        {
            if (reached.Where(r => r.At >= at).OrderBy(static r => r.At).FirstOrDefault() is { At: var on } got && on != default)
            {
                var took = on - at;
                longest = longest is { } so ? (took > so ? took : so) : null;
                said.Add($"gain {gain} staged at {at:HH:mm:ss.fff} was on the camera ({got.Gain}) {took.TotalMilliseconds:F0} ms later");
            }
            else
            {
                longest = null;
                said.Add($"gain {gain} staged at {at:HH:mm:ss.fff} NEVER reached the camera");
            }
        }
        return (string.Join("; ", said), longest);
    }

    /// <summary>
    /// The endpoint tables are read right: a loopback TCP listener and UDP socket this process opens, on IPv4 and IPv6, are
    /// found, so an empty listing of the node's means it bound nothing.
    /// </summary>
    private static void SamplerSeesThisProcesssOwnSockets()
    {
        using var tcp4 = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        using var tcp6 = new System.Net.Sockets.TcpListener(System.Net.IPAddress.IPv6Loopback, 0);
        tcp4.Start();
        tcp6.Start();
        using var udp4 = new System.Net.Sockets.UdpClient(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 0));
        using var udp6 = new System.Net.Sockets.UdpClient(new System.Net.IPEndPoint(System.Net.IPAddress.IPv6Loopback, 0));
        var seen = new HashSet<OwnedEndpoints.Endpoint>();
        OwnedEndpoints.Sample([Environment.ProcessId], seen);
        foreach (var (protocol, local) in new (string, System.Net.EndPoint?)[]
        {
            ("TCP listen", tcp4.LocalEndpoint), ("TCP listen", tcp6.LocalEndpoint), ("UDP", udp4.Client.LocalEndPoint), ("UDP", udp6.Client.LocalEndPoint),
        })
        {
            var at = local.ShouldBeOfType<System.Net.IPEndPoint>();
            seen.ShouldContain(new OwnedEndpoints.Endpoint(Environment.ProcessId, protocol, at.Address, at.Port), $"the sampler finds {protocol} {at}");
        }
        tcp4.Stop();
        tcp6.Stop();
    }

    /// <summary>#1596's arm: a recording through <see cref="Imaging.Planetary.PlanetaryCapture"/> in this process, and the window a stream starts at.</summary>
    private async Task<(Run Run, RoiRect Centred)> InProcessAsync(string arm, ToupTekDevice device, TimeSpan duration, CancellationToken ct)
    {
        var quiet = await OtherWorktreeBuilds.WaitUntilQuietAsync(ct);
        var (camera, external, services) = await ToupTekFrameRateProbe.ConnectAsync(output, LogLevel.Information, device);
        await using var _ = services;
        try
        {
            var constraints = camera.RoiConstraints;
            var centred = constraints.Snap(RoiRect.Centered(constraints.MaxWidth, constraints.MaxHeight, RoiWidth, RoiHeight));
            var path = Path.Combine(external.ImageOutputFolder.FullName, "in-process.ser");
            var load = HostCpuLoad.Start();
            var result = await CaptureAsync(camera, SaturnOptions, (path, duration), external.AppLogger, ct);
            var described = load.Describe();
            File.Delete(path);
            var ser = result.Ser.ShouldNotBeNull("a recording reads back");
            return (new Run(arm, ser, duration, result.BodyDropped, result.DiskDropped, described, quiet, Replaced: result.Replaced,
                LoggedStalls: result.Stalls, Stream: result.Detail), centred);
        }
        finally
        {
            await camera.DisconnectAsync(ct);
        }
    }

    /// <summary>A baseline: the node records with no live view attached, started by a client that opens no frame stream.</summary>
    private static async Task<Run> NoViewAsync(KeptNode kept, Pump pump, (int X, int Y) jog, TimeSpan duration, CancellationToken ct)
    {
        var quiet = await OtherWorktreeBuilds.WaitUntilQuietAsync(ct);
        var client = kept.Client;
        var started = await client.StartPlanetaryAsync(Request, ct);
        started.IsSuccess.ShouldBeTrue(started.Error);
        await pump.UntilAsync(async token => (await client.GetPlanetaryAsync(token)).Value is { FramesReceived: > 0 }, ct);
        (await client.SetPlanetaryControlsAsync(new PlanetaryControlsDto { JogX = jog.X, JogY = jog.Y }, ct)).IsSuccess.ShouldBeTrue();
        await pump.ForAsync(PanAndWarm, ct);

        var load = HostCpuLoad.Start();
        var (recording, bodyDropped, answers) = await RecordAsync(client, pump, duration, ct);
        var described = load.Describe();
        (await client.StopPlanetaryAsync(ct)).IsSuccess.ShouldBeTrue("the capture stops");
        return new Run("node, no view", ReadBackAndDelete(recording), duration, bodyDropped, recording.FramesDropped, described, quiet,
            Answers: answers.ToString());
    }

    /// <summary>
    /// A gated recording: the capture started by the panel's signal, the GUI's planetary view on RAW reading the camera's live
    /// frame through shared memory, and the recording started by the Record control's signal (#1344).
    /// </summary>
    private static async Task<Run> WithViewAsync(string arm, GuiNodeHarness.GuiClient gui, LocalNodeConnection local, PlanetaryCaptureController planetary,
        Pump pump, (int X, int Y) jog, TimeSpan duration, CancellationToken ct)
    {
        var quiet = await OtherWorktreeBuilds.WaitUntilQuietAsync(ct);
        planetary.ViewerState.ShowStacked = false;
        var noted = gui.AppState.Notifications.Length;
        gui.Bus.Post(new StartVideoCaptureSignal(OtaIndex: 0, ExposureMs: SaturnExposure.TotalMilliseconds, Gain: SaturnGain,
            RoiWidth: RoiWidth, RoiHeight: RoiHeight, BitDepth: BitDepth.Int8));
        // Dispatched now, so the start's tracked work exists before the drain is taken.
        gui.Bus.ProcessPending(gui.Tracker);
        // A refused start is a notification, and the planner, with no mount and so no site transform here, posts its own:
        // the capture running is the answer, the notifications since the post the words when it is not.
        // Bounded by the test's token alone: until the start's tracked work has finished and the capture either streams or is not
        // going at all (refused, or ended at once).
        var started = gui.Tracker.DrainAsync();
        await pump.UntilAsync(_ => ValueTask.FromResult(started.IsCompleted && (!planetary.IsCapturing || planetary.Capture.FramesReceived > 0)), ct);
        planetary.IsCapturing.ShouldBeTrue(string.Join("; ", gui.AppState.Notifications.Skip(noted).Select(static n => $"{n.Severity}: {n.Message}")));
        planetary.Capture.JogRoi(jog.X, jog.Y);
        await pump.ForAsync(PanAndWarm, ct);
        var shown = planetary.Source.ShouldBeOfType<LiveFramePreviewSource>("RAW shows the camera's live frame");

        var (shownFrom, readFrom, slotsFrom, mastersFrom) = (shown.FrameCount, planetary.LiveFramesRead, planetary.LiveFramesFromSharedMemory,
            (await local.Client.GetPlanetaryAsync(ct)).Value?.Masters ?? 0);
        var load = HostCpuLoad.Start();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        // A live control staged through the GUI's own capture client while it records, as the panel's Gain field stages one: one up a
        // third of the way in, back two thirds in. When each reached the camera is read from the node's log once it has exited.
        var gainsAsked = new List<(DateTimeOffset Asked, int Gain)>();
        var (recording, bodyDropped, answers) = await RecordBySignalAsync(gui, planetary, local.Client, pump, duration, ct, elapsed =>
        {
            var next = gainsAsked.Count switch
            {
                0 when elapsed >= duration / 3 => SaturnGain + 1,
                1 when elapsed >= duration * 2 / 3 => SaturnGain,
                _ => -1,
            };
            if (next >= 0)
            {
                gainsAsked.Add((DateTimeOffset.Now, next));
                planetary.Capture.SetGain(next);
            }
        });
        var seconds = clock.Elapsed.TotalSeconds;
        var described = load.Describe();
        var (shownTo, readTo, slotsTo, mastersTo) = (shown.FrameCount, planetary.LiveFramesRead, planetary.LiveFramesFromSharedMemory,
            (await local.Client.GetPlanetaryAsync(ct)).Value?.Masters ?? 0);

        gui.Bus.Post(new StopVideoCaptureSignal());
        await pump.UntilAsync(async token => !planetary.IsCapturing && (await local.Client.GetPlanetaryAsync(token)).Value is not { Running: true }, ct);

        (readTo - readFrom).ShouldBeGreaterThan(0, "the view read the live frame through the recording");
        (slotsTo - slotsFrom).ShouldBe(readTo - readFrom, "every live frame the view read came through shared memory");
        var view = $"the view: {(shownTo - shownFrom) / seconds:F1} live frames a second shown ({shownTo - shownFrom}), "
            + $"{(readTo - readFrom) / seconds:F1} read ({readTo - readFrom}, {slotsTo - slotsFrom} through shared memory; the node samples at most one "
            + $"each 33 ms, drop-to-latest), {mastersTo - mastersFrom} masters stacked by the node";
        return new Run(arm, ReadBackAndDelete(recording), duration, bodyDropped, recording.FramesDropped, described, quiet, view,
            Answers: answers.ToString(), GainsAsked: gainsAsked);
    }

    /// <summary>
    /// Records the capture going on for <paramref name="duration"/> as the Record control's button does, by posting
    /// <see cref="StartPlanetaryRecordingSignal"/> (#1344), and waits until its file is whole on the recording's own state as the
    /// panel reads it (<see cref="NodePlanetaryCapture.Recording"/>). The tracker is drained only for the request: the handler
    /// tracks the request alone, so the drain ends with the node's answer, never with the recording
    /// (<c>PlanetaryRecordingThroughTheNodeTests</c> pins it), and a refused record ends it too, with its words. Meanwhile the
    /// node's state is asked for on <paramref name="client"/> every 100 ms and each answer timed, as <see cref="RecordAsync"/> does,
    /// and <paramref name="whileRecording"/> is called at each ask with the time the recording has run.
    /// </summary>
    private static async Task<(PlanetaryRecordingDto Recording, int BodyDropped, AnswerTimes Answers)> RecordBySignalAsync(GuiNodeHarness.GuiClient gui,
        PlanetaryCaptureController planetary, TianWen.RemoteClient.TianWenNodeClient client, Pump pump, TimeSpan duration, CancellationToken ct,
        Action<TimeSpan>? whileRecording = null)
    {
        var noted = gui.AppState.Notifications.Length;
        gui.Bus.Post(new StartPlanetaryRecordingSignal(duration));
        // Dispatched now, so the record's tracked work exists before the drain is taken.
        gui.Bus.ProcessPending(gui.Tracker);
        var asked = gui.Tracker.DrainAsync();
        await pump.UntilAsync(_ => ValueTask.FromResult(asked.IsCompleted), ct);
        var said = string.Join("; ", gui.AppState.Notifications.Skip(noted).Select(static n => $"{n.Severity}: {n.Message}"));
        var started = planetary.Capture.Recording.ShouldNotBeNull(said);
        started.Recording.ShouldBeTrue(said);
        (started.EndsUtc - started.StartedUtc).ShouldBe(duration, "the node records for the duration the signal asked");
        var droppedFrom = planetary.Capture.DroppedFrames;
        var clock = Stopwatch.StartNew();
        var answers = new AnswerTimes();

        int? droppedTo = null;
        await pump.UntilAsync(async token =>
        {
            var asked = Stopwatch.GetTimestamp();
            if ((await client.GetPlanetaryAsync(token)).Value is { Recording: { Recording: true } })
            {
                answers.Add(Stopwatch.GetElapsedTime(asked));
                whileRecording?.Invoke(clock.Elapsed);
            }
            if (planetary.Capture.Recording is { Recording: false })
            {
                droppedTo ??= planetary.Capture.DroppedFrames;
            }
            return planetary.Capture.Recording is { Written: true };
        }, ct);
        var recording = planetary.Capture.Recording.ShouldNotBeNull();
        recording.Path.ShouldBe(started.Path, "the recording the signal started");
        recording.FailureReason.ShouldBeNull();
        return (recording, (droppedTo ?? droppedFrom) - droppedFrom, answers);
    }

    /// <summary>
    /// Records the capture going on for <paramref name="duration"/> on a client of the node's own, and waits until its file is
    /// whole. The node's state is asked
    /// for every 100 ms meanwhile, as a client polls it, and each answer's time is kept (TIA2's capture-priority leg: the node
    /// must answer while the box is loaded); <paramref name="whileRecording"/>, when given, is called at each ask with the time
    /// the recording has run.
    /// </summary>
    private static async Task<(PlanetaryRecordingDto Recording, int BodyDropped, AnswerTimes Answers)> RecordAsync(TianWen.RemoteClient.TianWenNodeClient client,
        Pump pump, TimeSpan duration, CancellationToken ct, Action<TimeSpan>? whileRecording = null)
    {
        var started = await client.StartPlanetaryRecordingAsync(new PlanetaryRecordRequestDto { DurationSeconds = duration.TotalSeconds }, ct);
        var state = started.Value.ShouldNotBeNull(started.Error);
        var clock = Stopwatch.StartNew();
        var answers = new AnswerTimes();
        var droppedFrom = state.DroppedFrames;
        int? droppedTo = null;
        PlanetaryRecordingDto? written = null;
        await pump.UntilAsync(async token =>
        {
            var asked = Stopwatch.GetTimestamp();
            var now = (await client.GetPlanetaryAsync(token)).Value;
            if (now?.Recording is { Recording: true })
            {
                answers.Add(Stopwatch.GetElapsedTime(asked));
                whileRecording?.Invoke(clock.Elapsed);
            }
            if (now?.Recording is { Recording: false } ended)
            {
                droppedTo ??= now.DroppedFrames;
                written = ended.Written ? ended : null;
            }
            return written is not null;
        }, ct);
        var recording = written.ShouldNotBeNull();
        recording.FailureReason.ShouldBeNull();
        return (recording, (droppedTo ?? droppedFrom) - droppedFrom, answers);
    }

    /// <summary>How long the node took to answer <c>GET /api/v1/planetary</c>, from this client, each time it was asked during a recording.</summary>
    private sealed class AnswerTimes
    {
        private readonly List<double> _ms = [];

        public void Add(TimeSpan answeredIn) => _ms.Add(answeredIn.TotalMilliseconds);

        public override string ToString()
        {
            if (_ms.Count == 0)
            {
                return "the node's state was never asked for while it recorded";
            }
            double[] sorted = [.. _ms.Order()];
            double At(double p) => sorted[(int)Math.Round(p * (sorted.Length - 1))];
            return $"the node answered GET /api/v1/planetary {sorted.Length} times while it recorded, p50 {At(0.5):F1} / p99 {At(0.99):F1} / max {sorted[^1]:F1} ms";
        }
    }

    private static SerRateReadBack ReadBackAndDelete(PlanetaryRecordingDto recording)
    {
        var ser = SerRateReadBack.Read(recording.Path, LongStepBound);
        File.Delete(recording.Path);
        return ser;
    }

    private static Task SeedProfileAsync(string dataRoot, Uri camera, CancellationToken ct)
    {
        var profiles = Directory.CreateDirectory(Path.Combine(dataRoot, "Profiles")).FullName;
        var data = new ProfileData(
            Mount: NoneDevice.Instance.DeviceUri,
            Guider: NoneDevice.Instance.DeviceUri,
            OTAs: [new OTAData("Saturn scope", 2000, Camera: camera, Cover: null, Focuser: null, FilterWheel: null,
                PreferOutwardFocus: null, OutwardIsPositive: null)],
            SiteLatitude: 48.2,
            SiteLongitude: 16.3);
        return File.WriteAllTextAsync(Path.Combine(profiles, Profile.DeviceIdFromUUID(ProfileId) + ".json"),
            JsonSerializer.Serialize(new ProfileDto(ProfileId, "The ToupTek at the Saturn window", data), Profile.ProfileJsonSerializerContextIndented.ProfileDto), ct);
    }

    private static readonly Regex StreamStart = new Regex(@"streaming video: (?<w>\d+)x(?<h>\d+) at \((?<x>\d+), (?<y>\d+)\)", RegexOptions.CultureInvariant);
    private static readonly Regex StreamEnd = new Regex(@"(?<counts>streamed \d+ frames in .* were dropped by the full hand-off .* (?<stalls>\d+) stalls, (?<recovered>\d+) recovered[^\r\n]*)",
        RegexOptions.CultureInvariant);
    private static readonly Regex Panned = new Regex(@"stream window is (?<w>\d+)x(?<h>\d+) at \((?<x>\d+), (?<y>\d+)\) after a pan", RegexOptions.CultureInvariant);
    private static readonly Regex ReplacedCount =new Regex(@"the reader took \d+, (?<replaced>\d+) were dropped", RegexOptions.CultureInvariant);
    private static readonly Regex Recovery = new Regex(@"stream is back after its stall .*", RegexOptions.CultureInvariant);
    private static readonly Regex GainOnCamera = new Regex(@"stream is at gain (?<gain>\d+)", RegexOptions.CultureInvariant);

    /// <summary>The node's camera streams, in order, from every server log under its data root.</summary>
    private static List<NodeStream> ReadStreams(string dataRoot)
    {
        var streams = new List<NodeStream>();
        (int X, int Y)? start = null;
        var recoveries = new List<string>();
        var gains = new List<(DateTimeOffset At, int Gain)>();
        var (width, height) = (0, 0);
        RoiRect? panned = null;
        foreach (var (day, line) in ServerLogLines(dataRoot))
        {
            if (StreamStart.Match(line) is { Success: true } started)
            {
                start = (int.Parse(started.Groups["x"].Value, CultureInfo.InvariantCulture), int.Parse(started.Groups["y"].Value, CultureInfo.InvariantCulture));
                (width, height) = (int.Parse(started.Groups["w"].Value, CultureInfo.InvariantCulture), int.Parse(started.Groups["h"].Value, CultureInfo.InvariantCulture));
                panned = null;
                recoveries.Clear();
                gains.Clear();
            }
            else if (GainOnCamera.Match(line) is { Success: true } gain && start is not null && WrittenAt(day, line) is { } gainAt)
            {
                gains.Add((gainAt, int.Parse(gain.Groups["gain"].Value, CultureInfo.InvariantCulture)));
            }
            else if (Panned.Match(line) is { Success: true } pan && start is not null)
            {
                // The DAL's own line once the body has the window a pan left: the last one is where the stream recorded.
                panned = new RoiRect(int.Parse(pan.Groups["x"].Value, CultureInfo.InvariantCulture), int.Parse(pan.Groups["y"].Value, CultureInfo.InvariantCulture),
                    int.Parse(pan.Groups["w"].Value, CultureInfo.InvariantCulture), int.Parse(pan.Groups["h"].Value, CultureInfo.InvariantCulture));
            }
            else if (Recovery.Match(line) is { Success: true } recovery)
            {
                recoveries.Add(recovery.Value);
            }
            else if (StreamEnd.Match(line) is { Success: true } ended && start is { } at)
            {
                streams.Add(new NodeStream(at.X, at.Y, ended.Groups["counts"].Value,
                    long.Parse(ReplacedCount.Match(line).Groups["replaced"].Value, CultureInfo.InvariantCulture),
                    int.Parse(ended.Groups["stalls"].Value, CultureInfo.InvariantCulture),
                    int.Parse(ended.Groups["recovered"].Value, CultureInfo.InvariantCulture), [.. recoveries], width, height, panned, [.. gains]));
                start = null;
            }
        }
        return streams;
    }

    private static async Task DisposeAsync(GuiNodeHarness.GuiClient gui)
    {
        await gui.Cts.CancelAsync();
        await gui.Tracker.DrainAsync();
        if (gui.AppState.LocalNode is { } local)
        {
            await local.DisposeAsync();
        }
        await gui.Services.DisposeAsync();
        gui.Cts.Dispose();
    }

    private static int IntFromEnvironment(string name, int fallback)
        => int.TryParse(Environment.GetEnvironmentVariable(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value > 0 ? value : fallback;

    /// <summary>
    /// The GUI's render loop, as far as a probe drives it: each frame the planetary view's <see cref="PlanetaryCaptureController.Tick"/>,
    /// the signals posted since the last, the presence beat and the tracker's completions; and once a second the endpoints
    /// of the watched processes.
    /// </summary>
    private sealed class Pump(GuiNodeHarness.GuiClient gui, int[] watched, HashSet<OwnedEndpoints.Endpoint> endpoints)
    {
        private readonly PlanetaryCaptureController _planetary = gui.Services.GetRequiredService<PlanetaryCaptureController>();
        private long _lastSample;

        public async Task ForAsync(TimeSpan duration, CancellationToken ct)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            await UntilAsync(_ => ValueTask.FromResult(clock.Elapsed >= duration), ct);
        }

        /// <summary>Drives frames until <paramref name="done"/>, asked about every sixth frame (100 ms), says so.</summary>
        public async Task UntilAsync(Func<CancellationToken, ValueTask<bool>> done, CancellationToken ct)
        {
            for (var frame = 0; ; frame++)
            {
                if (frame % 6 == 0 && await done(ct))
                {
                    return;
                }
                gui.Bus.ProcessPending(gui.Tracker);
                _planetary.Tick();
                gui.Handler.BeatNodes();
                gui.Tracker.ProcessCompletions(NullLogger.Instance);
                if (System.Diagnostics.Stopwatch.GetElapsedTime(_lastSample) >= EndpointSampleEvery)
                {
                    OwnedEndpoints.Sample(watched, endpoints);
                    _lastSample = System.Diagnostics.Stopwatch.GetTimestamp();
                }
                await SystemTimeProvider.Instance.SleepAsync(RenderFrame, ct);
            }
        }
    }
}
