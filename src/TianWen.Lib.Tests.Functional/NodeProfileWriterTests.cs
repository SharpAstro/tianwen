using Shouldly;
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using TianWen.Hosting.Dto;
using TianWen.Lib.Devices;
using TianWen.Lib.Sequencing;
using TianWen.RemoteClient;
using Xunit;
using static TianWen.Lib.Tests.Functional.NodeWait;

namespace TianWen.Lib.Tests.Functional;

/// <summary>
/// The node as the ONE profile writer (P3 part 1 of docs/plans/hardware-in-the-server.md, #930): the whole profile crosses
/// the socket and is stored exactly as sent, an edit made against a revision the file has moved past is refused, every
/// write is pushed, and a client on the LAN reads a profile but cannot change one.
/// </summary>
[Collection("Hosting")]
#pragma warning disable CS8774 // MemberNotNull on InitializeAsync; xUnit guarantees init before tests
#pragma warning disable CS8602 // Dereference of possibly null; same reason
public class NodeProfileWriterTests(ITestOutputHelper output) : IAsyncLifetime
{
    private static readonly Guid Rig = Guid.Parse("7e57ab1e-0b0e-4e5d-9a5e-000000000930");
    private const string RigName = "Every field";

    /// <summary>
    /// A profile with every field set to something other than its default, including all that today's
    /// <see cref="ProfileDetailDto.Equipment"/> leaves out: the guider focuser, the OAG OTA, mount limits, the site
    /// tie-breaker, focus direction and the sensor geometry.
    /// </summary>
    private static readonly ProfileData Everything = new ProfileData(
        Mount: new Uri("Mount://FakeDevice/FakeMount1"),
        Guider: new Uri("Guider://FakeDevice/FakeGuider1"),
        OTAs:
        [
            new OTAData("Main", 800,
                Camera: new Uri("Camera://FakeDevice/FakeCamera1"),
                Cover: new Uri("CoverCalibrator://FakeDevice/FakeCoverCalibrator1"),
                Focuser: new Uri("Focuser://FakeDevice/FakeFocuser1"),
                FilterWheel: new Uri("FilterWheel://FakeDevice/FakeFilterWheel1"),
                PreferOutwardFocus: true, OutwardIsPositive: false, Aperture: 100, OpticalDesign: OpticalDesign.Refractor,
                CameraPixelSizeUm: 3.76, CameraSensorWidthPx: 6248, CameraSensorHeightPx: 4176),
            new OTAData("Guide", 240, Camera: new Uri("Camera://FakeDevice/FakeCamera2"), Cover: null, Focuser: null, FilterWheel: null,
                PreferOutwardFocus: null, OutwardIsPositive: null),
        ],
        GuiderCamera: new Uri("Camera://FakeDevice/FakeCamera2"),
        GuiderFocuser: new Uri("Focuser://FakeDevice/FakeFocuser2"),
        OAG_OTA_Index: 0,
        GuiderFocalLength: 240,
        Weather: new Uri("Weather://FakeDevice/FakeWeather1"),
        SiteLatitude: 48.2,
        SiteLongitude: 16.3,
        SiteElevation: 180.5,
        SiteTieBreaker: SiteTieBreaker.Profile,
        MountLimits: new MountLimitConfiguration(Enabled: true, MeridianWarnMinutes: 12.5, MeridianActionExtraMinutes: 7,
            MeridianResponse: MountLimitResponse.Park, HorizonActionDeg: 15, HorizonWarnExtraDeg: 3, HorizonResponse: MountLimitResponse.Park));

    private NodeHarness? _node;

    [System.Diagnostics.CodeAnalysis.MemberNotNull(nameof(_node))]
    public async ValueTask InitializeAsync() => _node = await NodeHarness.StartAsync(output, TestContext.Current.CancellationToken,
        socketPath: Path.Combine(Directory.CreateTempSubdirectory("tws").FullName, "node.sock"));

    public async ValueTask DisposeAsync()
    {
        if (_node is not null)
        {
            await _node.DisposeAsync();
        }
    }

    private TianWenNodeClient Client => new TianWenNodeClient(_node.Client);

    /// <summary>The profile as another process saves it, the GUI's way until P6: straight to the file.</summary>
    private static Task SeedAsync(IExternal external, ProfileData data) =>
        new Profile(Rig, RigName, data).SaveAsync(external, TestContext.Current.CancellationToken);

    [Fact(Timeout = 30_000)]
    public async Task TheWholeProfileCrossesTheSocketAndIsStoredAsSent()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(_node.External, Everything);

        var read = (await Client.GetProfileAsync(Rig, ct)).Value.ShouldNotBeNull();

        // A revision is the hash of the stored bytes, so equal revisions mean every field crossed and came back.
        read.Revision.ShouldBe(new Profile(Rig, RigName, Everything).ComputeRevision());
        var data = read.Data.ShouldNotBeNull();
        new Profile(Rig, RigName, data).ComputeRevision().ShouldBe(read.Revision, "the profile read over the wire is not the one stored");

        var edited = data with
        {
            GuiderFocuser = new Uri("Focuser://FakeDevice/FakeFocuser3"),
            OAG_OTA_Index = 1,
            SiteTieBreaker = SiteTieBreaker.Mount,
            MountLimits = Everything.MountLimits! with { Enabled = false },
            OTAs = data.OTAs.SetItem(0, data.OTAs[0] with { PreferOutwardFocus = false, CameraPixelSizeUm = 4.63 }),
        };
        var written = (await Client.UpdateProfileAsync(Rig, edited, read.Revision.ShouldNotBeNull(), "Renamed", ct)).Value.ShouldNotBeNull();

        var expected = new Profile(Rig, "Renamed", edited).ComputeRevision();
        written.Revision.ShouldBe(expected);
        written.Name.ShouldBe("Renamed");
        (await Profile.TryReadStoredAsync(_node.External, Rig, ct)).ShouldNotBeNull().Revision.ShouldBe(expected, "the file does not hold what was sent");
    }

    [Fact(Timeout = 30_000)]
    public async Task AnEditMadeAgainstARevisionTheFileHasMovedPastIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(_node.External, Everything);
        var read = (await Client.GetProfileAsync(Rig, ct)).Value.ShouldNotBeNull();

        // Another process changes the file after the client read it.
        await SeedAsync(_node.External, Everything with { GuiderFocalLength = 180 });
        var stale = await Client.UpdateProfileAsync(Rig, read.Data.ShouldNotBeNull() with { SiteElevation = 999 }, read.Revision.ShouldNotBeNull(), null, ct);

        stale.StatusCode.ShouldBe(412, stale.Error);
        var stored = (await Profile.TryReadStoredAsync(_node.External, Rig, ct)).ShouldNotBeNull().Profile.Data.ShouldNotBeNull();
        stored.GuiderFocalLength.ShouldBe(180);
        stored.SiteElevation.ShouldBe(180.5);
    }

    [Fact(Timeout = 30_000)]
    public async Task EveryWriteIsPushedAndListed()
    {
        var ct = TestContext.Current.CancellationToken;
        var pushed = new ConcurrentQueue<ProfileChangedDto>();
        await using var stream = _node.Transport.CreateEventStream(new SystemTimeProvider(), FakeExternal.CreateLogger(output));
        stream.EventReceived += (_, e) =>
        {
            if (ProfileChangedDto.TryFromEvent(e, out var change))
            {
                pushed.Enqueue(change);
            }
        };
        stream.Start(ct);
        await UntilAsync("the event stream to connect", _ => ValueTask.FromResult((stream.IsConnected, "not yet")), ct);

        var created = (await Client.CreateProfileAsync("Pushed", ct)).Value.ShouldNotBeNull();
        await UntilAsync("the create pushed", _ => ValueTask.FromResult((
            pushed.Any(c => c.ProfileId == created.ProfileId && c.Revision == created.Revision && c.Name == "Pushed"), $"{pushed.Count} pushed")), ct);
        (await Client.GetProfilesAsync(ct)).Value.ShouldNotBeNull().ShouldContain(p => p.ProfileId == created.ProfileId);

        var edited = (await Client.UpdateProfileAsync(created.ProfileId, created.Data.ShouldNotBeNull() with { SiteLatitude = 10, SiteLongitude = 20 },
            created.Revision.ShouldNotBeNull(), null, ct)).Value.ShouldNotBeNull();
        edited.Revision.ShouldNotBe(created.Revision);
        await UntilAsync("the edit pushed", _ => ValueTask.FromResult((
            pushed.Any(c => c.ProfileId == created.ProfileId && c.Revision == edited.Revision), $"{pushed.Count} pushed")), ct);

        (await Client.DeleteProfileAsync(created.ProfileId, ct)).IsSuccess.ShouldBeTrue();
        await UntilAsync("the delete pushed", _ => ValueTask.FromResult((
            pushed.Any(c => c.ProfileId == created.ProfileId && c.Deleted), $"{pushed.Count} pushed")), ct);
        (await Client.GetProfilesAsync(ct)).Value.ShouldNotBeNull().ShouldNotContain(p => p.ProfileId == created.ProfileId);
    }

    [Fact(Timeout = 30_000)]
    public async Task AProfileWithNoTelescopeListIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(_node.External, Everything);
        var revision = new Profile(Rig, RigName, Everything).ComputeRevision();

        // Hand-written, as a script would send it: the telescope list left out arrives as no array at all.
        using var body = new StringContent(
            $$"""{"data":{"mount":"Mount://FakeDevice/FakeMount1","guider":"Guider://FakeDevice/FakeGuider1"},"revision":"{{revision}}"}""",
            Encoding.UTF8, "application/json");
        using var response = await _node.Client.PutAsync($"/api/v1/profiles/{Rig}", body, ct);

        ((int)response.StatusCode).ShouldBe(400);
        (await Profile.TryReadStoredAsync(_node.External, Rig, ct)).ShouldNotBeNull().Revision.ShouldBe(revision);
    }

    [Fact(Timeout = 30_000)]
    public async Task ALanClientReadsAProfileButCannotChangeOne()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var lan = await NodeHarness.StartAsync(output, ct);
        await SeedAsync(lan.External, Everything);
        var client = new TianWenNodeClient(lan.Client);

        var read = (await client.GetProfileAsync(Rig, ct)).Value.ShouldNotBeNull();
        read.Data.ShouldNotBeNull();

        (await client.UpdateProfileAsync(Rig, Everything with { SiteElevation = 1 }, read.Revision.ShouldNotBeNull(), null, ct)).StatusCode.ShouldBe(403);
        (await client.CreateProfileAsync("From the LAN", ct)).StatusCode.ShouldBe(403);
        (await client.DeleteProfileAsync(Rig, ct)).StatusCode.ShouldBe(403);
        (await Profile.TryReadStoredAsync(lan.External, Rig, ct)).ShouldNotBeNull().Revision.ShouldBe(read.Revision);
    }
}
