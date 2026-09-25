using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using System;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Hosting;
using TianWen.Hosting.Extensions;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Fake;
using TianWen.Lib.Extensions;
using TianWen.Lib.Sequencing;
using Xunit;

namespace TianWen.Lib.Tests.Functional;

/// <summary>
/// A node owns its runs (P0b of docs/plans/hardware-in-the-server.md, #752): a run lives on the node's
/// token, an abort ends it through its own Finalise, a finished run never blocks the next, and stopping the
/// host stops the rig in the safe order. Driven over real HTTP against a real host, with a session factory
/// whose sessions end only when the test says so.
/// </summary>
[Collection("Hosting")]
#pragma warning disable CS8774 // MemberNotNull on InitializeAsync; xUnit guarantees init before tests
#pragma warning disable CS8602 // Dereference of possibly null; same reason
#pragma warning disable CS8604 // Possibly null argument; same reason
public class NodeRunLifecycleTests(ITestOutputHelper outputHelper) : IAsyncLifetime
{
    private static readonly Guid ProfileId = new Guid("5a1ec7ed-0b0e-4e5d-9a5e-000000000001");

    private WebApplication? _app;
    private HttpClient? _client;
    private FakeExternal? _external;
    private readonly ControlledSessionFactory _factory = new ControlledSessionFactory();

    [MemberNotNull(nameof(_app), nameof(_client), nameof(_external))]
    public async ValueTask InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();

        _external = new FakeExternal(outputHelper, Directory.CreateTempSubdirectory("tw_" + Guid.NewGuid().ToString("D")));
        builder.Services.AddSingleton<IExternal>(_external);
        builder.Services.AddSingleton<ITimeProvider>(_external.TimeProvider);
        builder.Services.AddAstrometry();
        builder.Services.AddFake();
        builder.Services.AddDevices();
        builder.Services.AddProfiles();
        builder.Services.AddSessionFactory();
        builder.Services.AddHostedSession();
        // Registered last, so it is the factory every endpoint resolves.
        builder.Services.AddSingleton<ISessionFactory>(_factory);

        _app = builder.Build();
        _app.UseWebSockets();
        _app.MapHostingApi();
        await _app.StartAsync(TestContext.Current.CancellationToken);

        _client = new HttpClient { BaseAddress = new Uri(_app.Urls.First()) };
    }

    public async ValueTask DisposeAsync()
    {
        // Whatever a test left running ends, so the host can stop.
        foreach (var session in _factory.Created)
        {
            session.EndsOnItsOwn.TrySetResult();
            session.Finalise.TrySetResult();
        }
        _factory.Initialised.TrySetResult();

        _client?.Dispose();
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }

    private IHostedSession Node => _app.Services.GetRequiredService<IHostedSession>();

    private async Task<ControlledSession> StartAsync(CancellationToken ct)
    {
        (await EnvelopeStatusAsync(_client.PostAsync($"/api/v1/session/start?profileId={ProfileId}", null, ct), ct)).ShouldBe(200);
        var session = _factory.Created.Last();
        await session.Started.Task.WaitAsync(ct);
        return session;
    }

    // The envelope's status, not the HTTP one: these endpoints still answer HTTP 200 whatever the envelope
    // says (P0b item 6, fixed with the wire items).
    private static async Task<int> EnvelopeStatusAsync(Task<HttpResponseMessage> request, CancellationToken ct)
    {
        using var response = await request;
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return body.RootElement.GetProperty("statusCode").GetInt32();
    }

    // The node's run task completes a moment after the session's own run returns.
    private async Task UntilTheNodeIsIdleAsync(CancellationToken ct)
    {
        for (var i = 0; i < 100 && Node.IsRunning; i++)
        {
            await Task.Delay(20, ct);
        }
        Node.IsRunning.ShouldBeFalse();
    }

    [Fact(Timeout = 30_000)]
    public async Task TheHostStartsTheNodesDiscoveryWithoutWaitingForIt()
    {
        // InitializeAsync is still pending (the factory never completes it here), yet the host started and
        // answers: discovery probes serial ports for tens of seconds and must not hold the server from
        // listening. It begins on a background task, so wait for the call rather than race it.
        var ct = TestContext.Current.CancellationToken;
        for (var i = 0; i < 100 && _factory.InitializeCalls == 0; i++)
        {
            await Task.Delay(50, ct);
        }
        _factory.InitializeCalls.ShouldBe(1, "the host starts the node, which it never did");

        var response = await _client.GetAsync("/api/v1/session/state", ct);
        response.StatusCode.ShouldNotBe(HttpStatusCode.ServiceUnavailable);
    }

    [Fact(Timeout = 30_000)]
    public async Task AStartWaitsForDiscoveryThenRuns()
    {
        var ct = TestContext.Current.CancellationToken;
        var start = _client.PostAsync($"/api/v1/session/start?profileId={ProfileId}", null, ct);
        await Task.Delay(200, ct);
        _factory.Created.ShouldBeEmpty("no session is created before the node knows its profiles and devices");

        _factory.Initialised.SetResult();
        (await start).StatusCode.ShouldBe(HttpStatusCode.OK);
        await _factory.Created.Single().Started.Task.WaitAsync(ct);
    }

    /// <summary>
    /// A run used to take the START request's token, and Kestrel reuses a connection's cancellation source
    /// for its next request, so a later request on the same connection that the client abandoned (what a
    /// GUI restarting does) cancelled the night.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task AClientAbandoningARequestOnTheSameConnectionDoesNotCancelTheRun()
    {
        var ct = TestContext.Current.CancellationToken;
        _factory.Initialised.SetResult();
        var session = await StartAsync(ct);

        using (var abandon = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            var neverEnding = new StreamContent(new NeverEndingStream());
            neverEnding.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            var inFlight = _client.PostAsync("/api/v1/session/targets", neverEnding, abandon.Token);
            await Task.Delay(300, ct);
            await abandon.CancelAsync();
            await Should.ThrowAsync<OperationCanceledException>(inFlight);
        }

        await Task.Delay(1000, ct);
        session.RunToken.IsCancellationRequested.ShouldBeFalse("only an abort or the host stopping may end a run");
        Node.IsRunning.ShouldBeTrue();
    }

    [Fact(Timeout = 30_000)]
    public async Task AnAbortEndsTheRunThroughItsFinaliseAndNeverDisposesItUnderneath()
    {
        var ct = TestContext.Current.CancellationToken;
        _factory.Initialised.SetResult();
        var session = await StartAsync(ct);

        (await EnvelopeStatusAsync(_client.PostAsync("/api/v1/session/abort", null, ct), ct)).ShouldBe(200);
        await session.Cancelled.Task.WaitAsync(ct);

        // Finalise is still going: the session is the node's current one, visible, and not disposed.
        await Task.Delay(200, ct);
        session.DisposedWhileRunning.ShouldBe(0, "the old abort disposed the session while its run carried on");
        Node.CurrentSession.ShouldBeSameAs(session.Session, "a client watches the abort through /state");

        session.Finalise.SetResult();
        await UntilTheNodeIsIdleAsync(ct);
    }

    [Fact(Timeout = 30_000)]
    public async Task AFinishedRunNeverBlocksTheNextStart()
    {
        var ct = TestContext.Current.CancellationToken;
        _factory.Initialised.SetResult();
        var first = await StartAsync(ct);
        first.EndsOnItsOwn.SetResult();
        first.Finalise.SetResult();
        await UntilTheNodeIsIdleAsync(ct);

        var second = await StartAsync(ct);

        second.Session.ShouldNotBeSameAs(first.Session);
        first.Disposals.ShouldBe(1, "the finished run is disposed once, before the next touches the rig");
        Node.CurrentSession.ShouldBeSameAs(second.Session);
    }

    [Fact(Timeout = 30_000)]
    public async Task TwoStartsAtOnceStartOneRun()
    {
        var ct = TestContext.Current.CancellationToken;
        var a = _client.PostAsync($"/api/v1/session/start?profileId={ProfileId}", null, ct);
        var b = _client.PostAsync($"/api/v1/session/start?profileId={ProfileId}", null, ct);
        await Task.Delay(200, ct);
        _factory.Initialised.SetResult();

        var codes = (await Task.WhenAll(EnvelopeStatusAsync(a, ct), EnvelopeStatusAsync(b, ct))).OrderBy(c => c).ToArray();

        codes.ShouldBe([200, 409]);
        _factory.Created.Count(s => s.Started.Task.IsCompleted).ShouldBe(1);
        _factory.Created.Count(s => s.Disposals == 1 && !s.Started.Task.IsCompleted).ShouldBe(1, "the loser's session is disposed, never run");
    }

    [Fact(Timeout = 60_000)]
    public async Task StoppingTheHostEndsTheRunThroughItsFinaliseBeforeItReturns()
    {
        var ct = TestContext.Current.CancellationToken;
        _factory.Initialised.SetResult();
        var session = await StartAsync(ct);

        var stop = _app.StopAsync(ct);
        await session.Cancelled.Task.WaitAsync(ct);
        await Task.Delay(200, ct);
        stop.IsCompleted.ShouldBeFalse("the host waits for the run's Finalise");
        session.DisposedWhileRunning.ShouldBe(0);

        session.Finalise.SetResult();
        await stop;
        session.Disposals.ShouldBe(1);
    }

    [Fact(Timeout = 60_000)]
    public async Task StoppingTheHostWarmsAndDisconnectsItsCooledCameras()
    {
        var ct = TestContext.Current.CancellationToken;
        var hub = _app.Services.GetRequiredService<IDeviceHub>();
        var device = new FakeDevice(DeviceType.Camera, 1);
        var camera = (ICameraDriver)await hub.ConnectAsync(device, ct);
        await camera.SetSetCCDTemperatureAsync(-10, ct);
        await camera.SetCoolerOnAsync(true, ct);

        await _app.StopAsync(ct);

        hub.IsConnected(device.DeviceUri).ShouldBeFalse("a node stopping disconnects its cameras, warmed first");
        (await camera.GetCoolerOnAsync(ct)).ShouldBeFalse();
    }

    [Fact]
    public void TheHostMayTakeLongEnoughToStopForAWarmUp()
    {
        var options = _app.Services.GetRequiredService<IOptions<HostOptions>>().Value;
        options.ShutdownTimeout.ShouldBeGreaterThanOrEqualTo(TimeSpan.FromMinutes(15), "the default 30 s cut every warm-up off");
    }

    // --- What a start runs on (P0b item 10) -------------------------------------------------------

    [Fact(Timeout = 30_000)]
    public async Task AStartWithNoBodyRunsOnTheDeclaredDefaults()
    {
        var ct = TestContext.Current.CancellationToken;
        _factory.Initialised.SetResult();

        var session = await StartAsync(ct);

        // Declared values, never compared with new SessionConfiguration(), which on the old code WAS the zeros.
        double.IsNaN(session.Configuration.SiteLatitude).ShouldBeTrue("the zero-filled configuration synced the mount's site to 0, 0");
        session.Configuration.AutoFocusStepCount.ShouldBe(9);
        session.Configuration.WarmCamerasOnSessionEnd.ShouldBeTrue();
        session.Configuration.GuidingTries.ShouldBe(3);
    }

    [Fact(Timeout = 30_000)]
    public async Task TheClientsOwnConfigurationReachesTheRun()
    {
        // JsonContent goes chunked, with no Content-Length, and a body read only when Content-Length > 0
        // was never read: the client's configuration fell on the floor and the run used the zeros.
        var ct = TestContext.Current.CancellationToken;
        _factory.Initialised.SetResult();
        var asked = new SessionConfiguration() with { AutoFocusStepCount = 7, SiteLatitude = -37.8136, SiteLongitude = 144.9631 };
        var client = new TianWen.RemoteClient.TianWenNodeClient(_client);

        var result = await client.StartSessionAsync(ProfileId, TianWen.Hosting.Dto.SessionConfigApiDto.FromConfiguration(asked), ct);

        result.IsSuccess.ShouldBeTrue(result.Error);
        var session = _factory.Created.Single();
        await session.Started.Task.WaitAsync(ct);
        session.Configuration.ShouldBe(asked);
    }

    [Fact(Timeout = 30_000)]
    public async Task AMalformedConfigurationIsRefusedNotRunOnDefaults()
    {
        var ct = TestContext.Current.CancellationToken;
        _factory.Initialised.SetResult();
        using var body = new StringContent("{ not json", System.Text.Encoding.UTF8, "application/json");

        var status = await EnvelopeStatusAsync(_client.PostAsync($"/api/v1/session/start?profileId={ProfileId}", body, ct), ct);

        status.ShouldBe(400);
        _factory.Created.ShouldBeEmpty("a caller's mistake is answered, never run on defaults it did not ask for");
    }

    private sealed class ControlledSessionFactory : ISessionFactory
    {
        public TaskCompletionSource Initialised { get; } = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        public int InitializeCalls => Volatile.Read(ref _initializeCalls);

        public ConcurrentQueue<ControlledSession> Created { get; } = new ConcurrentQueue<ControlledSession>();

        private int _initializeCalls;

        public ValueTask InitializeAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _initializeCalls);
            return new ValueTask(Initialised.Task.WaitAsync(cancellationToken));
        }

        public ISession Create(Guid profileId, in SessionConfiguration configuration, ReadOnlySpan<ScheduledObservation> observations)
        {
            var session = new ControlledSession(configuration);
            Created.Enqueue(session);
            return session.Session;
        }
    }

    /// <summary>A session that runs until cancelled or told it ended, then "finalises" until told it is done.</summary>
    private sealed class ControlledSession
    {
        private int _running;
        private int _disposals;
        private int _disposedWhileRunning;

        public ControlledSession(SessionConfiguration configuration)
        {
            Configuration = configuration;
            Session = Substitute.For<ISession>();
            Session.RunAsync(Arg.Any<CancellationToken>()).Returns(call => RunAsync(call.Arg<CancellationToken>()));
            Session.RunFlatsOnlyAsync(Arg.Any<TwilightPeriod>(), Arg.Any<CancellationToken>())
                .Returns(call => RunAsync(call.ArgAt<CancellationToken>(1)));
            Session.DisposeAsync().Returns(_ =>
            {
                Interlocked.Increment(ref _disposals);
                if (Volatile.Read(ref _running) == 1)
                {
                    Interlocked.Increment(ref _disposedWhileRunning);
                }
                return ValueTask.CompletedTask;
            });
        }

        public ISession Session { get; }
        public SessionConfiguration Configuration { get; }
        public CancellationToken RunToken { get; private set; }
        public TaskCompletionSource Started { get; } = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource EndsOnItsOwn { get; } = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Finalise { get; } = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Ended { get; } = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Disposals => Volatile.Read(ref _disposals);
        public int DisposedWhileRunning => Volatile.Read(ref _disposedWhileRunning);

        private async Task RunAsync(CancellationToken cancellationToken)
        {
            RunToken = cancellationToken;
            Volatile.Write(ref _running, 1);
            using var registration = cancellationToken.Register(() => Cancelled.TrySetResult());
            Started.TrySetResult();
            try
            {
                await EndsOnItsOwn.Task.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // Aborted: the real session goes on to its Finalise, and so does this one.
            }
            await Finalise.Task;
            Volatile.Write(ref _running, 0);
            Ended.TrySetResult();
        }
    }

    /// <summary>A request body that never arrives, so the request stays in flight until the client abandons it.</summary>
    private sealed class NeverEndingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
