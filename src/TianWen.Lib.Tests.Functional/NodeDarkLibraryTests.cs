using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Hosting;
using TianWen.Hosting.Dto;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Fake;
using TianWen.Lib.Imaging;
using TianWen.RemoteClient;
using Xunit;
using static TianWen.Lib.Tests.Functional.NodeWait;

namespace TianWen.Lib.Tests.Functional;

/// <summary>
/// A dark library as the node's run (P5 part 1 of docs/plans/hardware-in-the-server.md, #934): the CLI's <c>darks</c>, run
/// by the node on its own token as a session is, one run at a time with it. The run holds its camera for as long as it
/// goes on, gives it back as it ends however it ends, names itself when it refuses anything, and is journaled by kind.
/// </summary>
[Collection("Hosting")]
public class NodeDarkLibraryTests(ITestOutputHelper outputHelper)
{
    private static TianWenNodeClient ClientOf(NodeHarness node) => new TianWenNodeClient(node.Client);

    private static async Task<FakeDevice> ConnectedCameraAsync(NodeHarness node, CancellationToken ct)
    {
        var device = new FakeDevice(DeviceType.Camera, 1);
        await node.App.Services.GetRequiredService<IDeviceHub>().ConnectAsync(device, ct);
        return device;
    }

    private static DarkLibraryRequestDto Request(FakeDevice camera, int count, bool bias = false) => new DarkLibraryRequestDto
    {
        DeviceUri = camera.DeviceUri.ToString(),
        ExposureSeconds = 0.05,
        Count = count,
        Bias = bias,
    };

    private static Task<DarkLibraryStateDto> UntilAsync(NodeHarness node, string waitingFor, Func<DarkLibraryStateDto, bool> holds, CancellationToken ct) =>
        UntilAsync<DarkLibraryStateDto>(waitingFor, async token =>
        {
            var state = (await ClientOf(node).GetDarkLibraryAsync(token)).Value;
            return (state is not null && holds(state) ? state : null,
                state is null ? "no dark library" : $"{state.Frames.Length} of {state.Count}, {(state.Running ? "running" : "ended")}");
        }, ct);

    /// <summary>The envelope's error text, whatever the HTTP status says.</summary>
    private static async Task<(int Status, string? Error)> EnvelopeAsync(Task<HttpResponseMessage> request, CancellationToken ct)
    {
        using var response = await request;
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return (body.RootElement.GetProperty("statusCode").GetInt32(),
            body.RootElement.TryGetProperty("error", out var error) && error.ValueKind is JsonValueKind.String ? error.GetString() : null);
    }

    [Theory(Timeout = 60_000)]
    [InlineData(false, "Darks", FrameType.Dark)]
    [InlineData(true, "Bias", FrameType.Bias)]
    public async Task ADarkLibraryRunsOnTheNodeAndWritesEachFrameLabelledAsWhatItIs(bool bias, string folder, FrameType expected)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var node = await NodeHarness.StartAsync(outputHelper, ct);
        // The test double writes only its first frame to disk unless told otherwise; each of these is read back.
        ((FakeExternal)node.External).MaxFitsWrites = 3;
        var camera = await ConnectedCameraAsync(node, ct);

        var started = await ClientOf(node).StartDarkLibraryAsync(Request(camera, count: 3, bias), ct);
        started.IsSuccess.ShouldBeTrue(started.Error);

        var ended = await UntilAsync(node, "the dark library to end", static state => !state.Running, ct);
        ended.FailureReason.ShouldBeNull();
        ended.Stopped.ShouldBeFalse();
        ended.Frames.Length.ShouldBe(3);
        foreach (var frame in ended.Frames)
        {
            Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(frame.Path))).ShouldBe(folder);
            Image.TryReadFitsHeader(frame.Path, out var header).ShouldBeTrue(frame.Path);
            header.FrameType.ShouldBe(expected, "what a stacker matches a calibration frame on");
        }

        var settings = await ClientOf(node).SetCameraSettingsAsync(new CameraSettingsRequestDto { DeviceUri = camera.DeviceUri.ToString(), Bin = 1 }, ct);
        settings.IsSuccess.ShouldBeTrue($"the camera goes back as the library ends: {settings.Error}");
    }

    [Fact(Timeout = 60_000)]
    public async Task WhileItRunsTheCameraAndTheNodeAreItsAndOnlyItsOwnStopEndsIt()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var node = await NodeHarness.StartAsync(outputHelper, ct);
        var client = ClientOf(node);
        var camera = await ConnectedCameraAsync(node, ct);

        (await client.StartDarkLibraryAsync(Request(camera, count: 10_000), ct)).IsSuccess.ShouldBeTrue();
        await UntilAsync(node, "the first frame", static state => state.Frames.Length > 0, ct);

        (await client.GetNodeAsync(ct)).Value.ShouldNotBeNull().Run.ShouldNotBeNull().Kind.ShouldBe(NodeRunKind.Darks);

        var settings = await client.SetCameraSettingsAsync(new CameraSettingsRequestDto { DeviceUri = camera.DeviceUri.ToString(), Bin = 1 }, ct);
        settings.StatusCode.ShouldBe(409);
        settings.Error.ShouldNotBeNull().ShouldContain(NodeDarkLibrary.LeaseOwner);

        var second = await client.StartDarkLibraryAsync(Request(camera, count: 1), ct);
        second.StatusCode.ShouldBe(409);
        second.Error.ShouldNotBeNull().ShouldContain("dark library");

        var session = await EnvelopeAsync(node.Client.PostAsync($"/api/v1/session/start?profileId={NodeHarness.ProfileId}", null, ct), ct);
        session.Status.ShouldBe(409);
        session.Error.ShouldNotBeNull().ShouldContain("dark library", customMessage: "a refusal names the run going on, not a session");

        (await EnvelopeAsync(node.Client.PostAsync("/api/v1/session/abort", null, ct), ct)).Status.ShouldBe(404, "a session's abort never cuts a dark library short");
        (await client.GetDarkLibraryAsync(ct)).Value.ShouldNotBeNull().Running.ShouldBeTrue();

        var stopped = (await client.StopDarkLibraryAsync(ct)).Value.ShouldNotBeNull();
        stopped.Running.ShouldBeFalse();
        stopped.Stopped.ShouldBeTrue();
        stopped.Frames.Length.ShouldBeLessThan(stopped.Count);

        (await client.GetNodeAsync(ct)).Value.ShouldNotBeNull().Run.ShouldBeNull();
        (await client.SetCameraSettingsAsync(new CameraSettingsRequestDto { DeviceUri = camera.DeviceUri.ToString(), Bin = 1 }, ct))
            .IsSuccess.ShouldBeTrue("a stopped library gives its camera back");
        (await client.StopDarkLibraryAsync(ct)).StatusCode.ShouldBe(404);
    }

    [Fact(Timeout = 60_000)]
    public async Task ACameraNotConnectedOrAtWorkOnAJobIsRefusedBeforeAnythingStarts()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var node = await NodeHarness.StartAsync(outputHelper, ct);
        var client = ClientOf(node);
        var camera = new FakeDevice(DeviceType.Camera, 1);

        (await client.StartDarkLibraryAsync(Request(camera, count: 1), ct)).StatusCode.ShouldBe(404);

        await node.App.Services.GetRequiredService<IDeviceHub>().ConnectAsync(camera, ct);
        var cooling = (await client.CoolCameraAsync(camera.DeviceUri, -10, rampMinutes: null, ct)).Value.ShouldNotBeNull();

        var refused = await client.StartDarkLibraryAsync(Request(camera, count: 1), ct);
        refused.StatusCode.ShouldBe(409);
        refused.Error.ShouldNotBeNull().ShouldContain(cooling.Id);
        node.Node.IsRunning.ShouldBeFalse();

        (await client.CancelJobAsync(cooling.Id, ct)).IsSuccess.ShouldBeTrue();
    }

    [Fact(Timeout = 60_000)]
    public async Task TheJournalRecordsADarkLibraryByItsKind()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = Path.Combine(Directory.CreateTempSubdirectory("twj").FullName, "node.journal");
        await using var node = await NodeHarness.StartAsync(outputHelper, ct,
            services => services.AddSingleton(new NodeJournalOptions(path, null, static () => null, TimeProvider.System)));
        var camera = await ConnectedCameraAsync(node, ct);

        (await ClientOf(node).StartDarkLibraryAsync(Request(camera, count: 10_000), ct)).IsSuccess.ShouldBeTrue();

        var journal = await UntilTheJournalAsync(path, static j => j.Run is not null, ct);
        journal.Run.ShouldNotBeNull().Kind.ShouldBe(NodeRunKind.Darks, "the next node reports which run a crash interrupted");
        journal.Run.Target.ShouldBeNull();

        (await ClientOf(node).StopDarkLibraryAsync(ct)).IsSuccess.ShouldBeTrue();
    }
}
