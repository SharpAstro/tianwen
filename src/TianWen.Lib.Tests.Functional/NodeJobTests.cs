using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Hosting.Dto;
using TianWen.Lib.Devices;
using TianWen.RemoteClient;
using Xunit;

namespace TianWen.Lib.Tests.Functional;

/// <summary>
/// A slow operation is the node's JOB, and discovery is the first (P0b item 17 of
/// docs/plans/hardware-in-the-server.md, #752). <c>/devices/discover</c> ran a full discovery inline on the
/// request's token: a client's 10 s control budget cut it off mid-probe, and a dropped request cancelled a serial
/// probe half-way. Starting one answers 202 with a job now, the discovery runs on the node's token,
/// <c>GET /jobs/{id}</c> says how it stands, <c>DELETE /jobs/{id}</c> cancels it, and <c>JOB-PROGRESS</c> tells
/// a listening client.
/// </summary>
[Collection("Hosting")]
public class NodeJobTests(ITestOutputHelper outputHelper)
{
    /// <summary>A discovery that runs until the test lets it end, and remembers the token it ran on.</summary>
    private sealed class HeldDiscovery : IDeviceDiscovery
    {
        private int _runs;

        public TaskCompletionSource Release { get; } = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<CancellationToken> Began { get; } = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Runs => Volatile.Read(ref _runs);

        public async ValueTask DiscoverAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _runs);
            Began.TrySetResult(cancellationToken);
            await Release.Task.WaitAsync(cancellationToken);
        }

        public ValueTask DiscoverOnlyDeviceType(DeviceType type, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public IEnumerable<DeviceType> RegisteredDeviceTypes => [];

        public IEnumerable<DeviceBase> RegisteredDevices(DeviceType deviceType) => [];

        public ValueTask<bool> CheckSupportAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(true);
    }

    private async Task<(NodeHarness Node, HeldDiscovery Discovery)> StartAsync(CancellationToken ct)
    {
        var discovery = new HeldDiscovery();
        var node = await NodeHarness.StartAsync(outputHelper, ct, services => services.AddSingleton<IDeviceDiscovery>(discovery));
        return (node, discovery);
    }

    private static async Task<(HttpStatusCode Status, JsonElement Job)> SendAsync(HttpClient client, HttpMethod method, string path, CancellationToken ct)
    {
        using var response = await client.SendAsync(new HttpRequestMessage(method, path), ct);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return (response.StatusCode, body.RootElement.GetProperty("response").Clone());
    }

    private static async Task<JsonElement> UntilStateAsync(HttpClient client, string id, JobState state, CancellationToken ct)
    {
        while (true)
        {
            var (status, job) = await SendAsync(client, HttpMethod.Get, $"/api/v1/jobs/{id}", ct);
            status.ShouldBe(HttpStatusCode.OK);
            if (job.GetProperty("state").GetInt32() == (int)state)
            {
                return job;
            }
            await Task.Delay(20, ct);
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task ADiscoveryAnswersAtOnceWithAJobThatFinishesInTheNode()
    {
        var ct = TestContext.Current.CancellationToken;
        var (node, discovery) = await StartAsync(ct);
        await using var host = node;

        var (status, job) = await SendAsync(node.Client, HttpMethod.Post, "/api/v1/devices/discover", ct);

        status.ShouldBe(HttpStatusCode.Accepted, "the request answers before the discovery ends");
        job.GetProperty("kind").GetString().ShouldBe("discover");
        job.GetProperty("state").GetInt32().ShouldBe((int)JobState.Running);
        var id = job.GetProperty("id").GetString().ShouldNotBeNull();
        await discovery.Began.Task.WaitAsync(ct);

        (await UntilStateAsync(node.Client, id, JobState.Running, ct)).TryGetProperty("endedUtc", out _).ShouldBeFalse("it has not ended");
        discovery.Release.SetResult();

        var ended = await UntilStateAsync(node.Client, id, JobState.Succeeded, ct);
        ended.TryGetProperty("endedUtc", out _).ShouldBeTrue();
    }

    [Fact(Timeout = 30_000)]
    public async Task TheDiscoveryRunsOnTheNodesTokenSoTheRequestGoingAwayLeavesItRunning()
    {
        var ct = TestContext.Current.CancellationToken;
        var (node, discovery) = await StartAsync(ct);
        await using var host = node;

        // A client of its own, closed once it has its answer: its connection, and every token tied to it, go.
        string id;
        using (var client = new HttpClient { BaseAddress = node.Client.BaseAddress })
        {
            (_, var job) = await SendAsync(client, HttpMethod.Post, "/api/v1/devices/discover", ct);
            id = job.GetProperty("id").GetString().ShouldNotBeNull();
        }

        var token = await discovery.Began.Task.WaitAsync(ct);
        await Task.Delay(200, ct);
        token.IsCancellationRequested.ShouldBeFalse("the discovery ran on the request's token");

        discovery.Release.SetResult();
        await UntilStateAsync(node.Client, id, JobState.Succeeded, ct);
    }

    [Fact(Timeout = 30_000)]
    public async Task DeletingAJobCancelsIt()
    {
        var ct = TestContext.Current.CancellationToken;
        var (node, discovery) = await StartAsync(ct);
        await using var host = node;
        (_, var job) = await SendAsync(node.Client, HttpMethod.Post, "/api/v1/devices/discover", ct);
        var id = job.GetProperty("id").GetString().ShouldNotBeNull();
        var token = await discovery.Began.Task.WaitAsync(ct);

        (await SendAsync(node.Client, HttpMethod.Delete, $"/api/v1/jobs/{id}", ct)).Status.ShouldBe(HttpStatusCode.OK);

        token.IsCancellationRequested.ShouldBeTrue("the discovery was not asked to stop");
        await UntilStateAsync(node.Client, id, JobState.Cancelled, ct);
    }

    [Fact(Timeout = 30_000)]
    public async Task ASecondDiscoveryWhileOneRunsJoinsIt()
    {
        var ct = TestContext.Current.CancellationToken;
        var (node, discovery) = await StartAsync(ct);
        await using var host = node;

        (_, var first) = await SendAsync(node.Client, HttpMethod.Post, "/api/v1/devices/discover", ct);
        await discovery.Began.Task.WaitAsync(ct);
        (var status, var second) = await SendAsync(node.Client, HttpMethod.Post, "/api/v1/devices/discover", ct);

        status.ShouldBe(HttpStatusCode.Accepted);
        second.GetProperty("id").GetString().ShouldBe(first.GetProperty("id").GetString(), "a discovery is one sweep of the ports; a second would fight the first for them");
        discovery.Runs.ShouldBe(1);
        discovery.Release.SetResult();
    }

    [Fact(Timeout = 30_000)]
    public async Task AFailedDiscoverySaysWhy()
    {
        var ct = TestContext.Current.CancellationToken;
        var (node, discovery) = await StartAsync(ct);
        await using var host = node;
        (_, var job) = await SendAsync(node.Client, HttpMethod.Post, "/api/v1/devices/discover", ct);
        var id = job.GetProperty("id").GetString().ShouldNotBeNull();
        await discovery.Began.Task.WaitAsync(ct);

        discovery.Release.SetException(new InvalidOperationException("the serial sweep failed"));

        (await UntilStateAsync(node.Client, id, JobState.Failed, ct)).GetProperty("error").GetString().ShouldBe("the serial sweep failed");
    }

    [Fact(Timeout = 30_000)]
    public async Task AJobNobodyStartedIsNotFound()
    {
        var ct = TestContext.Current.CancellationToken;
        var (node, _) = await StartAsync(ct);
        await using var host = node;

        using (var get = await node.Client.GetAsync("/api/v1/jobs/no-such-job", ct))
        {
            get.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        }
        using (var delete = await node.Client.DeleteAsync("/api/v1/jobs/no-such-job", ct))
        {
            delete.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task TheNodeClientStartsFollowsAndCancelsADiscovery()
    {
        var ct = TestContext.Current.CancellationToken;
        var (node, discovery) = await StartAsync(ct);
        await using var host = node;
        var client = new TianWenNodeClient(node.Client);

        var started = await client.StartDiscoveryAsync(ct);
        started.IsSuccess.ShouldBeTrue(started.Error);
        var id = started.Value.ShouldNotBeNull().Id;
        await discovery.Began.Task.WaitAsync(ct);
        (await client.GetJobsAsync(ct)).Value.ShouldNotBeNull().ShouldContain(job => job.Id == id && job.State == JobState.Running);

        (await client.CancelJobAsync(id, ct)).IsSuccess.ShouldBeTrue();

        JobDto? ended;
        while ((ended = (await client.GetJobAsync(id, ct)).Value) is { State: JobState.Running })
        {
            await Task.Delay(20, ct);
        }
        ended.ShouldNotBeNull().State.ShouldBe(JobState.Cancelled);
        (await client.GetJobAsync("no-such-job", ct)).IsNotFound.ShouldBeTrue();
    }

    [Fact(Timeout = 30_000)]
    public async Task AJobsEndReachesAListeningClient()
    {
        var ct = TestContext.Current.CancellationToken;
        var (node, discovery) = await StartAsync(ct);
        await using var host = node;

        var ended = new TaskCompletionSource<WebSocketEventDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var stream = new TianWenEventStream(
            node.Client.BaseAddress ?? throw new InvalidOperationException("the harness client has no base address"),
            new SystemTimeProvider(), FakeExternal.CreateLogger(outputHelper));
        stream.EventReceived += (_, e) =>
        {
            if (e.Event == "JOB-PROGRESS" && e.Data?["State"]?.ToString() == nameof(JobState.Succeeded))
            {
                ended.TrySetResult(e);
            }
        };
        stream.Start(ct);
        while (!stream.IsConnected)
        {
            await Task.Delay(20, ct);
        }

        (_, var job) = await SendAsync(node.Client, HttpMethod.Post, "/api/v1/devices/discover", ct);
        await discovery.Began.Task.WaitAsync(ct);
        discovery.Release.SetResult();

        var push = await ended.Task.WaitAsync(ct);
        push.Data.ShouldNotBeNull()["Id"]?.ToString().ShouldBe(job.GetProperty("id").GetString());
        push.Data["Kind"]?.ToString().ShouldBe("discover");
    }
}
