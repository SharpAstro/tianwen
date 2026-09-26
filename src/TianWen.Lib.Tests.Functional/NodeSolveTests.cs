using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using System;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Hosting;
using TianWen.Hosting.Dto;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.PlateSolve;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Fake;
using TianWen.Lib.Imaging;
using TianWen.RemoteClient;
using Xunit;
using static TianWen.Lib.Tests.Functional.NodeWait;

namespace TianWen.Lib.Tests.Functional;

/// <summary>
/// A plate solve and a solve and sync as the node's jobs (P5 part 3 of docs/plans/hardware-in-the-server.md, #934). A job
/// carries no result of its own, so a solve's lives at <c>/preview/ota/{index}/solution</c>, with the token of the frame it
/// belongs to; a solve and sync's frame becomes the OTA's, and the job succeeds only when the mount synced. The solver is a
/// stand-in answering at once: what is under test is the node's plumbing, and a real blind solve of the fake camera's
/// random field runs for minutes.
/// </summary>
[Collection("Hosting")]
public class NodeSolveTests(ITestOutputHelper outputHelper)
{
    private static readonly FakeDevice Camera = new FakeDevice(DeviceType.Camera, 1);
    private static readonly FakeDevice Mount = new FakeDevice(DeviceType.Mount, 1);

    /// <summary>What a stand-in solve finds: a field near Orion, with a scale and a small rotation.</summary>
    private static readonly WCS Field = new WCS(5.5, -5.4) { CRPix1 = 2071.5, CRPix2 = 1410.5, CD1_1 = -2.1e-4, CD1_2 = 3.0e-6, CD2_1 = -2.9e-6, CD2_2 = -2.1e-4 };

    private Task<NodeHarness> NodeSolvingAsync(WCS? answer, CancellationToken ct) =>
        NodeHarness.StartAsync(outputHelper, ct, services => services.AddSingleton<IPlateSolverFactory>(StandInSolver.Answering(answer)));

    private static TianWenNodeClient ClientOf(NodeHarness node) => new TianWenNodeClient(node.Client);

    private static Task ActiveRigAsync(NodeHarness node, bool withMount, CancellationToken ct) => node.ActivateRigAsync(Camera, withMount ? Mount : null, ct);

    private static Task<JobDto> UntilEndedAsync(TianWenNodeClient client, string id, CancellationToken ct) =>
        UntilAsync<JobDto>($"job {id} to end", async token =>
        {
            var job = (await client.GetJobAsync(id, token)).Value;
            return (job is { State: not JobState.Running } ? job : null, job is null ? "not found" : $"{job.State}: {job.Step}");
        }, ct);

    [Fact(Timeout = 60_000)]
    public async Task ASolveIsAJobAndItsSolutionLivesWithTheFrameItIsOf()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var node = await NodeSolvingAsync(Field, ct);
        await ActiveRigAsync(node, withMount: false, ct);
        var client = ClientOf(node);

        (await client.StartSolveAsync(0, ct)).StatusCode.ShouldBe(404, "no frame to solve yet");
        (await client.GetSolutionAsync(0, ct)).StatusCode.ShouldBe(404, "nothing solved yet");

        var preview = (await client.StartPreviewExposureAsync(0, new PreviewExposureRequestDto { ExposureSeconds = 0.1 }, ct)).Value.ShouldNotBeNull();
        (await UntilEndedAsync(client, preview.Id, ct)).State.ShouldBe(JobState.Succeeded);
        var frame = await client.GetLatestFrameAsync(FrameSources.Ota(0), after: null, new FrameReader(), ct);
        var number = frame.FrameNumber.ShouldNotBeNull();
        frame.Image.ShouldNotBeNull().Release();

        var solving = (await client.StartSolveAsync(0, ct)).Value.ShouldNotBeNull();
        solving.Kind.ShouldBe(NodePreviews.SolveJob);
        var solved = await UntilEndedAsync(client, solving.Id, ct);
        solved.State.ShouldBe(JobState.Succeeded);

        var solution = (await client.GetSolutionAsync(0, ct)).Value.ShouldNotBeNull();
        solution.FrameNumber.ShouldBe(number, "the solution names the frame it is of");
        solution.Solved.ShouldBeTrue();
        solution.Message.ShouldBe(solved.Step);
        var wcs = solution.Solution.ShouldNotBeNull().ToWcs();
        (wcs.CenterRA, wcs.CenterDec, wcs.CD1_1).ShouldBe((Field.CenterRA, Field.CenterDec, Field.CD1_1));
    }

    [Fact(Timeout = 60_000)]
    public async Task ASolveAndSyncSyncsTheMountAndItsFrameAndSolutionAreTheOtas()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var node = await NodeSolvingAsync(Field, ct);
        await ActiveRigAsync(node, withMount: true, ct);
        var client = ClientOf(node);

        var job = (await client.StartSolveSyncAsync(0, new PreviewExposureRequestDto { ExposureSeconds = 2 }, ct)).Value.ShouldNotBeNull();
        job.Kind.ShouldBe(NodePreviews.SolveSyncJob);
        job.DeviceUri.ShouldBe(Mount.DeviceUri.ToString());

        // While it runs the camera is its, as the mount is: a command to it is refused naming it.
        var settings = await client.SetCameraSettingsAsync(new CameraSettingsRequestDto { DeviceUri = Camera.DeviceUri.ToString(), Bin = 1 }, ct);
        settings.StatusCode.ShouldBe(409);
        settings.Error.ShouldNotBeNull().ShouldContain(NodePreviews.SolveSyncLeaseOwner);

        var ended = await UntilEndedAsync(client, job.Id, ct);
        ended.State.ShouldBe(JobState.Succeeded, ended.Error);
        ended.Step.ShouldNotBeNull().ShouldStartWith("Synced");

        var frame = await client.GetLatestFrameAsync(FrameSources.Ota(0), after: null, new FrameReader(), ct);
        var number = frame.FrameNumber.ShouldNotBeNull();
        frame.Image.ShouldNotBeNull("the frame it took is the OTA's").Release();
        var solution = (await client.GetSolutionAsync(0, ct)).Value.ShouldNotBeNull();
        (solution.FrameNumber, solution.Solved).ShouldBe((number, true));

        (await client.SetCameraSettingsAsync(new CameraSettingsRequestDto { DeviceUri = Camera.DeviceUri.ToString(), Bin = 1 }, ct))
            .IsSuccess.ShouldBeTrue("the camera is given back as the job ends");
    }

    [Fact(Timeout = 60_000)]
    public async Task ASolveAndSyncThatFindsNothingFailsWithTheOutcomesOwnWordsAndKeepsItsFrame()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var node = await NodeSolvingAsync(answer: null, ct);
        await ActiveRigAsync(node, withMount: true, ct);
        var client = ClientOf(node);

        var job = (await client.StartSolveSyncAsync(0, new PreviewExposureRequestDto { ExposureSeconds = 0.1 }, ct)).Value.ShouldNotBeNull();
        var ended = await UntilEndedAsync(client, job.Id, ct);

        ended.State.ShouldBe(JobState.Failed, "a solve that found nothing synced nothing");
        ended.Error.ShouldNotBeNull().ShouldContain("no match");
        var frame = await client.GetLatestFrameAsync(FrameSources.Ota(0), after: null, new FrameReader(), ct);
        frame.Image.ShouldNotBeNull("the frame it took is still the OTA's").Release();
        (await client.GetSolutionAsync(0, ct)).Value.ShouldNotBeNull().Solved.ShouldBeFalse();
    }

    [Fact(Timeout = 60_000)]
    public async Task ASolveAndSyncIsRefusedWithoutAMountToSyncAndWhileASessionRuns()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var node = await NodeSolvingAsync(Field, ct);
        var client = ClientOf(node);
        var request = new PreviewExposureRequestDto { ExposureSeconds = 0.1 };

        await ActiveRigAsync(node, withMount: false, ct);
        var noMount = await client.StartSolveSyncAsync(0, request, ct);
        noMount.StatusCode.ShouldBe(409);
        noMount.Error.ShouldNotBeNull().ShouldContain("no mount");

        // A rig that could solve and sync, but a session holds the node.
        await ActiveRigAsync(node, withMount: true, ct);
        node.Factory.Initialised.TrySetResult();
        await node.StartSessionAsync(ct);
        var running = await client.StartSolveSyncAsync(0, request, ct);
        running.StatusCode.ShouldBe(409);
        running.Error.ShouldNotBeNull().ShouldContain("session");
    }
}
