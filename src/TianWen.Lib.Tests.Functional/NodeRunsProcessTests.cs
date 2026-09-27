using Shouldly;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Hosting.Dto;
using TianWen.Lib.Devices;
using TianWen.Lib.Sequencing;
using TianWen.RemoteClient;
using Xunit;
using static TianWen.Lib.Tests.Functional.NodeWait;

namespace TianWen.Lib.Tests.Functional;

/// <summary>
/// The proof of P5 (docs/plans/hardware-in-the-server.md, #934): each kind of run over a fake rig through the SOCKET of a
/// spawned node (<see cref="KeptNode"/>, <c>--fake-devices --local-only</c> and a 5 s detach grace), with its client gone
/// mid-run. A session and a dark library go on. Polar alignment and a planetary live view stop cleanly once the grace is
/// spent, and a client back within the grace keeps them.
/// </summary>
[Collection("NodeProcesses")]
public class NodeRunsProcessTests(ITestOutputHelper output)
{
    /// <summary>Long enough for a window to come back inside it, short enough to wait out.</summary>
    private static readonly TimeSpan Grace = TimeSpan.FromSeconds(5);
    private static readonly Guid ProfileId = Guid.Parse("7e57ab1e-0b0e-4e5d-9a5e-000000000934");
    private static readonly Uri Mount = new Uri("Mount://FakeDevice/FakeMount1?latitude=48.2&longitude=16.3");
    private static readonly Uri Camera = new Uri("Camera://FakeDevice/FakeCamera1");
    private static readonly Uri Focuser = new Uri("Focuser://FakeDevice/FakeFocuser1");
    private static readonly Uri Wheel = new Uri("FilterWheel://FakeDevice/FakeFilterWheel1");

    /// <summary>A winter night at the rig's site, so a session has a dark to run in.</summary>
    private static readonly DateTimeOffset Night = new DateTimeOffset(2026, 1, 15, 22, 0, 0, TimeSpan.FromHours(1));

    private static Task SeedProfileAsync(string dataRoot, CancellationToken ct)
    {
        var profiles = Directory.CreateDirectory(Path.Combine(dataRoot, "Profiles")).FullName;
        var data = new ProfileData(
            Mount: Mount,
            // A session needs a guider; the fake one simulates guiding on its own.
            Guider: new Devices.Fake.FakeDevice(DeviceType.Guider, 1).DeviceUri,
            OTAs: [new OTAData("OTA 1", 1000, Camera: Camera, Cover: null, Focuser: Focuser, FilterWheel: Wheel,
                PreferOutwardFocus: null, OutwardIsPositive: null)],
            SiteLatitude: 48.2,
            SiteLongitude: 16.3);
        return File.WriteAllTextAsync(Path.Combine(profiles, Profile.DeviceIdFromUUID(ProfileId) + ".json"),
            JsonSerializer.Serialize(new ProfileDto(ProfileId, "Node runs", data), Profile.ProfileJsonSerializerContextIndented.ProfileDto), ct);
    }

    /// <summary>A spawned node on the rig, with the grace short enough to wait out, its devices connected.</summary>
    private static async Task<KeptNode> RigAsync(CancellationToken ct)
    {
        var kept = await KeptNode.StartAsync(ct, dataRoot => SeedProfileAsync(dataRoot, ct),
            arguments: ["--detach-grace", Grace.TotalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)],
            environment: new Dictionary<string, string> { [StartupTimeOverride.EnvVarName] = Night.ToString("O") });
        try
        {
            var client = kept.Client;
            (await client.SetActiveProfileAsync(ProfileId, ct)).IsSuccess.ShouldBeTrue();
            await SucceedsAsync(client, await client.StartDiscoveryAsync(ct), ct);
            foreach (var device in new[] { Mount, Camera })
            {
                await SucceedsAsync(client, await client.ConnectDeviceAsync(device, ct), ct);
            }
            return kept;
        }
        catch
        {
            await kept.DisposeAsync();
            throw;
        }
    }

    private static async Task SucceedsAsync(TianWenNodeClient client, NodeResult<JobDto> started, CancellationToken ct)
    {
        var job = started.Value.ShouldNotBeNull(started.Error);
        var ended = await UntilAsync<JobDto>($"{job.Kind} job {job.Id} to end", async token =>
        {
            var now = (await client.GetJobAsync(job.Id, token)).Value;
            return (now is { State: not JobState.Running } ? now : null, now is null ? "not found" : $"{now.State}: {now.Step}");
        }, ct);
        ended.State.ShouldBe(JobState.Succeeded, $"{ended.Kind}: {ended.Error}");
    }

    /// <summary>A client window over the node's socket, beating from the moment it has connected.</summary>
    private async Task<NodeWindow> WindowAsync(KeptNode kept, CancellationToken ct)
    {
        var window = NodeWindow.Open(NodeTransport.OverSocket(kept.SocketPath), output, ct);
        await window.UntilConnectedAsync(ct);
        // The node counts a client present from its first beat, one interval after it connects.
        await Task.Delay(Hosting.Api.NodeWire.PresenceBeatInterval * 2, ct);
        return window;
    }

    /// <summary>
    /// Until the node counts no client attached: a window's socket closes as it goes, and from then the node's grace
    /// clock runs. Every step that depends on the grace starts from here rather than from the dispose.
    /// </summary>
    private static Task UntilNobodyIsAttachedAsync(TianWenNodeClient client, CancellationToken ct)
        => UntilAsync<NodeInfoDto>("the node to see its last client go", async token =>
        {
            var node = (await client.GetNodeAsync(token)).Value;
            return (node is { ClientsAttached: 0 } ? node : null, node is null ? "no answer" : $"{node.ClientsAttached} attached");
        }, ct);

    private static async Task<IEnumerable<string>> NotesAsync(TianWenNodeClient client, CancellationToken ct)
        => ((await client.GetNotificationsAsync(ct)).Value ?? []).Select(n => n.Message);

    [Fact(Timeout = 240_000)]
    public async Task AnInteractiveRunStopsAGraceAfterItsLastClientGoesAndAClientBackWithinItKeepsIt()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var kept = await RigAsync(ct);
        var client = kept.Client;

        // A planetary live view, its window gone and another back within the grace: it goes on.
        var first = await WindowAsync(kept, ct);
        (await client.StartPlanetaryAsync(new PlanetaryRequestDto(), ct)).IsSuccess.ShouldBeTrue();
        await UntilAsync<PlanetaryStateDto>("frames to arrive", async token =>
        {
            var state = (await client.GetPlanetaryAsync(token)).Value;
            return (state is { FramesReceived: > 0 } ? state : null, state is null ? "none" : $"{state.FramesReceived} frames");
        }, ct);
        await first.DisposeAsync();
        await UntilNobodyIsAttachedAsync(client, ct);
        var second = await WindowAsync(kept, ct);
        await Task.Delay(Grace * 2, ct);
        (await client.GetPlanetaryAsync(ct)).Value.ShouldNotBeNull().Running.ShouldBeTrue("a client came back within the grace");

        // Its last window goes: it stops once the grace is spent, and says so.
        await second.DisposeAsync();
        var goneAt = Stopwatch.StartNew();
        var planetary = await UntilAsync<PlanetaryStateDto>("the planetary capture to stop", async token =>
        {
            var state = (await client.GetPlanetaryAsync(token)).Value;
            return (state is { Running: false } ? state : null, state is null ? "none" : $"running {state.Running}");
        }, ct);
        goneAt.Elapsed.ShouldBeGreaterThanOrEqualTo(Grace, "the grace is whole before it stops");
        planetary.FailureReason.ShouldBeNull("the grace ends it cleanly");
        // The message names the grace, which is what says the node took the one it was started with.
        (await NotesAsync(client, ct)).ShouldContain($"The planetary capture stopped: no client has watched it for {Grace.TotalSeconds:0} s");

        // Polar alignment, likewise: rotating the mount for nobody is the case the grace is for.
        var third = await WindowAsync(kept, ct);
        var polar = new PolarAlignmentRequestDto
        {
            Configuration = new PolarAlignmentConfigDto { ExposureRampSeconds = [.. Enumerable.Repeat(1.0, 8)], ReferenceFrameAverages = 1 },
        };
        (await client.StartPolarAlignmentAsync(polar, ct)).IsSuccess.ShouldBeTrue();
        await third.DisposeAsync();
        goneAt.Restart();
        var aligned = await UntilAsync<PolarStateDto>("polar alignment to end", async token =>
        {
            var state = (await client.GetPolarAlignmentAsync(token)).Value;
            return (state is { Running: false } ? state : null, state is null ? "none" : $"{state.Phase}: {state.StatusMessage}");
        }, ct);
        goneAt.Elapsed.ShouldBeGreaterThanOrEqualTo(Grace);
        aligned.FailureReason.ShouldBeNull("stopped by the grace, the mount restored, not failed");
        (await NotesAsync(client, ct)).ShouldContain($"Polar alignment stopped: no client has watched it for {Grace.TotalSeconds:0} s");
    }

    [Fact(Timeout = 240_000)]
    public async Task ASessionAndADarkLibraryGoOnWithTheirLastClientGone()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var kept = await RigAsync(ct);
        var client = kept.Client;

        // A dark library: its window goes at once, and it takes every frame regardless. It lasts well past a grace
        // (six 2 s frames), or an interactive dark library would finish before the grace could stop it.
        var window = await WindowAsync(kept, ct);
        (await client.StartDarkLibraryAsync(new DarkLibraryRequestDto { DeviceUri = Camera.ToString(), ExposureSeconds = 2, Count = 6 }, ct))
            .IsSuccess.ShouldBeTrue();
        await window.DisposeAsync();
        var darks = await UntilAsync<DarkLibraryStateDto>("the dark library to end", async token =>
        {
            var state = (await client.GetDarkLibraryAsync(token)).Value;
            return (state is { Running: false } ? state : null, state is null ? "none" : $"{state.Frames.Length} of {state.Count}");
        }, ct);
        (darks.Frames.Length, darks.Stopped, darks.FailureReason).ShouldBe((6, false, (string?)null), "a dark library finishes unwatched");

        // A session: on for twice the grace after its window has gone, until it is aborted.
        window = await WindowAsync(kept, ct);
        (await client.SetScheduleAsync([new ScheduledObservationDto
        {
            TargetName = "M 42", TargetRA = 5.588, TargetDec = -5.391, Start = Night, DurationMinutes = 60, AcrossMeridian = false,
        }], ct)).IsSuccess.ShouldBeTrue();
        var started = await client.StartSessionAsync(ProfileId, configuration: null, ct);
        started.IsSuccess.ShouldBeTrue(started.Error);
        await window.DisposeAsync();
        await UntilNobodyIsAttachedAsync(client, ct);
        await Task.Delay(Grace * 2, ct);

        var state = await client.GetSessionStateAsync(ct);
        var session = state.Value.ShouldNotBeNull(state.Error);
        session.Phase.ShouldNotBeOneOf([SessionPhase.Failed, SessionPhase.Aborted, SessionPhase.Complete], session.FailureReason);
        (await client.GetNodeAsync(ct)).Value.ShouldNotBeNull().Run.ShouldNotBeNull().Kind.ShouldBe(NodeRunKind.Session, "a session goes on");
        (await client.AbortSessionAsync(ct)).IsSuccess.ShouldBeTrue();
        await UntilAsync<NodeInfoDto>("the session to end", async token =>
        {
            var node = (await client.GetNodeAsync(token)).Value;
            return (node is { Run: null } ? node : null, node?.Run is { } run ? $"{run.Kind} running" : "none");
        }, ct);
    }
}
