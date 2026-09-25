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
    private NodeHarness? _harness;

    [MemberNotNull(nameof(_harness))]
    public async ValueTask InitializeAsync() => _harness = await NodeHarness.StartAsync(outputHelper, TestContext.Current.CancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (_harness is not null)
        {
            await _harness.DisposeAsync();
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task TheHostStartsTheNodesDiscoveryWithoutWaitingForIt()
    {
        // InitializeAsync is still pending (the factory never completes it here), yet the host started and
        // answers: discovery probes serial ports for tens of seconds and must not hold the server from
        // listening. It begins on a background task, so wait for the call rather than race it.
        var ct = TestContext.Current.CancellationToken;
        for (var i = 0; i < 100 && _harness.Factory.InitializeCalls == 0; i++)
        {
            await Task.Delay(50, ct);
        }
        _harness.Factory.InitializeCalls.ShouldBe(1, "the host starts the node, which it never did");

        var response = await _harness.Client.GetAsync("/api/v1/session/state", ct);
        response.StatusCode.ShouldNotBe(HttpStatusCode.ServiceUnavailable);
    }

    [Fact(Timeout = 30_000)]
    public async Task AStartWaitsForDiscoveryThenRuns()
    {
        var ct = TestContext.Current.CancellationToken;
        var start = _harness.Client.PostAsync($"/api/v1/session/start?profileId={NodeHarness.ProfileId}", null, ct);
        await Task.Delay(200, ct);
        _harness.Factory.Created.ShouldBeEmpty("no session is created before the node knows its profiles and devices");

        _harness.Factory.Initialised.SetResult();
        (await start).StatusCode.ShouldBe(HttpStatusCode.OK);
        await _harness.Factory.Created.Single().Started.Task.WaitAsync(ct);
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
        _harness.Factory.Initialised.SetResult();
        var session = await _harness.StartSessionAsync(ct);

        using (var abandon = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            var neverEnding = new StreamContent(new NeverEndingStream());
            neverEnding.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            var inFlight = _harness.Client.PostAsync("/api/v1/session/targets", neverEnding, abandon.Token);
            await Task.Delay(300, ct);
            await abandon.CancelAsync();
            await Should.ThrowAsync<OperationCanceledException>(inFlight);
        }

        await Task.Delay(1000, ct);
        session.RunToken.IsCancellationRequested.ShouldBeFalse("only an abort or the host stopping may end a run");
        _harness.Node.IsRunning.ShouldBeTrue();
    }

    [Fact(Timeout = 30_000)]
    public async Task AnAbortEndsTheRunThroughItsFinaliseAndNeverDisposesItUnderneath()
    {
        var ct = TestContext.Current.CancellationToken;
        _harness.Factory.Initialised.SetResult();
        var session = await _harness.StartSessionAsync(ct);

        (await NodeHarness.EnvelopeStatusAsync(_harness.Client.PostAsync("/api/v1/session/abort", null, ct), ct)).ShouldBe(200);
        await session.Cancelled.Task.WaitAsync(ct);

        // Finalise is still going: the session is the node's current one, visible, and not disposed.
        await Task.Delay(200, ct);
        session.DisposedWhileRunning.ShouldBe(0, "the old abort disposed the session while its run carried on");
        _harness.Node.CurrentSession.ShouldBeSameAs(session.Session, "a client watches the abort through /state");

        session.Finalise.SetResult();
        await _harness.UntilTheNodeIsIdleAsync(ct);
    }

    [Fact(Timeout = 30_000)]
    public async Task AFinishedRunNeverBlocksTheNextStart()
    {
        var ct = TestContext.Current.CancellationToken;
        _harness.Factory.Initialised.SetResult();
        var first = await _harness.StartSessionAsync(ct);
        first.EndsOnItsOwn.SetResult();
        first.Finalise.SetResult();
        await _harness.UntilTheNodeIsIdleAsync(ct);

        var second = await _harness.StartSessionAsync(ct);

        second.Session.ShouldNotBeSameAs(first.Session);
        first.Disposals.ShouldBe(1, "the finished run is disposed once, before the next touches the rig");
        _harness.Node.CurrentSession.ShouldBeSameAs(second.Session);
    }

    [Fact(Timeout = 30_000)]
    public async Task TwoStartsAtOnceStartOneRun()
    {
        var ct = TestContext.Current.CancellationToken;
        var a = _harness.Client.PostAsync($"/api/v1/session/start?profileId={NodeHarness.ProfileId}", null, ct);
        var b = _harness.Client.PostAsync($"/api/v1/session/start?profileId={NodeHarness.ProfileId}", null, ct);
        await Task.Delay(200, ct);
        _harness.Factory.Initialised.SetResult();

        var codes = (await Task.WhenAll(NodeHarness.EnvelopeStatusAsync(a, ct), NodeHarness.EnvelopeStatusAsync(b, ct))).OrderBy(c => c).ToArray();

        codes.ShouldBe([200, 409]);
        _harness.Factory.Created.Count(s => s.Started.Task.IsCompleted).ShouldBe(1);
        _harness.Factory.Created.Count(s => s.Disposals == 1 && !s.Started.Task.IsCompleted).ShouldBe(1, "the loser's session is disposed, never run");
    }

    [Fact(Timeout = 60_000)]
    public async Task StoppingTheHostEndsTheRunThroughItsFinaliseBeforeItReturns()
    {
        var ct = TestContext.Current.CancellationToken;
        _harness.Factory.Initialised.SetResult();
        var session = await _harness.StartSessionAsync(ct);

        var stop = _harness.App.StopAsync(ct);
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
        var hub = _harness.App.Services.GetRequiredService<IDeviceHub>();
        var device = new FakeDevice(DeviceType.Camera, 1);
        var camera = (ICameraDriver)await hub.ConnectAsync(device, ct);
        await camera.SetSetCCDTemperatureAsync(-10, ct);
        await camera.SetCoolerOnAsync(true, ct);

        await _harness.App.StopAsync(ct);

        hub.IsConnected(device.DeviceUri).ShouldBeFalse("a node stopping disconnects its cameras, warmed first");
        (await camera.GetCoolerOnAsync(ct)).ShouldBeFalse();
    }

    [Fact]
    public void TheHostMayTakeLongEnoughToStopForAWarmUp()
    {
        var options = _harness.App.Services.GetRequiredService<IOptions<HostOptions>>().Value;
        options.ShutdownTimeout.ShouldBeGreaterThanOrEqualTo(TimeSpan.FromMinutes(15), "the default 30 s cut every warm-up off");
    }

    // --- What a start runs on (P0b item 10) -------------------------------------------------------

    [Fact(Timeout = 30_000)]
    public async Task AStartWithNoBodyRunsOnTheDeclaredDefaults()
    {
        var ct = TestContext.Current.CancellationToken;
        _harness.Factory.Initialised.SetResult();

        var session = await _harness.StartSessionAsync(ct);

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
        _harness.Factory.Initialised.SetResult();
        var asked = new SessionConfiguration() with { AutoFocusStepCount = 7, SiteLatitude = -37.8136, SiteLongitude = 144.9631 };
        var client = new TianWen.RemoteClient.TianWenNodeClient(_harness.Client);

        var result = await client.StartSessionAsync(NodeHarness.ProfileId, TianWen.Hosting.Dto.SessionConfigApiDto.FromConfiguration(asked), ct);

        result.IsSuccess.ShouldBeTrue(result.Error);
        var session = _harness.Factory.Created.Single();
        await session.Started.Task.WaitAsync(ct);
        session.Configuration.ShouldBe(asked);
    }

    [Fact(Timeout = 30_000)]
    public async Task AMalformedConfigurationIsRefusedNotRunOnDefaults()
    {
        var ct = TestContext.Current.CancellationToken;
        _harness.Factory.Initialised.SetResult();
        using var body = new StringContent("{ not json", System.Text.Encoding.UTF8, "application/json");

        var status = await NodeHarness.EnvelopeStatusAsync(_harness.Client.PostAsync($"/api/v1/session/start?profileId={NodeHarness.ProfileId}", body, ct), ct);

        status.ShouldBe(400);
        _harness.Factory.Created.ShouldBeEmpty("a caller's mistake is answered, never run on defaults it did not ask for");
    }
}
