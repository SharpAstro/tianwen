using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using System;
using System.Text.Json;
using System.Threading.Tasks;
using TianWen.Hosting;
using TianWen.Hosting.Dto;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Fake;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The node's crash journal (P1 of docs/plans/hardware-in-the-server.md, #917): what it holds, and how a node that finds
/// one judges it. A journal is believed when the node that wrote it is the one its keeper just saw crash, and otherwise
/// only when it is younger than the machine's last boot; <c>NodeJournalServiceTests</c> and
/// <c>NodeJournalProcessTests</c> are the node keeping one.
/// </summary>
public class NodeJournalTests(ITestOutputHelper output)
{
    private static readonly DateTimeOffset Boot = new DateTimeOffset(2026, 9, 20, 22, 57, 41, TimeSpan.Zero);

    private static NodeJournal Held(DateTimeOffset written, int pid = 4242) => new NodeJournal
    {
        WrittenUtc = written,
        ProcessId = pid,
        Devices = [new NodeJournalDevice("Camera://FakeDevice/FakeCamera1", "Fake Camera 1", CoolerIntentKind.Cool, -10)],
        Run = new NodeJournalRun(NodeRunKind.Session, Guid.Parse("5a1ec7ed-0b0e-4e5d-9a5e-000000000001"), Boot.AddHours(1), "M 42"),
    };

    [Fact]
    public void ARestartAfterACrashVouchesOnlyForTheJournalOfTheNodeThatCrashed()
    {
        // Written a day before the boot, by the node whose crash the keeper just saw: no boot can have come between.
        var vouched = Held(Boot.AddDays(-1), pid: 4242).Recover(afterCrashOf: 4242, Boot.AddDays(1), lastBootUtc: Boot);
        vouched.AfterCrash.ShouldBeTrue();
        vouched.Stale.ShouldBeFalse("its keeper saw it crash just now");

        // Another node's journal, left over from before: the keeper vouches for nothing it did not see.
        var other = Held(Boot.AddDays(-1), pid: 4242).Recover(afterCrashOf: 777, Boot.AddDays(1), lastBootUtc: Boot);
        other.AfterCrash.ShouldBeFalse();
        other.Stale.ShouldBeTrue("older than the boot, and nobody saw its node die");
    }

    [Fact]
    public void AJournalOlderThanTheLastBootIsStaleAndANewerOneIsNot()
    {
        Held(Boot.AddMinutes(-1)).Recover(afterCrashOf: null, Boot.AddHours(1), Boot).Stale.ShouldBeTrue("a power cut or a restart came after it");
        Held(Boot.AddMinutes(1)).Recover(afterCrashOf: null, Boot.AddHours(1), Boot).Stale.ShouldBeFalse();
    }

    [Fact]
    public void AJournalWhoseAgeAgainstTheBootCannotBeJudgedIsStale()
    {
        // Acting on a rig whose state nobody knows is the one thing the guard is for, so an unknown boot counts as one.
        Held(Boot).Recover(afterCrashOf: null, Boot.AddHours(1), lastBootUtc: null).Stale.ShouldBeTrue();
    }

    [Fact]
    public void ASecondCrashWithinTheWindowIsALoopThatNamesTheDeviceItWasReachingFor()
    {
        // The node before died reconnecting the camera, a minute after the crash it was recovering from.
        var found = Held(Boot.AddHours(3)) with { Crashes = [Boot.AddHours(3).AddMinutes(-1)], Touching = "Camera://FakeDevice/FakeCamera1" };

        var report = found.Recover(afterCrashOf: 4242, Boot.AddHours(3), Boot);

        report.CrashLoop.ShouldBeTrue("a driver that crashes the node would crash every node that reconnected it");
        report.SuspectDevice.ShouldBe("Camera://FakeDevice/FakeCamera1");
        found.CrashesWith(Boot.AddHours(3)).Length.ShouldBe(2);
    }

    [Fact]
    public void CrashesFurtherApartThanTheWindowAreNoLoop()
    {
        var found = Held(Boot.AddHours(3)) with { Crashes = [Boot.AddHours(3) - NodeKeeper.CrashLoopWindow - TimeSpan.FromSeconds(1)] };

        found.Recover(afterCrashOf: 4242, Boot.AddHours(3), Boot).CrashLoop.ShouldBeFalse();
        found.CrashesWith(Boot.AddHours(3)).ShouldBe([Boot.AddHours(3)], "only the crash that left this journal counts, and it is carried on");
    }

    [Fact]
    public void AStaleJournalIsNoCrashLoop()
    {
        // A boot came between: whatever crashed before it is not a loop this node is in.
        var found = Held(Boot.AddMinutes(-2)) with { Crashes = [Boot.AddMinutes(-3)] };

        var report = found.Recover(afterCrashOf: null, Boot.AddMinutes(1), Boot);

        report.Stale.ShouldBeTrue();
        report.CrashLoop.ShouldBeFalse();
    }

    [Fact]
    public void TheReportSaysWhichRunWasInterruptedAndWhatWasHeld()
    {
        var journal = Held(Boot.AddHours(3));

        var report = journal.Recover(afterCrashOf: null, Boot.AddHours(4), Boot);

        report.JournalWrittenUtc.ShouldBe(Boot.AddHours(3));
        report.FoundUtc.ShouldBe(Boot.AddHours(4));
        var run = report.InterruptedRun.ShouldNotBeNull();
        run.Kind.ShouldBe(NodeRunKind.Session);
        run.Target.ShouldBe("M 42");
        run.StartedUtc.ShouldBe(Boot.AddHours(1));
        var camera = report.Devices.ShouldHaveSingleItem();
        camera.DeviceUri.ShouldBe("Camera://FakeDevice/FakeCamera1");
        camera.Cooler.ShouldBe(CoolerIntentKind.Cool);
        camera.CoolerSetpointC.ShouldBe(-10);
    }

    [Fact]
    public async Task TheJournalHoldsTheHubsDevicesWithEachCamerasCoolerIntent()
    {
        var ct = TestContext.Current.CancellationToken;
        var hub = new FakeExternal(output).BuildServiceProvider().GetRequiredService<IDeviceHub>();
        var camera = new FakeDevice(DeviceType.Camera, 1);
        var mount = new FakeDevice(DeviceType.Mount, 1);
        await hub.ConnectAsync(mount, ct);
        await hub.ConnectAsync(camera, ct);
        hub.SetCoolerIntent(camera.DeviceUri, CoolerIntent.Warm);
        var run = new NodeRunRecord(NodeRunKind.Flats, Guid.NewGuid(), Boot);

        var journal = NodeJournal.Of(hub, run, session: null, Boot.AddMinutes(5), processId: 99);

        journal.HoldsAnything.ShouldBeTrue();
        journal.Devices.Length.ShouldBe(2);
        journal.Devices[0].DeviceUri.ShouldBe(camera.DeviceUri.ToString(), "in URI order, so the same holdings compare equal");
        journal.Devices[0].Cooler.ShouldBe(CoolerIntentKind.Warm);
        journal.Devices[0].CoolerSetpointC.ShouldBeNull("a warm-up names no setpoint, and NaN is not JSON");
        journal.Devices[1].Cooler.ShouldBeNull("a mount has no cooler, and nothing asked anything of it");
        journal.Run.ShouldBe(new NodeJournalRun(NodeRunKind.Flats, run.ProfileId, Boot, Target: null));
    }

    [Fact]
    public void WhatAJournalHoldsIsComparedWithoutWhenOrWhoWroteIt()
    {
        Held(Boot, pid: 1).HoldsTheSameAs(Held(Boot.AddHours(1), pid: 2)).ShouldBeTrue();
        Held(Boot).HoldsTheSameAs(Held(Boot) with { Run = null }).ShouldBeFalse();
        Held(Boot).HoldsTheSameAs(Held(Boot) with { Devices = [] }).ShouldBeFalse();
        Held(Boot).HoldsTheSameAs(Held(Boot) with { Touching = "Mount://FakeDevice/FakeMount1" }).ShouldBeFalse("the device being reconnected must reach the file first");
        Held(Boot).HoldsTheSameAs(Held(Boot) with { Crashes = [Boot] }).ShouldBeFalse();
        new NodeJournal().HoldsAnything.ShouldBeFalse();
        new NodeJournal { Touching = "Mount://FakeDevice/FakeMount1" }.HoldsAnything.ShouldBeTrue("named before its connect, when nothing is held yet");
    }

    [Fact]
    public void TheJournalRoundTripsWithItsKindsByName()
    {
        // A person reads it after a crash, so its enums are names.
        var json = JsonSerializer.Serialize(Held(Boot), NodeJournalJsonContext.Default.NodeJournal);
        json.ShouldContain("\"Cool\"");
        json.ShouldContain("\"Session\"");

        var back = JsonSerializer.Deserialize(json, NodeJournalJsonContext.Default.NodeJournal).ShouldNotBeNull();
        back.HoldsTheSameAs(Held(Boot)).ShouldBeTrue();
        back.WrittenUtc.ShouldBe(Boot);
        back.ProcessId.ShouldBe(4242);
    }

    [Fact]
    public void TheJournalLivesBesideTheNodesSocketAndLock()
    {
        var socket = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "node.sock");
        NodeJournal.PathFor(socket).ShouldBe(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "node.journal"));
    }

    [Fact]
    public void TheMachineBootedInThePastAndNoEarlierThanItsUptimeAllows()
    {
        var boot = MachineBoot.LastBootUtc();
        if (!(OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()))
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var booted = boot.ShouldNotBeNull("every platform the node ships on can say when it booted");
        booted.ShouldBeLessThanOrEqualTo(now.AddSeconds(1));
        output.WriteLine($"Last boot {booted:o}; uptime says {now - TimeSpan.FromMilliseconds(Environment.TickCount64):o}");
        if (OperatingSystem.IsWindows())
        {
            // The Kernel-Boot event can only be LATER than the kernel's own start (a Fast Startup boot), never earlier.
            booted.ShouldBeGreaterThanOrEqualTo(now - TimeSpan.FromMilliseconds(Environment.TickCount64) - TimeSpan.FromSeconds(2));
        }
    }

    [Fact]
    public void OnWindowsTheKernelsOwnBootEventAnswersNotJustTheUptime()
    {
        // The event log is the only source that sees a Fast Startup boot; a fallback to the tick count would pass the
        // test above and miss exactly those.
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var logged = MachineBoot.NewestKernelBootEventUtc().ShouldNotBeNull("the System log holds a Kernel-Boot event 27 for every boot");
        output.WriteLine($"Kernel-Boot event 27 at {logged:o}");
        logged.ShouldBeLessThanOrEqualTo(DateTimeOffset.UtcNow);
        logged.ShouldBeGreaterThan(DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64) - TimeSpan.FromMinutes(1),
            "no earlier than the kernel's own start, less the moment it takes to log it");
    }
}
