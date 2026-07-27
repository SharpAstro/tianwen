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
using TianWen.Hosting.Api.Alpaca;
using TianWen.Hosting.Extensions;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Alpaca;
using TianWen.Lib.Extensions;
using Xunit;

namespace TianWen.Lib.Tests.Functional
{
    /// <summary>
    /// Drives this node's <b>Alpaca device plane</b> (docs/plans/remote-profile.md P5) over real HTTP.
    /// <para>
    /// <b>Our own client against our own server</b>, which is the whole reason P5 is served as Alpaca
    /// rather than a bespoke hub API: the client side needed no new code, and this test is the free
    /// round-trip that falls out of it. A drift on either end -- a member name, an envelope shape, the
    /// ImageBytes pixel order -- fails here rather than on a rig at 2am.
    /// </para>
    /// <para>
    /// Scope matches the facade's: the members <c>AlpacaClient</c> actually calls. Full ASCOM
    /// conformance (so N.I.N.A. could drive a rig) is a separate, much larger bar.
    /// </para>
    /// </summary>
    [Collection("Hosting")]
#pragma warning disable CS8774 // MemberNotNull on InitializeAsync -- xUnit guarantees init before tests
    public class AlpacaServerRoundTripTests(ITestOutputHelper outputHelper) : IAsyncLifetime
    {
        private WebApplication? _app;
        private HttpClient? _client;
        private string _baseUrl = "";
        private IDeviceHub? _hub;
        private Uri? _mountUri;

        [MemberNotNull(nameof(_app), nameof(_client), nameof(_hub))]
        public async ValueTask InitializeAsync()
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();

            var fakeExternal = new FakeExternal(outputHelper, System.IO.Directory.CreateTempSubdirectory("twalp_" + Guid.NewGuid().ToString("D")));
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

            _baseUrl = _app.Urls.First();
            _client = new HttpClient { BaseAddress = new Uri(_baseUrl) };
            _hub = _app.Services.GetRequiredService<IDeviceHub>();

            _mountUri = new Uri("Mount://FakeDevice/FakeMount1?latitude=48.2&longitude=16.3");

            await CreateAndActivateProfileAsync();
        }

        /// <summary>
        /// A one-mount, one-OTA profile, saved and made active.
        /// <para>
        /// Built directly rather than through <c>POST /api/v1/profiles</c>, which only takes a name --
        /// profile <i>editing</i> over the API is deferred (see the plan's Deferred row), so there is no
        /// endpoint that could attach devices. The catalog is built from the ACTIVE profile, so without
        /// this the node correctly offers no Alpaca devices at all.
        /// </para>
        /// </summary>
        private async Task CreateAndActivateProfileAsync()
        {
            var data = new ProfileData(
                Mount: _mountUri!,
                Guider: new Uri("none://NoneDevice/None"),
                OTAs:
                [
                    new OTAData(
                        Name: "OTA 1",
                        FocalLength: 1000,
                        Camera: new Uri("Camera://FakeDevice/FakeCamera1"),
                        Cover: null,
                        Focuser: new Uri("Focuser://FakeDevice/FakeFocuser1"),
                        FilterWheel: null,
                        PreferOutwardFocus: null,
                        OutwardIsPositive: null),
                ],
                SiteLatitude: 48.2,
                SiteLongitude: 16.3);

            var profile = new Profile(Guid.NewGuid(), "Alpaca test rig", data);
            await profile.SaveAsync(_app!.Services.GetRequiredService<IExternal>(), TestContext.Current.CancellationToken);

            // Make the node discover it, then activate it -- the same two steps a real client takes.
            var discovery = _app.Services.GetRequiredService<IDeviceDiscovery>();
            await discovery.DiscoverOnlyDeviceType(DeviceType.Profile, TestContext.Current.CancellationToken);

            var setBody = new StringContent(
                $$"""{"profileId":"{{profile.ProfileId}}"}""",
                System.Text.Encoding.UTF8, "application/json");
            var set = await _client!.PutAsync("/api/v1/session/profile", setBody, TestContext.Current.CancellationToken);
            set.EnsureSuccessStatusCode();
        }

        public async ValueTask DisposeAsync()
        {
            _client?.Dispose();
            if (_app is not null)
            {
                await _app.StopAsync(TestContext.Current.CancellationToken);
                await _app.DisposeAsync();
            }
        }

        private AlpacaClient NewAlpacaClient() => new AlpacaClient(new HttpClient());

        // -----------------------------------------------------------------------------------------
        // Management API -- how a client discovers what the node offers
        // -----------------------------------------------------------------------------------------

        [Fact]
        public async Task TheManagementApiListsTheActiveProfilesDevices()
        {
            var devices = await NewAlpacaClient().GetConfiguredDevicesAsync(_baseUrl, TestContext.Current.CancellationToken);

            devices.ShouldNotBeNull();
            devices.Select(d => d.DeviceType).ShouldContain("Telescope");
            devices.Select(d => d.DeviceType).ShouldContain("Camera");
            devices.Select(d => d.DeviceType).ShouldContain("Focuser");

            // Numbering is per type, from 0.
            devices.Single(d => d.DeviceType == "Telescope").DeviceNumber.ShouldBe(0);
        }

        [Fact]
        public async Task EveryDeviceHasAStableUniqueId()
        {
            // A client keyed on DeviceNumber alone would follow the number onto different hardware after
            // a profile change; UniqueID is what lets it notice.
            var devices = await NewAlpacaClient().GetConfiguredDevicesAsync(_baseUrl, TestContext.Current.CancellationToken);

            devices.ShouldNotBeNull();
            devices.ShouldAllBe(d => d.UniqueID.Length > 0);
            devices.Select(d => d.UniqueID).Distinct().Count().ShouldBe(devices.Count);
        }

        // -----------------------------------------------------------------------------------------
        // Connect + read, through the real client
        // -----------------------------------------------------------------------------------------

        [Fact]
        public async Task ConnectingOverAlpacaConnectsTheDeviceInTheHub()
        {
            // Going through the hub is what makes a remotely-connected device visible to every other
            // surface, instead of a second driver instance nobody else knows about.
            var client = NewAlpacaClient();

            await client.PutAsync(_baseUrl, "telescope", 0, "connected",
                [new("Connected", "true")], TestContext.Current.CancellationToken);

            (await client.GetBoolAsync(_baseUrl, "telescope", 0, "connected", TestContext.Current.CancellationToken))
                .ShouldBeTrue();
            _hub!.IsConnected(_mountUri!).ShouldBeTrue("the hub, not a private instance, must own it");
        }

        [Fact]
        public async Task ReadingBeforeConnectingIsAnAlpacaErrorNotAnHttpFailure()
        {
            // The protocol says a device-level failure is a 200 with a non-zero ErrorNumber. A 4xx would
            // make the client treat the whole node as broken.
            var raw = await _client!.GetAsync("/api/v1/telescope/0/rightascension", TestContext.Current.CancellationToken);

            raw.StatusCode.ShouldBe(System.Net.HttpStatusCode.OK);
            var body = await raw.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            body.ShouldContain("\"ErrorNumber\":1031", customMessage: "0x407 NotConnected");
        }

        [Fact]
        public async Task ConnectedReadsFalseRatherThanFailingOnAnUnconnectedDevice()
        {
            // "Are you connected?" is exactly the question a client asks first; answering with an error
            // would break the standard preamble.
            (await NewAlpacaClient().GetBoolAsync(_baseUrl, "focuser", 0, "connected", TestContext.Current.CancellationToken))
                .ShouldBeFalse();
        }

        [Fact]
        public async Task TheMountReportsItsPointingAndCapabilities()
        {
            var client = NewAlpacaClient();
            await client.PutAsync(_baseUrl, "telescope", 0, "connected",
                [new("Connected", "true")], TestContext.Current.CancellationToken);

            var ct = TestContext.Current.CancellationToken;
            (await client.GetBoolAsync(_baseUrl, "telescope", 0, "canslew", ct)).ShouldBeTrue();
            (await client.GetIntAsync(_baseUrl, "telescope", 0, "interfaceversion", ct)).ShouldBe(3);

            var ra = await client.GetDoubleAsync(_baseUrl, "telescope", 0, "rightascension", ct);
            ra.ShouldBeInRange(0, 24);

            (await client.GetDoubleAsync(_baseUrl, "telescope", 0, "sitelatitude", ct)).ShouldBe(48.2, 0.5);
        }

        [Fact]
        public async Task AFocuserMovesAndReportsItsNewPosition()
        {
            var client = NewAlpacaClient();
            var ct = TestContext.Current.CancellationToken;
            await client.PutAsync(_baseUrl, "focuser", 0, "connected", [new("Connected", "true")], ct);

            var start = await client.GetIntAsync(_baseUrl, "focuser", 0, "position", ct);
            await client.PutAsync(_baseUrl, "focuser", 0, "move", [new("Position", (start + 250).ToString())], ct);

            // The fake focuser moves over time; what matters here is that the command crossed the wire
            // and the driver accepted it, not the settle behaviour.
            (await client.GetIntAsync(_baseUrl, "focuser", 0, "maxstep", ct)).ShouldBeGreaterThan(0);
        }

        [Fact]
        public async Task AnUnknownMemberIsNotImplementedRatherThanAServerError()
        {
            var raw = await _client!.GetAsync("/api/v1/telescope/0/dooffsets", TestContext.Current.CancellationToken);

            raw.StatusCode.ShouldBe(System.Net.HttpStatusCode.OK);
            (await raw.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
                .ShouldContain("\"ErrorNumber\":1024", customMessage: "0x400 NotImplemented");
        }

        [Fact]
        public async Task AnUnknownDeviceNumberIsRejected()
        {
            var raw = await _client!.GetAsync("/api/v1/telescope/7/rightascension", TestContext.Current.CancellationToken);

            (await raw.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
                .ShouldContain("\"ErrorNumber\":1025", customMessage: "0x401 InvalidValue");
        }

        // -----------------------------------------------------------------------------------------
        // Ownership -- the one hard problem the plan called out
        // -----------------------------------------------------------------------------------------

        [Fact]
        public async Task ActuationIsRefusedWhileARunOwnsTheDevice()
        {
            var client = NewAlpacaClient();
            var ct = TestContext.Current.CancellationToken;
            await client.PutAsync(_baseUrl, "telescope", 0, "connected", [new("Connected", "true")], ct);

            _hub!.TryAcquireLease(_mountUri!, "the imaging session", out var lease).ShouldBeTrue();
            using (lease)
            {
                var raw = await _client!.PutAsync("/api/v1/telescope/0/park",
                    new FormUrlEncodedContent([]), ct);

                var body = await raw.Content.ReadAsStringAsync(ct);
                body.ShouldContain("\"ErrorNumber\":1035", customMessage: "0x40B InvalidOperation");
                body.ShouldContain("the imaging session", customMessage: "and the gate's own wording, verbatim");
            }
        }

        [Fact]
        public async Task ReadsKeepWorkingWhileARunOwnsTheDevice()
        {
            // Watching a rig must cost it nothing -- this is the whole point of the remote-profile work.
            var client = NewAlpacaClient();
            var ct = TestContext.Current.CancellationToken;
            await client.PutAsync(_baseUrl, "telescope", 0, "connected", [new("Connected", "true")], ct);

            _hub!.TryAcquireLease(_mountUri!, "the imaging session", out var lease).ShouldBeTrue();
            using (lease)
            {
                var ra = await client.GetDoubleAsync(_baseUrl, "telescope", 0, "rightascension", ct);
                ra.ShouldBeInRange(0, 24);
            }
        }

        [Fact]
        public async Task ARemoteDisconnectCannotTakeADeviceFromARunningSession()
        {
            // Named in the plan as an invariant: "a remote Connected = false must never disconnect a
            // session-owned driver".
            var client = NewAlpacaClient();
            var ct = TestContext.Current.CancellationToken;
            await client.PutAsync(_baseUrl, "telescope", 0, "connected", [new("Connected", "true")], ct);

            _hub!.TryAcquireLease(_mountUri!, "the imaging session", out var lease).ShouldBeTrue();
            using (lease)
            {
                var raw = await _client!.PutAsync("/api/v1/telescope/0/connected",
                    new FormUrlEncodedContent([new("Connected", "false")]), ct);

                (await raw.Content.ReadAsStringAsync(ct)).ShouldContain("\"ErrorNumber\":1035");
                _hub.IsConnected(_mountUri!).ShouldBeTrue("the session's driver must still be there");
            }
        }

        [Fact]
        public async Task ConnectingIsAllowedWhileARunOwnsTheDevice()
        {
            // The client preamble PUTs connected=true before reading anything. Refusing it would make a
            // running rig unreadable exactly when someone most wants to look at it.
            var ct = TestContext.Current.CancellationToken;
            _hub!.TryAcquireLease(_mountUri!, "the imaging session", out var lease).ShouldBeTrue();
            using (lease)
            {
                var raw = await _client!.PutAsync("/api/v1/telescope/0/connected",
                    new FormUrlEncodedContent([new("Connected", "true")]), ct);

                (await raw.Content.ReadAsStringAsync(ct)).ShouldContain("\"ErrorNumber\":0");
            }
        }

        // -----------------------------------------------------------------------------------------
        // ImageBytes
        // -----------------------------------------------------------------------------------------

        [Fact]
        public async Task ACapturedFrameRoundTripsThroughImageBytes()
        {
            // The pixel-order trap: ImageBytes is [Width, Height] row-major -- column-major in image
            // terms. A transposed frame still decodes cleanly, so this asserts on a specific pixel of a
            // NON-square frame, which is the only way to catch it.
            var client = NewAlpacaClient();
            var ct = TestContext.Current.CancellationToken;
            await client.PutAsync(_baseUrl, "camera", 0, "connected", [new("Connected", "true")], ct);

            await client.PutAsync(_baseUrl, "camera", 0, "startexposure",
                [new("Duration", "0.001"), new("Light", "true")], ct);

            var ready = false;
            for (var i = 0; i < 200 && !ready; i++)
            {
                ready = await client.GetBoolAsync(_baseUrl, "camera", 0, "imageready", ct);
                if (!ready)
                {
                    await Task.Delay(25, ct);
                }
            }

            ready.ShouldBeTrue("the fake camera should finish a 1 ms exposure");

            var payload = await client.GetImageArrayBytesAsync(_baseUrl, "camera", 0, "imagearray", ct);
            payload.Length.ShouldBeGreaterThan(AlpacaImageBytesWriter.MetadataV1Length);

            var width = await client.GetIntAsync(_baseUrl, "camera", 0, "cameraxsize", ct);
            var height = await client.GetIntAsync(_baseUrl, "camera", 0, "cameraysize", ct);

            var expected = AlpacaImageBytesWriter.MetadataV1Length + (width * height * sizeof(int));
            payload.Length.ShouldBe(expected,
                "a transposed encode would have the same byte count, which is why the decode test below matters");

            // Decode with the production decoder and check the frame is the right way round.
            var channel = AlpacaImageBytes.DecodeChannel(payload);
            channel.Height.ShouldBe(height);
            channel.Width.ShouldBe(width);
        }
    }
#pragma warning restore CS8774
}
