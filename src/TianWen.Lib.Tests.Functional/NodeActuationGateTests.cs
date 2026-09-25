using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using System;
using System.Diagnostics.CodeAnalysis;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Hosting.Api;
using TianWen.Lib.Astrometry.Focus;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Fake;
using TianWen.Lib.Sequencing;
using Xunit;

namespace TianWen.Lib.Tests.Functional;

/// <summary>
/// A client over the API cannot command hardware a run is driving, and cannot change or delete the profile
/// out from under it (P0b items 8 and 18 of docs/plans/hardware-in-the-server.md, #752).
/// <para>
/// The native mount and OTA routes and every ninaAPI actuation route commanded the session's own drivers
/// without asking <see cref="DeviceOwnershipGate"/>, which the Alpaca plane and the GUI both ask, so a
/// client could slew the mount, move a focuser or abort an exposure in the middle of a night. The ninaAPI
/// profile switch skipped <see cref="ProfileSwitchGate"/>, and a profile could be deleted while in use.
/// </para>
/// </summary>
[Collection("Hosting")]
#pragma warning disable CS8774 // MemberNotNull on InitializeAsync; xUnit guarantees init before tests
#pragma warning disable CS8602 // Dereference of possibly null; same reason
public class NodeActuationGateTests(ITestOutputHelper outputHelper) : IAsyncLifetime
{
    private NodeHarness? _harness;
    private DeviceLeaseSet _runLease = DeviceLeaseSet.Empty;

    [MemberNotNull(nameof(_harness))]
    public async ValueTask InitializeAsync() => _harness = await NodeHarness.StartAsync(outputHelper, TestContext.Current.CancellationToken);

    public async ValueTask DisposeAsync()
    {
        _runLease.Dispose();
        if (_harness is not null)
        {
            await _harness.DisposeAsync();
        }
    }

    /// <summary>
    /// A running session with a rig of fake devices, none connected. A route that asks the gate first refuses
    /// before it touches a driver; one that does not finds a disconnected driver and answers something else.
    /// </summary>
    private async Task<ControlledSession> RunningSessionAsync(bool owningItsRig)
    {
        _harness.Factory.Initialised.SetResult();
        var controlled = await _harness.StartSessionAsync(TestContext.Current.CancellationToken);

        var sp = _harness.App.Services;
        var setup = new Setup(
            new Mount(new FakeDevice(DeviceType.Mount, 1), sp),
            new Guider(new FakeDevice(DeviceType.Guider, 1), sp),
            new GuiderSetup(),
            [new OTA(
                "OTA 1",
                1000,
                new Camera(new FakeDevice(DeviceType.Camera, 1), sp),
                Cover: null,
                new Focuser(new FakeDevice(DeviceType.Focuser, 1), sp),
                new FocusDirection(PreferOutward: true, OutwardIsPositive: true),
                new FilterWheel(new FakeDevice(DeviceType.FilterWheel, 1), sp),
                Switches: null)]);
        controlled.Session.Setup.Returns(setup);

        if (owningItsRig)
        {
            // What Session.RunAsync does as it starts: claim every device it drives, for the whole run.
            _runLease = DeviceLeaseSet.Acquire(sp.GetRequiredService<IDeviceHub>(), setup.DeviceUris(), "Session");
        }

        return controlled;
    }

    /// <summary>The envelope's status and error, native (camelCase) or ninaAPI (PascalCase) alike.</summary>
    private async Task<(int Status, string Error)> SendAsync(string method, string path)
    {
        var ct = TestContext.Current.CancellationToken;
        using var response = await _harness.Client.SendAsync(new HttpRequestMessage(new HttpMethod(method), path), ct);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));

        int? status = null;
        var error = "";
        foreach (var property in body.RootElement.EnumerateObject())
        {
            if (property.NameEquals("statusCode") || property.NameEquals("StatusCode"))
            {
                status = property.Value.GetInt32();
            }
            else if (property.NameEquals("error") || property.NameEquals("Error"))
            {
                error = property.Value.GetString() ?? "";
            }
        }

        return (status ?? throw new InvalidOperationException($"{path} answered no envelope"), error);
    }

    public static TheoryData<string, string> ActuationRoutes => new TheoryData<string, string>
    {
        { "POST", "/api/v1/mount/slew?ra=1&dec=2" },
        { "POST", "/api/v1/mount/park" },
        { "POST", "/api/v1/mount/unpark" },
        { "POST", "/api/v1/mount/tracking?on=true" },
        { "POST", "/api/v1/ota/0/focuser/move?position=1000" },
        { "POST", "/api/v1/ota/0/focuser/stop" },
        { "POST", "/api/v1/ota/0/filterwheel/change?position=1" },
        { "GET", "/v2/api/equipment/camera/abort-exposure" },
        { "GET", "/v2/api/equipment/camera/cool?temperature=-10" },
        { "GET", "/v2/api/equipment/camera/warm" },
        { "GET", "/v2/api/equipment/mount/slew?ra=1&dec=2" },
        { "GET", "/v2/api/equipment/mount/slew/stop" },
        { "GET", "/v2/api/equipment/mount/park" },
        { "GET", "/v2/api/equipment/mount/unpark" },
        { "GET", "/v2/api/equipment/mount/tracking?mode=0" },
        { "GET", "/v2/api/equipment/mount/move-axis?direction=east&rate=1" },
        { "GET", "/v2/api/equipment/mount/move-axis/stop" },
        { "GET", "/v2/api/equipment/focuser/move?position=1000" },
        { "GET", "/v2/api/equipment/filterwheel/change-filter?filterId=1" },
        { "GET", "/v2/api/equipment/guider/start" },
        { "GET", "/v2/api/equipment/guider/stop" },
        { "GET", "/v2/api/equipment/guider/clear-calibration" },
    };

    [Theory(Timeout = 30_000)]
    [MemberData(nameof(ActuationRoutes))]
    public async Task ADeviceARunIsDrivingIsNotCommandedOverTheApi(string method, string path)
    {
        await RunningSessionAsync(owningItsRig: true);

        var (status, error) = await SendAsync(method, path);

        status.ShouldBe(409, $"{method} {path} answered: {error}");
        error.ShouldContain("Session", Case.Sensitive, "the refusal names the run that owns the device");
    }

    [Fact(Timeout = 30_000)]
    public async Task ADeviceNoRunOwnsIsNotRefusedByTheGate()
    {
        // The control: the same route over the same rig, with no lease, gets past the gate to the driver
        // (which is not connected here, so it is refused for that instead).
        await RunningSessionAsync(owningItsRig: false);

        var (status, _) = await SendAsync("POST", "/api/v1/mount/slew?ra=1&dec=2");

        status.ShouldNotBe(409);
    }

    [Fact(Timeout = 30_000)]
    public async Task TheNinaProfileSwitchIsRefusedWhileARunIsGoing()
    {
        // The native PUT /session/profile asks ProfileSwitchGate; the ninaAPI switch did not, so Touch N
        // Stars could re-point the node's profile under a running night.
        await RunningSessionAsync(owningItsRig: true);
        var before = _harness.Node.ActiveProfileId;

        var (status, _) = await SendAsync("GET", $"/v2/api/profile/switch?profileid={Guid.NewGuid()}");

        status.ShouldBe(409);
        _harness.Node.ActiveProfileId.ShouldBe(before);
    }

    private async Task<Guid> CreateProfileAsync(string name)
    {
        var ct = TestContext.Current.CancellationToken;
        using var response = await _harness.Client.PostAsJsonAsync("/api/v1/profiles", new { name }, ct);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return body.RootElement.GetProperty("response").GetProperty("profileId").GetGuid();
    }

    [Fact(Timeout = 30_000)]
    public async Task TheNodesActiveProfileCannotBeDeleted()
    {
        var ct = TestContext.Current.CancellationToken;
        var id = await CreateProfileAsync("Active rig");
        (await NodeHarness.EnvelopeStatusAsync(
            _harness.Client.PutAsJsonAsync("/api/v1/session/profile", new { profileId = id }, ct), ct)).ShouldBe(200);

        var (status, _) = await SendAsync("DELETE", $"/api/v1/profiles/{id}");

        status.ShouldBe(409);
    }

    [Fact(Timeout = 30_000)]
    public async Task NoProfileIsDeletedWhileARunIsGoing()
    {
        // The node does not record which profile a run was started from, so while one runs, no delete is
        // safe to make: the one refused could be the one in use.
        var id = await CreateProfileAsync("Another rig");
        await RunningSessionAsync(owningItsRig: true);

        var (status, _) = await SendAsync("DELETE", $"/api/v1/profiles/{id}");

        status.ShouldBe(409);
    }

    [Fact(Timeout = 30_000)]
    public async Task AProfileNothingUsesCanBeDeleted()
    {
        var id = await CreateProfileAsync("Spare rig");

        var (status, error) = await SendAsync("DELETE", $"/api/v1/profiles/{id}");

        status.ShouldBe(200, error);
    }
}
