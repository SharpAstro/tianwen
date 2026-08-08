using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shouldly;
using System;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using TianWen.Hosting.Extensions;
using TianWen.Lib.Devices;
using TianWen.Lib.Extensions;
using Xunit;

namespace TianWen.Lib.Tests.Functional;

[Collection("Hosting")]
#pragma warning disable CS8774 // MemberNotNull on InitializeAsync; xUnit guarantees init before tests
#pragma warning disable CS8602 // Dereference of possibly null; same reason
public class HostingApiTests(ITestOutputHelper outputHelper) : IAsyncLifetime
{
    private WebApplication? _app;
    private HttpClient? _client;

    [MemberNotNull(nameof(_app), nameof(_client))]
    public async ValueTask InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();

        // Use a random available port
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        builder.Logging.ClearProviders();
        var fakeExternal = new FakeExternal(outputHelper, System.IO.Directory.CreateTempSubdirectory("tw_" + Guid.NewGuid().ToString("D")));
        // Register TianWen services with fake devices (mirrors CLI registration chain)
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

        var baseUrl = _app.Urls.First();
        _client = new HttpClient { BaseAddress = new Uri(baseUrl) };
    }

    public async ValueTask DisposeAsync()
    {
        _client?.Dispose();
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }

    [Fact(Timeout = 10_000)]
    public async Task SessionState_WithNoSession_ReturnsNotFound()
    {
        var ct = TestContext.Current.CancellationToken;
        var response = await _client.GetAsync("/api/v1/session/state", ct);
        var json = await response.Content.ReadAsStringAsync(ct);

        var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("success").GetBoolean().ShouldBeFalse();
        doc.RootElement.GetProperty("statusCode").GetInt32().ShouldBe(404);
        doc.RootElement.GetProperty("error").GetString().ShouldNotBeNull().ShouldContain("No active session");
    }

    [Fact(Timeout = 10_000)]
    public async Task MountInfo_WithNoSession_ReturnsNotFound()
    {
        var ct = TestContext.Current.CancellationToken;
        var response = await _client.GetAsync("/api/v1/mount/info", ct);
        var json = await response.Content.ReadAsStringAsync(ct);

        var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("success").GetBoolean().ShouldBeFalse();
        doc.RootElement.GetProperty("statusCode").GetInt32().ShouldBe(404);
    }

    [Fact(Timeout = 10_000)]
    public async Task GuiderInfo_WithNoSession_ReturnsNotFound()
    {
        var ct = TestContext.Current.CancellationToken;
        var response = await _client.GetAsync("/api/v1/guider/info", ct);
        var json = await response.Content.ReadAsStringAsync(ct);

        var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("success").GetBoolean().ShouldBeFalse();
    }

    [Fact(Timeout = 10_000)]
    public async Task OtaList_WithNoSession_ReturnsNotFound()
    {
        var ct = TestContext.Current.CancellationToken;
        var response = await _client.GetAsync("/api/v1/ota", ct);
        var json = await response.Content.ReadAsStringAsync(ct);

        var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("success").GetBoolean().ShouldBeFalse();
    }

    [Fact(Timeout = 10_000)]
    public async Task DeviceList_ReturnsSuccessEnvelope()
    {
        var ct = TestContext.Current.CancellationToken;
        var response = await _client.GetAsync("/api/v1/devices", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var json = await response.Content.ReadAsStringAsync(ct);
        var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("success").GetBoolean().ShouldBeTrue();
        doc.RootElement.GetProperty("type").GetString().ShouldBe("API");
    }

    [Fact(Timeout = 10_000)]
    public async Task ProfileList_ReturnsSuccessEnvelope()
    {
        var ct = TestContext.Current.CancellationToken;
        var response = await _client.GetAsync("/api/v1/profiles", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var json = await response.Content.ReadAsStringAsync(ct);
        var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("success").GetBoolean().ShouldBeTrue();
    }

    // Phase 2: Control endpoints

    [Fact(Timeout = 30_000)]
    public async Task DeviceDiscover_ReturnsDevices()
    {
        var ct = TestContext.Current.CancellationToken;
        var response = await _client.GetAsync("/api/v1/devices/discover", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var json = await response.Content.ReadAsStringAsync(ct);
        var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("success").GetBoolean().ShouldBeTrue();

        // Fake device source should discover fake devices
        var devices = doc.RootElement.GetProperty("response");
        devices.GetArrayLength().ShouldBeGreaterThan(0);
    }

    [Fact(Timeout = 10_000)]
    public async Task SessionStart_WithInvalidProfileId_ReturnsBadRequest()
    {
        var ct = TestContext.Current.CancellationToken;
        var response = await _client.PostAsync("/api/v1/session/start?profileId=not-a-guid", null, ct);

        var json = await response.Content.ReadAsStringAsync(ct);
        var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("success").GetBoolean().ShouldBeFalse();
        doc.RootElement.GetProperty("error").GetString().ShouldNotBeNull().ShouldContain("Invalid profile ID");
    }

    [Fact(Timeout = 10_000)]
    public async Task SessionAbort_WithNoSession_ReturnsNotFound()
    {
        var ct = TestContext.Current.CancellationToken;
        var response = await _client.PostAsync("/api/v1/session/abort", null, ct);

        var json = await response.Content.ReadAsStringAsync(ct);
        var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("success").GetBoolean().ShouldBeFalse();
        doc.RootElement.GetProperty("statusCode").GetInt32().ShouldBe(404);
    }

    [Fact(Timeout = 10_000)]
    public async Task MountSlew_WithNoSession_ReturnsNotFound()
    {
        var ct = TestContext.Current.CancellationToken;
        var response = await _client.PostAsync("/api/v1/mount/slew?ra=12.0&dec=45.0", null, ct);

        var json = await response.Content.ReadAsStringAsync(ct);
        var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("success").GetBoolean().ShouldBeFalse();
        doc.RootElement.GetProperty("statusCode").GetInt32().ShouldBe(404);
    }

    [Fact(Timeout = 10_000)]
    public async Task FocuserMove_WithNoSession_ReturnsNotFound()
    {
        var ct = TestContext.Current.CancellationToken;
        var response = await _client.PostAsync("/api/v1/ota/0/focuser/move?position=1000", null, ct);

        var json = await response.Content.ReadAsStringAsync(ct);
        var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("success").GetBoolean().ShouldBeFalse();
    }

    [Fact(Timeout = 10_000)]
    public async Task FilterWheelChange_WithNoSession_ReturnsNotFound()
    {
        var ct = TestContext.Current.CancellationToken;
        var response = await _client.PostAsync("/api/v1/ota/0/filterwheel/change?position=2", null, ct);

        var json = await response.Content.ReadAsStringAsync(ct);
        var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("success").GetBoolean().ShouldBeFalse();
    }

    // --- Native-AOT-fragile paths ---
    // These guard the wiring that the AOT publish needs but a plain `dotnet build` cannot flag:
    // complex JSON request-body binding (source-gen JsonSerializerContext via ConfigureHttpJsonOptions)
    // and concrete-DTO payloads (no ResponseEnvelope<object>). Since AddHostedSession registers the
    // source-gen contexts, the test host serializes through the same path as the native binary.

    [Fact(Timeout = 10_000)]
    public async Task SessionTargets_PostJsonBody_AddsAndRoundTrips()
    {
        var ct = TestContext.Current.CancellationToken;

        // POST a PendingTarget as a JSON body (not query params) -- this is the body-binding path
        // that throws NotSupportedException under AOT when the JSON context isn't registered.
        var body = new StringContent(
            """{"name":"Vega","ra":18.6156,"dec":38.7837,"durationMinutes":45}""",
            System.Text.Encoding.UTF8, "application/json");
        var post = await _client.PostAsync("/api/v1/session/targets", body, ct);
        post.StatusCode.ShouldBe(HttpStatusCode.OK);
        var postDoc = JsonDocument.Parse(await post.Content.ReadAsStringAsync(ct));
        postDoc.RootElement.GetProperty("success").GetBoolean().ShouldBeTrue();

        // GET returns a concrete PendingTarget[] payload (was ResponseEnvelope<object>) and must
        // round-trip the bound values.
        var get = await _client.GetAsync("/api/v1/session/targets", ct);
        get.StatusCode.ShouldBe(HttpStatusCode.OK);
        var arr = JsonDocument.Parse(await get.Content.ReadAsStringAsync(ct)).RootElement.GetProperty("response");
        arr.GetArrayLength().ShouldBe(1);
        var target = arr[0];
        target.GetProperty("name").GetString().ShouldBe("Vega");
        target.GetProperty("ra").GetDouble().ShouldBe(18.6156, 1e-6);
        target.GetProperty("dec").GetDouble().ShouldBe(38.7837, 1e-6);
        target.GetProperty("durationMinutes").GetDouble().ShouldBe(45.0, 1e-6);
    }

    [Fact(Timeout = 10_000)]
    public async Task ImageEnhance_WithNoPipelineRegistered_ReturnsServiceUnavailable()
    {
        var ct = TestContext.Current.CancellationToken;

        // This host wires the device chain but NOT AddRcAstroAi()/AddTianWenAi(), so no SharpenPipeline
        // is registered. The enhancer must report IsAvailable == false and the endpoint must reject with
        // an in-band 503 -- this is the exact regression that crashed host startup when HostedImageEnhancer
        // took a required SharpenPipeline (the whole functional suite failed to start). The body-binding
        // path also exercises the AOT-fragile EnhanceRequestDto deserialization.
        var body = new StringContent(
            """{"inputPath":"/does/not/matter.fits","backend":"sas"}""",
            System.Text.Encoding.UTF8, "application/json");
        var response = await _client.PostAsync("/api/v1/image/enhance", body, ct);

        var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        doc.RootElement.GetProperty("success").GetBoolean().ShouldBeFalse();
        doc.RootElement.GetProperty("statusCode").GetInt32().ShouldBe(503);
        doc.RootElement.GetProperty("error").GetString().ShouldNotBeNull().ShouldContain("not available");
    }

    [Fact(Timeout = 10_000)]
    public async Task NinaListDevices_ReturnsSuccessEnvelopeWithArray()
    {
        var ct = TestContext.Current.CancellationToken;

        // ninaAPI list-devices returns a concrete NinaDeviceListItemDto[] payload (was an
        // anonymous-type ResponseEnvelope<object>, which can't be serialized under AOT).
        var response = await _client.GetAsync("/v2/api/equipment/camera/list-devices", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        // ninaAPI v2 uses PascalCase.
        doc.RootElement.GetProperty("Success").GetBoolean().ShouldBeTrue();
        doc.RootElement.GetProperty("Response").ValueKind.ShouldBe(JsonValueKind.Array);
    }

    // --- P2 session-mirror surface (docs/plans/remote-profile.md Part 2) ---
    // Everything below goes through real routing, real parameter binding and the real JSON contexts.
    // The unit tests for these cover the logic in isolation; only this level catches a route that was
    // never mapped, a parameter that will not bind, or a payload the source-gen context cannot write.

    [Fact(Timeout = 10_000)]
    public async Task Preview_WithNoSession_ReturnsNotFound()
    {
        var ct = TestContext.Current.CancellationToken;

        var response = await _client.GetAsync("/api/v1/preview/0?quality=70&scale=0.5", ct);

        var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        doc.RootElement.GetProperty("success").GetBoolean().ShouldBeFalse();
        doc.RootElement.GetProperty("statusCode").GetInt32().ShouldBe(404);
    }

    [Fact(Timeout = 10_000)]
    public async Task DevicesStructured_ReturnsUrisAndConnectionState()
    {
        var ct = TestContext.Current.CancellationToken;

        // Discover first so there is something to report; the fake source surfaces devices.
        (await _client.GetAsync("/api/v1/devices/discover", ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var response = await _client.GetAsync("/api/v1/devices/structured", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        doc.RootElement.GetProperty("success").GetBoolean().ShouldBeTrue();
        var devices = doc.RootElement.GetProperty("response");
        devices.GetArrayLength().ShouldBeGreaterThan(0);

        // The whole point of this endpoint over /devices: a URI a client can actually assign, plus
        // live connection state. /devices returns only pre-formatted display strings.
        var device = devices[0];
        device.GetProperty("uri").GetString().ShouldNotBeNullOrWhiteSpace();
        device.GetProperty("deviceType").GetString().ShouldNotBeNullOrWhiteSpace();
        device.GetProperty("displayName").GetString().ShouldNotBeNull();
        device.GetProperty("connected").GetBoolean().ShouldBeFalse();
    }

    [Fact(Timeout = 10_000)]
    public async Task Notifications_WithNoSession_ReturnsEmptyEnvelope()
    {
        var ct = TestContext.Current.CancellationToken;

        var response = await _client.GetAsync("/api/v1/session/notifications", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        doc.RootElement.GetProperty("success").GetBoolean().ShouldBeTrue();
        doc.RootElement.GetProperty("response").ValueKind.ShouldBe(JsonValueKind.Array);
    }

    [Fact(Timeout = 10_000)]
    public async Task PromptRespond_WithNoPromptOutstanding_ReturnsNotFound()
    {
        var ct = TestContext.Current.CancellationToken;

        // Also pins that `bool proceed` binds from the query string at all.
        var response = await _client.PostAsync("/api/v1/session/prompt/respond?proceed=true", content: null, ct);

        var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        doc.RootElement.GetProperty("success").GetBoolean().ShouldBeFalse();
        doc.RootElement.GetProperty("statusCode").GetInt32().ShouldBe(404);
    }

    [Fact(Timeout = 10_000)]
    public async Task SessionSchedule_PostJsonBody_AcceptsFullFidelityAndClears()
    {
        var ct = TestContext.Current.CancellationToken;

        // The AOT-fragile path for this endpoint: an ARRAY of complex DTOs with a nested array
        // (filterPlan) bound from the body via the source-gen context.
        var body = new StringContent(
            """
            [{"targetName":"M42","targetRA":5.588,"targetDec":-5.39,
              "start":"2026-07-26T21:15:00+00:00","durationMinutes":90,"acrossMeridian":true,
              "filterPlan":[{"filterPosition":0,"subExposureSeconds":120,"count":20}],
              "gain":100,"offset":30,"priority":0}]
            """,
            System.Text.Encoding.UTF8, "application/json");

        var post = await _client.PostAsync("/api/v1/session/schedule", body, ct);
        post.StatusCode.ShouldBe(HttpStatusCode.OK);
        var postDoc = JsonDocument.Parse(await post.Content.ReadAsStringAsync(ct));
        postDoc.RootElement.GetProperty("success").GetBoolean().ShouldBeTrue();
        postDoc.RootElement.GetProperty("response").GetString().ShouldNotBeNull().ShouldContain("1 observation");

        var delete = await _client.DeleteAsync("/api/v1/session/schedule", ct);
        delete.StatusCode.ShouldBe(HttpStatusCode.OK);
        JsonDocument.Parse(await delete.Content.ReadAsStringAsync(ct))
            .RootElement.GetProperty("success").GetBoolean().ShouldBeTrue();
    }

    [Fact(Timeout = 10_000)]
    public async Task SessionSchedule_OmittingOptionalFieldsIsAccepted()
    {
        var ct = TestContext.Current.CancellationToken;

        // Priority and filterPlan are optional precisely so a hand-written caller need not supply them.
        // Enums cross this wire as ORDINALS (no string-enum conversion is configured), so a caller that
        // cannot omit priority would have to guess the number.
        var body = new StringContent(
            """
            [{"targetName":"M31","targetRA":0.712,"targetDec":41.27,
              "start":"2026-07-26T22:00:00+00:00","durationMinutes":60,"acrossMeridian":false}]
            """,
            System.Text.Encoding.UTF8, "application/json");

        var post = await _client.PostAsync("/api/v1/session/schedule", body, ct);
        post.StatusCode.ShouldBe(HttpStatusCode.OK);
        JsonDocument.Parse(await post.Content.ReadAsStringAsync(ct))
            .RootElement.GetProperty("success").GetBoolean().ShouldBeTrue();

        (await _client.DeleteAsync("/api/v1/session/schedule", ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact(Timeout = 10_000)]
    public async Task SessionSchedule_WithAStringEnumIsRejectedCleanly()
    {
        var ct = TestContext.Current.CancellationToken;

        // Documents the contract rather than wishing it were otherwise: "priority":"Normal" is NOT
        // valid here, and must fail as an in-band 400 rather than an unhandled 500.
        var body = new StringContent(
            """
            [{"targetName":"M31","targetRA":0.712,"targetDec":41.27,
              "start":"2026-07-26T22:00:00+00:00","durationMinutes":60,"acrossMeridian":false,
              "priority":"Normal"}]
            """,
            System.Text.Encoding.UTF8, "application/json");

        var post = await _client.PostAsync("/api/v1/session/schedule", body, ct);

        var doc = JsonDocument.Parse(await post.Content.ReadAsStringAsync(ct));
        doc.RootElement.GetProperty("success").GetBoolean().ShouldBeFalse();
        doc.RootElement.GetProperty("statusCode").GetInt32().ShouldBe(400);
    }

    // NOTE: the deep-telemetry arrays added to SessionStateDto (cooling / focus history / active
    // V-curve / exposure log) are NOT asserted here. /state 404s without a running session, and
    // standing one up is the session suite's job. Their serialization through the real
    // HostingJsonContext -- including the all-NaN worst case -- is pinned by HostingWireNumberTests,
    // and their projection back onto ISessionTelemetry by RemoteSessionMirrorTests.
}
