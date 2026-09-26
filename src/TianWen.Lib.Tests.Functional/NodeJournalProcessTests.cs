using Shouldly;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Hosting;
using TianWen.Hosting.Api;
using TianWen.Lib.Devices;
using Xunit;

namespace TianWen.Lib.Tests.Functional;

/// <summary>
/// The crash journal across a real crash (P1 of docs/plans/hardware-in-the-server.md, #917): a node killed while it holds
/// a cooled camera leaves its journal, its keeper starts the next node and tells it which node crashed, and that node
/// reports what was held, reconnects it and cools the camera back through the session's ramp. A second crash within
/// minutes is a loop: the keeper stops, and the node the next client starts reconnects nothing. A node stopped cleanly
/// leaves no journal. Real processes, on a socket and data root of the test's own, with the fake devices only
/// (<see cref="KeptNode"/>).
/// </summary>
[Collection("NodeProcesses")]
public class NodeJournalProcessTests
{
    private static readonly Guid ProfileId = Guid.Parse("7e57ab1e-0b0e-4e5d-9a5e-000000000007");

    /// <summary>A fake mount and one OTA with a fake camera, as the GUI would have saved them, made the node's active profile.</summary>
    private static Task SeedProfileAsync(string dataRoot, CancellationToken ct)
    {
        var profiles = Directory.CreateDirectory(Path.Combine(dataRoot, "Profiles")).FullName;
        var data = new ProfileData(
            Mount: new Uri("Mount://FakeDevice/FakeMount1?latitude=48.2&longitude=16.3"),
            Guider: new Uri("Guider://NoneDevice/none"),
            OTAs: [new OTAData("OTA 1", 1000, Camera: new Uri("Camera://FakeDevice/FakeCamera1"), Cover: null,
                Focuser: null, FilterWheel: null, PreferOutwardFocus: null, OutwardIsPositive: null)],
            SiteLatitude: 48.2,
            SiteLongitude: 16.3);
        return File.WriteAllTextAsync(Path.Combine(profiles, Profile.DeviceIdFromUUID(ProfileId) + ".json"),
            JsonSerializer.Serialize(new ProfileDto(ProfileId, "Journaled", data), Profile.ProfileJsonSerializerContextIndented.ProfileDto), ct);
    }

    /// <summary>An Alpaca PUT on the node's device plane, over its socket, which must succeed at the device.</summary>
    private static async Task AlpacaPutAsync(KeptNode kept, string member, string field, string value, CancellationToken ct)
    {
        using var content = new FormUrlEncodedContent([new KeyValuePair<string, string>(field, value)]);
        using var response = await kept.Http.PutAsync($"api/v1/camera/0/{member}", content, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        body.ShouldContain("\"ErrorNumber\":0", customMessage: $"PUT camera/0/{member}: {body}");
    }

    /// <summary>An Alpaca GET of a camera member's boolean value, over the node's socket.</summary>
    private static async Task<bool> AlpacaGetBoolAsync(KeptNode kept, string member, CancellationToken ct)
    {
        using var response = await kept.Http.GetAsync($"api/v1/camera/0/{member}", ct);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return body.RootElement.GetProperty("Value").GetBoolean();
    }

    private static async Task<NodeJournal> UntilTheJournalAsync(string path, Func<NodeJournal, bool> holds, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < TimeSpan.FromSeconds(30))
        {
            try
            {
                if (JsonSerializer.Deserialize(await File.ReadAllTextAsync(path, ct), NodeJournalJsonContext.Default.NodeJournal) is { } journal && holds(journal))
                {
                    return journal;
                }
            }
            catch (Exception ex) when (ex is FileNotFoundException or IOException or JsonException)
            {
                // Not there yet, or being replaced this instant.
            }
            await Task.Delay(50, ct);
        }
        throw new TimeoutException($"The journal {path} never held what the test waited for");
    }

    [Fact(Timeout = 180_000)]
    public async Task ANodeKilledHoldingACooledCameraIsReportedByTheNodeItsKeeperStartsNext()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var kept = await KeptNode.StartAsync(ct, dataRoot => SeedProfileAsync(dataRoot, ct));
        var first = await kept.WaitForNodeAsync(static _ => true, ct);
        (await kept.Client.SetActiveProfileAsync(ProfileId, ct)).IsSuccess.ShouldBeTrue();

        await AlpacaPutAsync(kept, "connected", "Connected", "true", ct);
        await AlpacaPutAsync(kept, "setccdtemperature", "SetCCDTemperature", "-10", ct);
        await AlpacaPutAsync(kept, "cooleron", "CoolerOn", "true", ct);
        var journalPath = NodeJournal.PathFor(kept.SocketPath);
        await UntilTheJournalAsync(journalPath, static j => j.Devices.Any(static d => d.Cooler is CoolerIntentKind.Cool), ct);

        using (var crashing = Process.GetProcessById(first.ProcessId))
        {
            crashing.Kill();
        }
        var second = await kept.WaitForNodeAsync(node => node.ProcessId != first.ProcessId
            && node.Recovery is { } recovery && recovery.Devices.All(static d => d.Reconnected is not null), ct);

        var report = second.Recovery.ShouldNotBeNull("the node before it died holding a camera");
        report.AfterCrash.ShouldBeTrue("its keeper saw the crash and said so");
        report.Stale.ShouldBeFalse();
        report.CrashLoop.ShouldBeFalse();
        report.InterruptedRun.ShouldBeNull("no run was going on");
        var camera = report.Devices.ShouldHaveSingleItem();
        camera.DeviceUri.ShouldContain("FakeCamera1", Case.Insensitive);
        camera.Cooler.ShouldBe(CoolerIntentKind.Cool);
        camera.CoolerSetpointC.ShouldBe(-10);
        camera.Reconnected.ShouldBe(true, camera.ReconnectError);

        // Reconnected and cooling back through the session's ramp, on the new node's own driver.
        second.HoldsHardware.ShouldBeTrue();
        (await AlpacaGetBoolAsync(kept, "connected", ct)).ShouldBeTrue();
        (await AlpacaGetBoolAsync(kept, "cooleron", ct)).ShouldBeTrue("the ramp's first step is taken at once");
        var recovered = await UntilTheJournalAsync(journalPath, j => j.ProcessId == second.ProcessId && j.Touching is null
            && j.Devices.Any(static d => d.Cooler is CoolerIntentKind.Cool), ct);
        recovered.Crashes.Length.ShouldBe(1, "the crash it recovered from, for the guard");

        // Stopped cleanly, holding nothing: the report was seen, and the journal goes with the node.
        (await kept.Http.PostAsync("api/v1/node/shutdown", content: null, ct)).IsSuccessStatusCode.ShouldBeTrue();
        (await kept.KeeperExitAsync(ct)).ShouldBe(NodeExitCodes.Stopped);
        File.Exists(journalPath).ShouldBeFalse("a node that stopped cleanly leaves no journal");
    }

    [Fact(Timeout = 180_000)]
    public async Task ASecondCrashWithinMinutesIsALoopAndTheNextNodeReconnectsNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var kept = await KeptNode.StartAsync(ct, dataRoot => SeedProfileAsync(dataRoot, ct));
        var first = await kept.WaitForNodeAsync(static _ => true, ct);
        (await kept.Client.SetActiveProfileAsync(ProfileId, ct)).IsSuccess.ShouldBeTrue();
        await AlpacaPutAsync(kept, "connected", "Connected", "true", ct);
        var journalPath = NodeJournal.PathFor(kept.SocketPath);
        await UntilTheJournalAsync(journalPath, static j => j.Devices.Length == 1, ct);

        // Crashes once: the next node reconnects the camera. Crashes again within minutes: its keeper leaves it down.
        using (var crashing = Process.GetProcessById(first.ProcessId))
        {
            crashing.Kill();
        }
        var second = await kept.WaitForNodeAsync(node => node.ProcessId != first.ProcessId
            && node.Recovery is { } recovery && recovery.Devices.All(static d => d.Reconnected == true), ct);
        await UntilTheJournalAsync(journalPath, j => j.ProcessId == second.ProcessId && j.Devices.Length == 1, ct);
        using (var crashing = Process.GetProcessById(second.ProcessId))
        {
            crashing.Kill();
        }
        (await kept.KeeperExitAsync(ct)).ShouldBe(NodeExitCodes.CrashLoop);

        // The next client starts another keeper: its node finds two crashes within the window and reconnects nothing.
        await using var next = await kept.StartAnotherKeeperAsync(ct);
        var third = await next.WaitForNodeAsync(static node => node.Recovery is not null, ct);

        var report = third.Recovery.ShouldNotBeNull();
        report.CrashLoop.ShouldBeTrue();
        report.Devices.ShouldHaveSingleItem().Reconnected.ShouldBeNull("a crash loop reconnects nothing");
        third.HoldsHardware.ShouldBeFalse();
    }

    [Fact(Timeout = 120_000)]
    public async Task ANodeStoppedCleanlyWithACameraConnectedLeavesNoJournal()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var kept = await KeptNode.StartAsync(ct, dataRoot => SeedProfileAsync(dataRoot, ct));
        await kept.WaitForNodeAsync(static _ => true, ct);
        (await kept.Client.SetActiveProfileAsync(ProfileId, ct)).IsSuccess.ShouldBeTrue();
        await AlpacaPutAsync(kept, "connected", "Connected", "true", ct);
        var journalPath = NodeJournal.PathFor(kept.SocketPath);
        await UntilTheJournalAsync(journalPath, static j => j.Devices.Length == 1, ct);

        (await kept.Http.PostAsync("api/v1/node/shutdown", content: null, ct)).IsSuccessStatusCode.ShouldBeTrue();

        (await kept.KeeperExitAsync(ct)).ShouldBe(NodeExitCodes.Stopped);
        File.Exists(journalPath).ShouldBeFalse();
    }
}
