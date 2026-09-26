using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Hosting;
using TianWen.Hosting.Dto;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Fake;
using TianWen.RemoteClient;
using Xunit;
using static TianWen.Lib.Tests.Functional.NodeWait;

namespace TianWen.Lib.Tests.Functional;

/// <summary>
/// A node keeping its crash journal (P1 of docs/plans/hardware-in-the-server.md, #917): written as what it holds changes,
/// gone once it holds nothing, and the one a node before it left reported until a client dismisses it, and acted on when
/// it can be believed: the devices reconnected and each camera's cooling re-established, a crash loop excepted. In
/// process, on a journal path of the test's own; <c>NodeJournalProcessTests</c> kills a real node.
/// </summary>
[Collection("Hosting")]
public class NodeJournalServiceTests(ITestOutputHelper outputHelper)
{
    private static string NewJournalPath() => Path.Combine(Directory.CreateTempSubdirectory("twj").FullName, "node.journal");

    private Task<NodeHarness> StartAsync(string journal, int? afterCrashOf, DateTimeOffset? lastBoot, CancellationToken ct) =>
        NodeHarness.StartAsync(outputHelper, ct,
            services => services.AddSingleton(new NodeJournalOptions(journal, afterCrashOf, () => lastBoot, TimeProvider.System)));

    [Fact(Timeout = 60_000)]
    public async Task WhatTheNodeHoldsIsJournaledAsItChangesAndNothingHeldLeavesNoJournal()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = NewJournalPath();
        await using var node = await StartAsync(path, afterCrashOf: null, lastBoot: null, ct);
        var hub = node.App.Services.GetRequiredService<IDeviceHub>();
        var camera = new FakeDevice(DeviceType.Camera, 1);

        await hub.ConnectAsync(camera, ct);
        var connected = await UntilTheJournalAsync(path, static j => j.Devices.Length == 1, ct);
        connected.Devices[0].DeviceUri.ShouldBe(camera.DeviceUri.ToString());
        connected.ProcessId.ShouldBe(Environment.ProcessId, "the pid its keeper would name after a crash");

        hub.SetCoolerIntent(camera.DeviceUri, CoolerIntent.CoolTo(-10));
        var cooling = await UntilTheJournalAsync(path, static j => j.Devices.Length == 1 && j.Devices[0].Cooler is CoolerIntentKind.Cool, ct);
        cooling.Devices[0].CoolerSetpointC.ShouldBe(-10);

        await hub.DisconnectAsync(camera.DeviceUri, cancellationToken: ct);
        await UntilTheJournalIsGoneAsync(path, ct);
    }

    [Fact(Timeout = 60_000)]
    public async Task ANodeWhoseStopFinishedLeavesNoJournalThoughItsMountWasConnected()
    {
        // The host's stop warms and releases the cameras; a mount left for the process's exit would have been journaled
        // as held by a node that died, and reported by the next one as a crash that never happened.
        var ct = TestContext.Current.CancellationToken;
        var path = NewJournalPath();
        await using var node = await StartAsync(path, afterCrashOf: null, lastBoot: null, ct);
        var hub = node.App.Services.GetRequiredService<IDeviceHub>();
        await hub.ConnectAsync(new FakeDevice(DeviceType.Mount, 1), ct);
        await UntilTheJournalAsync(path, static j => j.Devices.Length == 1, ct);

        await node.App.StopAsync(ct);

        File.Exists(path).ShouldBeFalse();
    }

    [Fact(Timeout = 60_000)]
    public async Task ACrashLoopReconnectsNothingNamesTheSuspectAndIsReportedUntilDismissed()
    {
        // The node before crashed reconnecting the camera, a minute after the crash it was recovering from.
        var ct = TestContext.Current.CancellationToken;
        var path = NewJournalPath();
        var written = DateTimeOffset.UtcNow.AddSeconds(-5);
        var dead = new NodeJournal
        {
            WrittenUtc = written,
            ProcessId = 31337,
            Devices = [new NodeJournalDevice(Camera, "Fake Camera 1", CoolerIntentKind.Cool, -20)],
            Run = new NodeJournalRun(NodeRunKind.Session, NodeHarness.ProfileId, written.AddHours(-1), "NGC 7000"),
            Crashes = [written.AddMinutes(-1)],
            Touching = Camera,
        };
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(dead, NodeJournalJsonContext.Default.NodeJournal), ct);

        await using var node = await StartAsync(path, afterCrashOf: 31337, lastBoot: written.AddDays(1), ct);
        var client = new TianWenNodeClient(node.Client);

        var report = (await client.GetNodeAsync(ct)).Value.ShouldNotBeNull().Recovery.ShouldNotBeNull();
        report.AfterCrash.ShouldBeTrue("its keeper saw that very node crash");
        report.Stale.ShouldBeFalse("so a boot the machine reports later is not one that came between");
        report.CrashLoop.ShouldBeTrue();
        report.SuspectDevice.ShouldBe(Camera);
        report.InterruptedRun.ShouldNotBeNull().Target.ShouldBe("NGC 7000");
        var camera = report.Devices.ShouldHaveSingleItem();
        camera.Cooler.ShouldBe(CoolerIntentKind.Cool);
        camera.CoolerSetpointC.ShouldBe(-20);
        camera.Reconnected.ShouldBeNull("a crash loop reconnects nothing");
        node.App.Services.GetRequiredService<IDeviceHub>().ConnectedDevices.ShouldBeEmpty();
        File.Exists(path).ShouldBeTrue("kept, naming the suspect, while this node holds nothing, for the next one should this one die too");

        (await client.DismissRecoveryAsync(ct)).IsSuccess.ShouldBeTrue();

        (await client.GetNodeAsync(ct)).Value.ShouldNotBeNull().Recovery.ShouldBeNull();
        await UntilTheJournalIsGoneAsync(path, ct);
    }

    private const string Mount = "Mount://FakeDevice/FakeMount1?latitude=48.2&longitude=16.3#Fake Mount";
    private const string Camera = "Camera://FakeDevice/FakeCamera1#Fake Camera 1";

    /// <summary>A journal the keeper vouches for: a mount and a camera cooling to -10 C, in a session's run.</summary>
    private static async Task<(string Path, DateTimeOffset Written)> BelievedJournalAsync(CancellationToken ct, params NodeJournalDevice[] devices)
    {
        var path = NewJournalPath();
        var written = DateTimeOffset.UtcNow.AddSeconds(-5);
        var dead = new NodeJournal
        {
            WrittenUtc = written,
            ProcessId = 31337,
            Devices = devices.Length > 0 ? [.. devices]
                : [new NodeJournalDevice(Camera, "Fake Camera 1", CoolerIntentKind.Cool, -10), new NodeJournalDevice(Mount, "Fake Mount", null, null)],
            Run = new NodeJournalRun(NodeRunKind.Session, NodeHarness.ProfileId, written.AddHours(-1), "NGC 7000"),
        };
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(dead, NodeJournalJsonContext.Default.NodeJournal), ct);
        return (path, written);
    }

    private static Task<NodeRecoveryDto> UntilRecoveredAsync(TianWenNodeClient client, CancellationToken ct) =>
        UntilAsync<NodeRecoveryDto>("the node to finish reconnecting what the journal held", async token =>
        {
            var report = (await client.GetNodeAsync(token)).Value?.Recovery;
            return report is null
                ? (null, "no recovery reported")
                : (report.Devices.All(static d => d.Reconnected is not null) ? report : null,
                    $"{report.Devices.Count(static d => d.Reconnected is not null)} of {report.Devices.Length} device(s) reported");
        }, ct);

    [Fact(Timeout = 60_000)]
    public async Task ABelievedJournalIsActedOnItsDevicesReconnectedAndItsCameraCooledBack()
    {
        var ct = TestContext.Current.CancellationToken;
        var (path, _) = await BelievedJournalAsync(ct);

        await using var node = await StartAsync(path, afterCrashOf: 31337, lastBoot: null, ct);
        var hub = node.App.Services.GetRequiredService<IDeviceHub>();

        var report = await UntilRecoveredAsync(new TianWenNodeClient(node.Client), ct);

        report.CrashLoop.ShouldBeFalse();
        report.Devices.ShouldAllBe(static d => d.Reconnected == true && d.ReconnectError == null);
        hub.IsConnected(new Uri(Mount)).ShouldBeTrue("the mount, which restores mount-limit enforcement");
        hub.TryGetConnectedDriver<ICameraDriver>(new Uri(Camera), out var camera).ShouldBeTrue();
        hub.TryGetCoolerIntent(new Uri(Camera), out var intent).ShouldBeTrue("recorded before the camera is reported reconnected");
        intent.ShouldBe(CoolerIntent.CoolTo(-10), "cooled back to its target through the session's ramp");

        // The ramp runs in the background, so its first step, the cooler on, follows the report rather than precedes it.
        await UntilAsync("the ramp's first step, the cooler on", async token =>
        {
            var on = await camera.GetCoolerOnAsync(token);
            return (on, on ? "the cooler on" : "the cooler off");
        }, ct);

        // This node's own journal now: what it reconnected, the run it resumes nothing of, and this crash counted. The
        // first it writes with nothing being reconnected is the recovery's last, which must already carry the intent.
        var journal = await UntilTheJournalAsync(path, static j => j.ProcessId == Environment.ProcessId && j.Devices.Length == 2 && j.Touching is null, ct);
        journal.Devices.ShouldContain(static d => d.Cooler == CoolerIntentKind.Cool && d.CoolerSetpointC == -10,
            "a crash before the next poll must still leave the camera's cooling to re-establish");
        journal.Crashes.Length.ShouldBe(1);
        journal.Run.ShouldNotBeNull().Target.ShouldBe("NGC 7000");
        node.Node.IsRunning.ShouldBeFalse("it never resumes a run");
    }

    [Fact(Timeout = 60_000)]
    public async Task ARunStartingEndsTheRecoverysCoolingRamp()
    {
        // The session cools its own cameras: a recovery's ramp on the same camera would fight it.
        var ct = TestContext.Current.CancellationToken;
        var (path, _) = await BelievedJournalAsync(ct);
        await using var node = await StartAsync(path, afterCrashOf: 31337, lastBoot: null, ct);
        var journal = node.App.Services.GetRequiredService<NodeJournalService>();
        await UntilRecoveredAsync(new TianWenNodeClient(node.Client), ct);
        journal.RampsRunning.ShouldBe(1, "a -10 C cool-down from ambient takes minutes");

        node.Factory.Initialised.SetResult();
        await node.StartSessionAsync(ct);

        await UntilAsync("the recovery's ramp to end", _ => ValueTask.FromResult((journal.RampsRunning == 0, $"{journal.RampsRunning} ramp(s) running")), ct);
    }

    [Fact(Timeout = 60_000)]
    public async Task TheJournalNamesADeviceBeforeItsConnectSoADriverThatCrashesTheNodeIsTheNextOnesSuspect()
    {
        // A connect the test holds open: the journal must name the device while it is under way, since a driver that
        // crashes the node crashes it there, and nothing written after the connect would ever reach the file.
        var ct = TestContext.Current.CancellationToken;
        const string gated = "CoverCalibrator://GatedCoverDevice/gated#Gated Panel";
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (path, _) = await BelievedJournalAsync(ct, new NodeJournalDevice(gated, "Gated Panel", null, null));
        await using var node = await NodeHarness.StartAsync(outputHelper, ct, services =>
        {
            services.AddSingleton(new NodeJournalOptions(path, 31337, static () => null, TimeProvider.System));
            services.AddKeyedSingleton<Func<Uri, DeviceBase>>("gatedcoverdevice", (_, _) => uri => new GatedCoverDevice(uri, gate.Task));
        });

        try
        {
            var during = await UntilTheJournalAsync(path, static j => j.ProcessId == Environment.ProcessId, ct);
            during.Touching.ShouldBe(gated);
            during.Devices.ShouldHaveSingleItem().DeviceUri.ShouldBe(gated, "still to reconnect, so still held");
        }
        finally
        {
            // Whatever failed above, the node's stop must not wait out a connect the test is holding shut.
            gate.TrySetResult();
        }

        var after = await UntilTheJournalAsync(path, static j => j.Touching is null, ct);
        after.Devices.ShouldHaveSingleItem();
    }

    /// <summary>
    /// A cover whose driver's connect waits for <paramref name="Gate"/>, and for its token too unless
    /// <paramref name="IgnoresItsToken"/>, as a serial driver blocked in a read may.
    /// </summary>
    private sealed record GatedCoverDevice(Uri Uri, Task Gate, bool IgnoresItsToken = false) : DeviceBase(Uri)
    {
        protected override IDeviceDriver? NewInstanceFromDevice(IServiceProvider sp)
        {
            var driver = Substitute.For<ICoverDriver>();
            driver.ConnectAsync(Arg.Any<CancellationToken>())
                .Returns(call => new ValueTask(IgnoresItsToken ? Gate : Gate.WaitAsync(call.Arg<CancellationToken>())));
            driver.Connected.Returns(_ => Gate.IsCompletedSuccessfully);
            return driver;
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task TheRecoveryEndsAsTheHostBeginsToStopNotWhenItsJournalDoes()
    {
        // The journal stops last. A recovery that went on until then connected devices the host's stop had already
        // released, and a camera it re-cooled then would exit cold, past the stop that warms cameras.
        var ct = TestContext.Current.CancellationToken;
        const string gated = "CoverCalibrator://GatedCoverDevice/gated#Gated Panel";
        const string focuser = "Focuser://FakeDevice/FakeFocuser1#Fake Focuser";
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (path, _) = await BelievedJournalAsync(ct,
            new NodeJournalDevice(gated, "Gated Panel", null, null),
            new NodeJournalDevice(focuser, "Fake Focuser", null, null));
        await using var node = await NodeHarness.StartAsync(outputHelper, ct, services =>
        {
            services.AddSingleton(new NodeJournalOptions(path, 31337, static () => null, TimeProvider.System));
            services.AddKeyedSingleton<Func<Uri, DeviceBase>>("gatedcoverdevice", (_, _) => uri => new GatedCoverDevice(uri, gate.Task));
        });
        await UntilTheJournalAsync(path, static j => j.ProcessId == Environment.ProcessId && j.Touching == gated, ct);
        node.Factory.Initialised.SetResult();
        var run = await node.StartSessionAsync(ct);

        // The host's stop is under way (the run ends through a Finalise the test holds), and the connect the recovery
        // was waiting on could finish now.
        var stop = node.App.StopAsync(ct);
        await run.Cancelled.Task.WaitAsync(ct);
        gate.SetResult();
        await Task.Delay(TimeSpan.FromSeconds(1), ct);

        node.App.Services.GetRequiredService<IDeviceHub>().IsConnected(new Uri(focuser))
            .ShouldBeFalse("the recovery ended as the host began to stop, before the next device");

        run.Finalise.SetResult();
        await stop;
    }

    [Fact(Timeout = 60_000)]
    public async Task AConnectThatLandsAfterTheHostReleasedTheRigLeavesNoJournal()
    {
        // A driver that ignores its token finishes its connect after the host's stop released the rig: the device is
        // connected as the process exits, which the journal must not take for something a dying node held.
        var ct = TestContext.Current.CancellationToken;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (path, _) = await BelievedJournalAsync(ct, new NodeJournalDevice("CoverCalibrator://GatedCoverDevice/gated#Gated Panel", "Gated Panel", null, null));
        await using var node = await NodeHarness.StartAsync(outputHelper, ct, services =>
        {
            services.AddSingleton(new NodeJournalOptions(path, 31337, static () => null, TimeProvider.System));
            services.AddKeyedSingleton<Func<Uri, DeviceBase>>("gatedcoverdevice", (_, _) => uri => new GatedCoverDevice(uri, gate.Task, IgnoresItsToken: true));
        });
        await UntilTheJournalAsync(path, static j => j.ProcessId == Environment.ProcessId && j.Touching is not null, ct);

        var stop = node.App.StopAsync(ct);
        await Task.Delay(TimeSpan.FromSeconds(1), ct);
        gate.SetResult();
        await stop;

        File.Exists(path).ShouldBeFalse("the host's stop finished, so what connected after it was no holding of a dying node");
    }

    [Fact(Timeout = 60_000)]
    public async Task ANodeAskedToStopMidRecoveryStopsReconnectingAndLeavesNoJournal()
    {
        // Its stop releases the rig: a recovery going on connecting devices meanwhile left them connected and journaled,
        // and the next node reported a crash that never happened.
        var ct = TestContext.Current.CancellationToken;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (path, _) = await BelievedJournalAsync(ct, new NodeJournalDevice("CoverCalibrator://GatedCoverDevice/gated#Gated Panel", "Gated Panel", null, null));
        await using var node = await NodeHarness.StartAsync(outputHelper, ct, services =>
        {
            services.AddSingleton(new NodeJournalOptions(path, 31337, static () => null, TimeProvider.System));
            services.AddKeyedSingleton<Func<Uri, DeviceBase>>("gatedcoverdevice", (_, _) => uri => new GatedCoverDevice(uri, gate.Task));
        });
        await UntilTheJournalAsync(path, static j => j.ProcessId == Environment.ProcessId && j.Touching is not null, ct);

        var clock = Stopwatch.StartNew();
        await node.App.StopAsync(ct);

        clock.Elapsed.ShouldBeLessThan(NodeJournalService.ReconnectBudget, "the stop ended the recovery rather than waiting its connect out");
        node.App.Services.GetRequiredService<IDeviceHub>().ConnectedDevices.ShouldBeEmpty();
        File.Exists(path).ShouldBeFalse("a stop the host finished leaves no journal");
    }

    [Fact(Timeout = 60_000)]
    public async Task AJournalWithAUriThatDoesNotParseIsReportedAndTheJournalGoesOn()
    {
        // A journal is a file a person may edit: one bad line used to end the node's journaling for good.
        var ct = TestContext.Current.CancellationToken;
        var (path, _) = await BelievedJournalAsync(ct,
            new NodeJournalDevice("not a device uri", "Mangled", null, null),
            new NodeJournalDevice(Camera, "Fake Camera 1", null, null));
        await using var node = await StartAsync(path, afterCrashOf: 31337, lastBoot: null, ct);
        var hub = node.App.Services.GetRequiredService<IDeviceHub>();

        var report = await UntilRecoveredAsync(new TianWenNodeClient(node.Client), ct);

        report.Devices.Single(static d => d.DeviceUri == "not a device uri").Reconnected.ShouldBe(false);
        report.Devices.Single(static d => d.DeviceUri == Camera).Reconnected.ShouldBe(true);
        await UntilTheJournalAsync(path, static j => j.ProcessId == Environment.ProcessId && j.Devices.Length == 1 && j.Touching is null, ct);

        // And it goes on journaling: the camera going is written, and holding nothing more, the file goes.
        (await new TianWenNodeClient(node.Client).DismissRecoveryAsync(ct)).IsSuccess.ShouldBeTrue();
        await hub.DisconnectAsync(new Uri(Camera), cancellationToken: ct);
        await UntilTheJournalIsGoneAsync(path, ct);
    }

    [Fact(Timeout = 60_000)]
    public async Task ACoolIntentThatNamesNoSetpointLeavesTheCoolerAlone()
    {
        var ct = TestContext.Current.CancellationToken;
        var (path, _) = await BelievedJournalAsync(ct, new NodeJournalDevice(Camera, "Fake Camera 1", CoolerIntentKind.Cool, null));
        await using var node = await StartAsync(path, afterCrashOf: 31337, lastBoot: null, ct);
        var hub = node.App.Services.GetRequiredService<IDeviceHub>();

        await UntilRecoveredAsync(new TianWenNodeClient(node.Client), ct);

        hub.TryGetCoolerIntent(new Uri(Camera), out var intent).ShouldBeFalse($"never recorded as off, which it was not asked to be: {intent}");
        node.App.Services.GetRequiredService<NodeJournalService>().RampsRunning.ShouldBe(0);
    }

    [Fact(Timeout = 60_000)]
    public async Task ADeviceThatCannotBeReconnectedIsReportedWithWhy()
    {
        var ct = TestContext.Current.CancellationToken;
        var (path, _) = await BelievedJournalAsync(ct, new NodeJournalDevice("Camera://NoSuchDevice/cam#Gone", "Gone", CoolerIntentKind.Cool, -10));
        await using var node = await StartAsync(path, afterCrashOf: 31337, lastBoot: null, ct);

        var report = await UntilRecoveredAsync(new TianWenNodeClient(node.Client), ct);

        var device = report.Devices.ShouldHaveSingleItem();
        device.Reconnected.ShouldBe(false);
        device.ReconnectError.ShouldNotBeNull().ShouldContain("No device source");
    }

    [Fact(Timeout = 60_000)]
    public async Task AJournalOlderThanTheLastBootIsReportedAsStale()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = NewJournalPath();
        var written = DateTimeOffset.UtcNow.AddHours(-5);
        var dead = new NodeJournal
        {
            WrittenUtc = written,
            ProcessId = 31337,
            Devices = [new NodeJournalDevice("Mount://FakeDevice/FakeMount1", "Fake Mount", null, null)],
        };
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(dead, NodeJournalJsonContext.Default.NodeJournal), ct);

        // Started by a client, not by a keeper that saw a crash: a power cut came after the journal.
        await using var node = await StartAsync(path, afterCrashOf: null, lastBoot: written.AddHours(1), ct);

        var report = (await new TianWenNodeClient(node.Client).GetNodeAsync(ct)).Value.ShouldNotBeNull().Recovery.ShouldNotBeNull();
        report.Stale.ShouldBeTrue();
        report.AfterCrash.ShouldBeFalse();
        report.InterruptedRun.ShouldBeNull();
    }

    [Fact(Timeout = 60_000)]
    public async Task ANodeThatStartedCleanlyReportsNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var node = await StartAsync(NewJournalPath(), afterCrashOf: null, lastBoot: null, ct);

        (await new TianWenNodeClient(node.Client).GetNodeAsync(ct)).Value.ShouldNotBeNull().Recovery.ShouldBeNull();
    }
}
