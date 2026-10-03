using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using System;
using System.Collections.Specialized;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Hosting.Dto;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Fake;
using TianWen.RemoteClient;
using Xunit;
using static TianWen.Lib.Tests.Functional.NodeWait;

namespace TianWen.Lib.Tests.Functional;

/// <summary>
/// What the node writes into a profile on its own, through its one writer, as the GUI's Equipment tab does (P3 part 2 of
/// docs/plans/hardware-in-the-server.md, #930): the discovery job reconciles every stored profile, connecting the active
/// profile's mount reconciles the site, connecting its camera records the sensor, and an edited site goes to the
/// connected mount when the profile wins. The rules are Lib's, one copy for the GUI and the node.
/// </summary>
[Collection("Hosting")]
#pragma warning disable CS8774 // MemberNotNull on InitializeAsync; xUnit guarantees init before tests
#pragma warning disable CS8602 // Dereference of possibly null; same reason
public class NodeProfileOwnWritesTests(ITestOutputHelper output) : IAsyncLifetime
{
    private static readonly Guid Rig = Guid.Parse("7e57ab1e-0b0e-4e5d-9a5e-000000000932");
    private static readonly Uri Mount = new FakeDevice(DeviceType.Mount, 1).DeviceUri;
    private static readonly Uri Camera = new FakeDevice(DeviceType.Camera, 1).DeviceUri;

    /// <summary>Where the fake mount says it is until it is told otherwise.</summary>
    private static readonly SiteCoordinates FakeMountsSite = new SiteCoordinates(48.2, 16.3, 200);
    private static readonly SiteCoordinates Melbourne = new SiteCoordinates(-37.8, 144.9, 30);
    private static readonly SiteCoordinates Greenwich = new SiteCoordinates(51.48, 0, 46);

    private NodeHarness? _node;

    [System.Diagnostics.CodeAnalysis.MemberNotNull(nameof(_node))]
    public async ValueTask InitializeAsync() => _node = await NodeHarness.StartAsync(output, TestContext.Current.CancellationToken,
        onItsSocket: true);

    public async ValueTask DisposeAsync()
    {
        if (_node is not null)
        {
            await _node.DisposeAsync();
        }
    }

    private TianWenNodeClient Client => new TianWenNodeClient(_node.Client);

    private IDeviceHub Hub => _node.App.Services.GetRequiredService<IDeviceHub>();

    private static ProfileData RigData(Uri mount, SiteCoordinates? site, SiteTieBreaker tieBreaker = SiteTieBreaker.Mount) => new ProfileData(
        Mount: mount,
        Guider: NoneDevice.Instance.DeviceUri,
        OTAs: [new OTAData("Main", 800, Camera: Camera, Cover: null, Focuser: null, FilterWheel: null, PreferOutwardFocus: null, OutwardIsPositive: null)],
        SiteLatitude: site?.Latitude,
        SiteLongitude: site?.Longitude,
        SiteElevation: site?.Elevation,
        SiteTieBreaker: tieBreaker);

    /// <summary>The profile saved as another process saves it, and made the node's active profile.</summary>
    private async Task<StoredProfile> ActiveAsync(ProfileData data, CancellationToken ct)
    {
        var stored = await new Profile(Rig, "Own writes", data).SaveStoredAsync(_node.External, ct);
        (await Client.SetActiveProfileAsync(Rig, ct)).IsSuccess.ShouldBeTrue();
        return stored;
    }

    /// <summary>The profile as its file holds it: a job's write is done before the job ends.</summary>
    private async Task<StoredProfile> StoredAsync(CancellationToken ct) =>
        (await Profile.TryReadStoredAsync(_node.External, Rig, ct)).ShouldNotBeNull();

    private async Task<JobDto> SucceedsAsync(NodeResult<JobDto> started, CancellationToken ct)
    {
        var job = started.Value.ShouldNotBeNull(started.Error);
        var ended = await UntilAsync<JobDto>($"{job.Kind} job {job.Id} to end", async token =>
        {
            var now = (await Client.GetJobAsync(job.Id, token)).Value;
            return (now is { State: not JobState.Running } ? now : null, now is null ? "not found" : $"{now.State}: {now.Step}");
        }, ct);
        ended.State.ShouldBe(JobState.Succeeded, $"{ended.Kind}: {ended.Error}");
        return ended;
    }

    /// <summary>The fake mount's URI with a site on its query, the legacy place for one and where its discovery puts its own.</summary>
    private static Uri FakeMountAt(SiteCoordinates site) => new FakeDevice(DeviceType.Mount, 1, new NameValueCollection
    {
        { DeviceQueryKey.Latitude.Key, site.Latitude.ToString(CultureInfo.InvariantCulture) },
        { DeviceQueryKey.Longitude.Key, site.Longitude.ToString(CultureInfo.InvariantCulture) },
    }).DeviceUri;

    [Fact(Timeout = 60_000)]
    public async Task ConnectingTheActiveProfilesMountGivesAProfileWithNoSiteTheMountsSite()
    {
        var ct = TestContext.Current.CancellationToken;
        await ActiveAsync(RigData(Mount, site: null), ct);

        var connected = await SucceedsAsync(await Client.ConnectDeviceAsync(Mount, ct), ct);

        connected.Step.ShouldNotBeNull().ShouldContain("the profile took the mount's site");
        (await StoredAsync(ct)).Profile.Data.ShouldNotBeNull().Site.ShouldBe(FakeMountsSite);
    }

    [Fact(Timeout = 60_000)]
    public async Task WhenTheProfileWinsTheMountIsGivenItsSiteAndTheProfileIsLeftAlone()
    {
        var ct = TestContext.Current.CancellationToken;
        var seeded = await ActiveAsync(RigData(Mount, Melbourne, SiteTieBreaker.Profile), ct);

        await SucceedsAsync(await Client.ConnectDeviceAsync(Mount, ct), ct);

        Hub.TryGetConnectedDriver<IMountDriver>(Mount, out var mount).ShouldBeTrue();
        (await mount.ShouldNotBeNull().GetSiteAsync(ct)).ShouldBe(Melbourne);
        (await StoredAsync(ct)).Revision.ShouldBe(seeded.Revision);
    }

    [Fact(Timeout = 60_000)]
    public async Task ConnectingTheActiveProfilesCameraRecordsItsSensorOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        await ActiveAsync(RigData(Mount, FakeMountsSite), ct);

        await SucceedsAsync(await Client.ConnectDeviceAsync(Camera, ct), ct);

        Hub.TryGetConnectedDriver<ICameraDriver>(Camera, out var camera).ShouldBeTrue();
        var captured = await StoredAsync(ct);
        var ota = captured.Profile.Data.ShouldNotBeNull().OTAs[0];
        ota.CameraPixelSizeUm.ShouldBe(camera.ShouldNotBeNull().PixelSizeX);
        ota.CameraSensorWidthPx.ShouldBe(camera.CameraXSize);
        ota.CameraSensorHeightPx.ShouldBe(camera.CameraYSize);

        // A second connect finds the sensor recorded and writes nothing.
        await SucceedsAsync(await Client.DisconnectDeviceAsync(Camera, skipWarmUp: true, ct), ct);
        await SucceedsAsync(await Client.ConnectDeviceAsync(Camera, ct), ct);
        (await StoredAsync(ct)).Revision.ShouldBe(captured.Revision);
    }

    // The legacy shape: a site kept on the mount's URI and nowhere else. The discovery job moves it into the profile, and
    // leaves a profile that is in sync unwritten.
    [Fact(Timeout = 120_000)]
    public async Task TheDiscoveryJobMovesASiteOffTheMountsUriIntoTheProfile()
    {
        var ct = TestContext.Current.CancellationToken;
        await new Profile(Rig, "Legacy", RigData(FakeMountAt(Melbourne), site: null)).SaveAsync(_node.External, ct);
        // In sync: its mount's URI is what the discovery finds (the fake mount announces its site on it).
        var inSync = Guid.Parse("7e57ab1e-0b0e-4e5d-9a5e-000000000933");
        var inSyncBefore = await new Profile(inSync, "In sync", RigData(FakeMountAt(FakeMountsSite), Greenwich)).SaveStoredAsync(_node.External, ct);

        var discovered = await SucceedsAsync(await Client.StartDiscoveryAsync(ct), ct);

        discovered.Step.ShouldNotBeNull().ShouldContain("reconciled 1 profile");
        var site = (await StoredAsync(ct)).Profile.Data.ShouldNotBeNull().Site.ShouldNotBeNull();
        (site.Latitude, site.Longitude).ShouldBe((Melbourne.Latitude, Melbourne.Longitude));
        (await Profile.TryReadStoredAsync(_node.External, inSync, ct)).ShouldNotBeNull().Revision.ShouldBe(inSyncBefore.Revision);
    }

    [Fact(Timeout = 60_000)]
    public async Task AnEditedSiteGoesToTheConnectedMountWhenTheProfileWins()
    {
        var ct = TestContext.Current.CancellationToken;
        await ActiveAsync(RigData(Mount, Melbourne, SiteTieBreaker.Profile), ct);
        await SucceedsAsync(await Client.ConnectDeviceAsync(Mount, ct), ct);
        var read = (await Client.GetProfileAsync(Rig, ct)).Value.ShouldNotBeNull();

        (await Client.UpdateProfileAsync(Rig, read.Data.ShouldNotBeNull().WithSite(Greenwich), read.Revision.ShouldNotBeNull(), null, ct))
            .IsSuccess.ShouldBeTrue();

        Hub.TryGetConnectedDriver<IMountDriver>(Mount, out var mount).ShouldBeTrue();
        (await mount.ShouldNotBeNull().GetSiteAsync(ct)).ShouldBe(Greenwich);
    }

    [Fact(Timeout = 60_000)]
    public async Task AnEditedSiteDoesNotGoToAMountThatWinsOrThatARunHolds()
    {
        var ct = TestContext.Current.CancellationToken;

        // The mount wins: the profile takes its site on connect, and an edit of the profile's does not move the mount.
        await ActiveAsync(RigData(Mount, site: null), ct);
        await SucceedsAsync(await Client.ConnectDeviceAsync(Mount, ct), ct);
        var read = (await Client.GetProfileAsync(Rig, ct)).Value.ShouldNotBeNull();
        (await Client.UpdateProfileAsync(Rig, read.Data.ShouldNotBeNull().WithSite(Greenwich), read.Revision.ShouldNotBeNull(), null, ct))
            .IsSuccess.ShouldBeTrue();
        Hub.TryGetConnectedDriver<IMountDriver>(Mount, out var mount).ShouldBeTrue();
        (await mount.ShouldNotBeNull().GetSiteAsync(ct)).ShouldBe(FakeMountsSite);

        // The profile wins, but a run holds the mount: the run keeps the site it settled on.
        read = (await Client.GetProfileAsync(Rig, ct)).Value.ShouldNotBeNull();
        using var run = DeviceLeaseSet.Acquire(Hub, [Mount], "Session");
        (await Client.UpdateProfileAsync(Rig, read.Data.ShouldNotBeNull() with { SiteTieBreaker = SiteTieBreaker.Profile, SiteLatitude = Melbourne.Latitude },
            read.Revision.ShouldNotBeNull(), null, ct)).IsSuccess.ShouldBeTrue();
        (await mount.GetSiteAsync(ct)).ShouldBe(FakeMountsSite);
    }
}
