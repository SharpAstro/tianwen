using System;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shouldly;
using TianWen.Lib.Devices;
using TianWen.Lib.Extensions;
using TianWen.Lib.Hosting;
using TianWen.Lib.Hosting.Extensions;
using TianWen.Lib.Tests;
using Xunit;

namespace TianWen.Lib.Tests.Functional;

[Collection("Hosting")]
#pragma warning disable CS8774 // MemberNotNull on InitializeAsync — xUnit guarantees init before tests
#pragma warning disable CS8602 // Dereference of possibly null — same reason
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

        // Register TianWen services with fake devices (mirrors CLI registration chain)
        builder.Services.AddSingleton<IExternal>(sp =>
        {
            var dir = System.IO.Directory.CreateDirectory(
                System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("D")));
            return new FakeExternal(outputHelper, dir);
        });
        builder.Services.AddAstrometry();
        builder.Services.AddFake();
        builder.Services.AddDevices();
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
}
