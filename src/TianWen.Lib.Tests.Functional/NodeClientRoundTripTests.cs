using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shouldly;
using System;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using TianWen.Hosting.Dto;
using TianWen.Hosting.Extensions;
using TianWen.Lib.Devices;
using TianWen.Lib.Extensions;
using TianWen.RemoteClient;
using Xunit;

namespace TianWen.Lib.Tests.Functional;

/// <summary>
/// The real <see cref="TianWenNodeClient"/> against a real running host, over real HTTP.
/// <para>
/// <b>What this layer adds.</b> <c>RemoteSessionMirrorTests</c> drives the client through a scripted
/// <c>HttpMessageHandler</c>, so it proves the client's parsing but nothing about routing, status-code
/// mapping, or whether the endpoint is even mapped. These tests close that: the request leaves through
/// a socket, is routed by the real minimal-API pipeline, and the envelope comes back through the real
/// registered JSON context.
/// </para>
/// <para>
/// <b>What it deliberately does NOT cover, so nobody reads more into it than is there.</b> This host has
/// no running session, so <c>GET /session/state</c> answers 404 and <b>no <c>SessionStateDto</c> is ever
/// deserialized here</b> -- which is the very DTO the `required`-on-a-nullable bug lived on. That class of
/// contract drift is caught one level down instead, and genuinely: <c>HostingWireNumberTests</c> drives
/// the real <c>SessionStateDto.FromSession</c> projection through the real context, and
/// <c>RemoteSessionMirrorTests.TheServersOwnProjectionRoundTripsBackIntoAMirror</c> reads that output back
/// into a mirror. Covering it over HTTP as well would mean standing up a profile and a live session here,
/// duplicating the session suite. If a future change makes that cheap, this is the place for it.
/// </para>
/// </summary>
[Collection("Hosting")]
#pragma warning disable CS8774 // MemberNotNull on InitializeAsync -- xUnit guarantees init before tests
#pragma warning disable CS8602 // Dereference of possibly null -- same reason
public class NodeClientRoundTripTests(ITestOutputHelper outputHelper) : IAsyncLifetime
{
    private WebApplication? _app;
    private HttpClient? _http;
    private TianWenNodeClient? _client;

    [MemberNotNull(nameof(_app), nameof(_http), nameof(_client))]
    public async ValueTask InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();

        var fakeExternal = new FakeExternal(outputHelper, System.IO.Directory.CreateTempSubdirectory("twrc_" + Guid.NewGuid().ToString("D")));
        builder.Services.AddSingleton<IExternal>(fakeExternal);
        builder.Services.AddSingleton<ITimeProvider>(fakeExternal.TimeProvider);
        builder.Services.AddAstrometry();
        builder.Services.AddFake();
        builder.Services.AddDevices();
        builder.Services.AddProfiles();
        builder.Services.AddSessionFactory();
        builder.Services.AddHostedSession();

        _app = builder.Build();
        _app.UseWebSockets();
        _app.MapHostingApi();

        await _app.StartAsync(TestContext.Current.CancellationToken);

        _http = new HttpClient { BaseAddress = new Uri(_app.Urls.First()) };
        _client = new TianWenNodeClient(_http);
    }

    public async ValueTask DisposeAsync()
    {
        _http?.Dispose();
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }

    [Fact(Timeout = 15_000)]
    public async Task AnIdleNodeReportsNotFoundRatherThanUnreachable()
    {
        // The distinction NodeResult draws, and it is load-bearing for the mirror: "this node answered,
        // and it has no session" must never be confused with "this node is down". A mirror that
        // conflated them would drop a reachable rig off the UI.
        var result = await _client.GetSessionStateAsync(TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeFalse();
        result.IsNotFound.ShouldBeTrue();
        result.StatusCode.ShouldBe(404);
    }

    [Fact(Timeout = 15_000)]
    public async Task TargetsRoundTripThroughRealHttp()
    {
        var ct = TestContext.Current.CancellationToken;

        var added = await _client.AddTargetAsync(
            new PendingTarget("Vega", 18.6156, 38.7837, DurationMinutes: 45), ct);
        added.IsSuccess.ShouldBeTrue(added.Error);

        var listed = await _client.GetTargetsAsync(ct);
        listed.IsSuccess.ShouldBeTrue(listed.Error);
        listed.Value.ShouldNotBeNull();
        listed.Value.Length.ShouldBe(1);
        listed.Value[0].Name.ShouldBe("Vega");
        listed.Value[0].RA.ShouldBe(18.6156, 1e-6);

        var cleared = await _client.ClearTargetsAsync(ct);
        cleared.IsSuccess.ShouldBeTrue(cleared.Error);

        var empty = await _client.GetTargetsAsync(ct);
        empty.Value.ShouldNotBeNull();
        empty.Value.ShouldBeEmpty();
    }

    [Fact(Timeout = 15_000)]
    public async Task ProfilesDeserializeThroughTheSharedContract()
    {
        var ct = TestContext.Current.CancellationToken;

        var profiles = await _client.GetProfilesAsync(ct);

        // A host with no saved profiles still answers successfully with an empty array -- the assertion
        // that matters is that the payload DESERIALIZED, i.e. the server's ProfileSummaryDto and the
        // client's agree. A drift here is what this test class is for.
        profiles.IsSuccess.ShouldBeTrue(profiles.Error);
        profiles.Value.ShouldNotBeNull();

        if (profiles.Value.Length > 0)
        {
            var detail = await _client.GetProfileAsync(profiles.Value[0].ProfileId, ct);
            detail.IsSuccess.ShouldBeTrue(detail.Error);
            detail.Value.ShouldNotBeNull();
            detail.Value.Equipment.ShouldNotBeNull();
        }
    }

    [Fact(Timeout = 15_000)]
    public async Task AnActiveProfileIsReadableBackOverTheWire()
    {
        var ct = TestContext.Current.CancellationToken;

        // Before anything is set, "which profile does this node run" has a real answer -- none. A 404 here
        // is the node answering, which is exactly the distinction a board of rigs depends on: unknown
        // profile is not the same as unreachable rig.
        var none = await _client.GetActiveProfileAsync(ct);
        none.IsNotFound.ShouldBeTrue(none.Error);

        var profiles = await _client.GetProfilesAsync(ct);
        profiles.IsSuccess.ShouldBeTrue(profiles.Error);
        if (profiles.Value is not { Length: > 0 } available)
        {
            return; // nothing saved on this host, so there is no id to make active
        }

        var expected = available[0];
        (await _client.SetActiveProfileAsync(expected.ProfileId, ct)).IsSuccess.ShouldBeTrue();

        // The point of the endpoint: the NAME comes back, not just the id. A client labelling a rig by the
        // optical train it runs cannot resolve a bare guid against a profile store it does not have.
        var active = await _client.GetActiveProfileAsync(ct);
        active.IsSuccess.ShouldBeTrue(active.Error);
        active.Value.ShouldNotBeNull().ProfileId.ShouldBe(expected.ProfileId);
        active.Value.Name.ShouldBe(expected.Name);
    }

    [Fact(Timeout = 15_000)]
    public async Task AbortingWithNoSessionIsAClean404NotAThrow()
    {
        var result = await _client.AbortSessionAsync(TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeFalse();
        result.IsNotFound.ShouldBeTrue();
        // The message is the node's own, proving the error envelope survived the round trip rather than
        // being synthesized client-side from a bare status code.
        result.Error.ShouldNotBeNull().ShouldContain("No active session");
    }

    [Fact(Timeout = 15_000)]
    public async Task StartingWithAnUnknownProfileSurfacesTheNodesOwnMessage()
    {
        var result = await _client.StartSessionAsync(Guid.NewGuid(), TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeFalse();
        result.Error.ShouldNotBeNullOrWhiteSpace();
    }
}
