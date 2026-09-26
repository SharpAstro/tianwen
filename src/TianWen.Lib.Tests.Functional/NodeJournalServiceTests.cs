using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Hosting;
using TianWen.Hosting.Dto;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Fake;
using TianWen.RemoteClient;
using Xunit;

namespace TianWen.Lib.Tests.Functional;

/// <summary>
/// A node keeping its crash journal (P1 of docs/plans/hardware-in-the-server.md, #917): written as what it holds changes,
/// gone once it holds nothing, and the one a node before it left reported until a client dismisses it. In process, on a
/// journal path of the test's own; <c>NodeJournalProcessTests</c> kills a real node.
/// </summary>
[Collection("Hosting")]
public class NodeJournalServiceTests(ITestOutputHelper outputHelper)
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);

    private static string NewJournalPath() => Path.Combine(Directory.CreateTempSubdirectory("twj").FullName, "node.journal");

    private Task<NodeHarness> StartAsync(string journal, int? afterCrashOf, DateTimeOffset? lastBoot, CancellationToken ct) =>
        NodeHarness.StartAsync(outputHelper, ct,
            services => services.AddSingleton(new NodeJournalOptions(journal, afterCrashOf, () => lastBoot, TimeProvider.System)));

    private static NodeJournal? Read(string path)
    {
        try
        {
            return JsonSerializer.Deserialize(File.ReadAllText(path), NodeJournalJsonContext.Default.NodeJournal);
        }
        catch (Exception ex) when (ex is FileNotFoundException or IOException or JsonException)
        {
            // Not there, or being replaced this instant: look again.
            return null;
        }
    }

    private static async Task<NodeJournal> UntilTheJournalAsync(string path, Func<NodeJournal, bool> holds, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < Budget)
        {
            if (Read(path) is { } journal && holds(journal))
            {
                return journal;
            }
            await Task.Delay(20, ct);
        }
        throw new TimeoutException($"The journal {path} never held what the test waited for");
    }

    private static async Task UntilGoneAsync(string path, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        while (File.Exists(path) && clock.Elapsed < Budget)
        {
            await Task.Delay(20, ct);
        }
        File.Exists(path).ShouldBeFalse("a node that holds nothing leaves no journal");
    }

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
        await UntilGoneAsync(path, ct);
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
    public async Task AJournalFoundAtStartIsReportedUntilAClientDismissesIt()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = NewJournalPath();
        var written = DateTimeOffset.UtcNow.AddMinutes(-3);
        var dead = new NodeJournal
        {
            WrittenUtc = written,
            ProcessId = 31337,
            Devices = [new NodeJournalDevice("Camera://FakeDevice/FakeCamera1#Fake Camera 1", "Fake Camera 1", CoolerIntentKind.Cool, -20)],
            Run = new NodeJournalRun(NodeRunKind.Session, NodeHarness.ProfileId, written.AddHours(-1), "NGC 7000"),
        };
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(dead, NodeJournalJsonContext.Default.NodeJournal), ct);

        await using var node = await StartAsync(path, afterCrashOf: 31337, lastBoot: written.AddDays(1), ct);
        var client = new TianWenNodeClient(node.Client);

        var report = (await client.GetNodeAsync(ct)).Value.ShouldNotBeNull().Recovery.ShouldNotBeNull();
        report.AfterCrash.ShouldBeTrue("its keeper saw that very node crash");
        report.Stale.ShouldBeFalse("so a boot the machine reports later is not one that came between");
        report.InterruptedRun.ShouldNotBeNull().Target.ShouldBe("NGC 7000");
        var camera = report.Devices.ShouldHaveSingleItem();
        camera.Cooler.ShouldBe(CoolerIntentKind.Cool);
        camera.CoolerSetpointC.ShouldBe(-20);
        File.Exists(path).ShouldBeTrue("kept while this node holds nothing, for the next one should this one die too");

        (await client.DismissRecoveryAsync(ct)).IsSuccess.ShouldBeTrue();

        (await client.GetNodeAsync(ct)).Value.ShouldNotBeNull().Recovery.ShouldBeNull();
        await UntilGoneAsync(path, ct);
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
