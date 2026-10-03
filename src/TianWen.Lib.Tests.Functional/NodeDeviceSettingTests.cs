using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using System.Web;
using TianWen.Hosting.Dto;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Fake;
using TianWen.Lib.Devices.Weather;
using TianWen.Lib.Extensions;
using TianWen.RemoteClient;
using Xunit;

namespace TianWen.Lib.Tests.Functional;

/// <summary>
/// A device's setting committed through the node, as the Equipment tab's text field commits it (P3 part 4 of
/// docs/plans/hardware-in-the-server.md, #930): a masked one (an API key) into the node's credential store, never
/// crossing back; any other onto the device's URI in the profile, through the one writer; neither from the LAN. The
/// node here keeps its secrets in a file under the test's own data folder, never the user's vault.
/// </summary>
[Collection("Hosting")]
#pragma warning disable CS8774 // MemberNotNull on InitializeAsync; xUnit guarantees init before tests
#pragma warning disable CS8602 // Dereference of possibly null; same reason
public class NodeDeviceSettingTests(ITestOutputHelper output) : IAsyncLifetime
{
    private static readonly Guid Rig = Guid.Parse("7e57ab1e-0b0e-4e5d-9a5e-000000000934");
    private static readonly OpenWeatherMapDevice Weather = new OpenWeatherMapDevice();

    private NodeHarness? _node;

    [System.Diagnostics.CodeAnalysis.MemberNotNull(nameof(_node))]
    // With the weather source tianwen-server composes, whose device carries a masked setting (its API key).
    public async ValueTask InitializeAsync() => _node = await NodeHarness.StartAsync(output, TestContext.Current.CancellationToken,
        services => services.AddOpenWeatherMap(), onItsSocket: true);

    public async ValueTask DisposeAsync()
    {
        if (_node is not null)
        {
            await _node.DisposeAsync();
        }
    }

    private TianWenNodeClient Client => new TianWenNodeClient(_node.Client);

    private ICredentialStore Store => _node.App.Services.GetRequiredService<ICredentialStore>();

    [Fact(Timeout = 30_000)]
    public async Task AMaskedSettingGoesIntoTheStoreAndNeverComesBack()
    {
        var ct = TestContext.Current.CancellationToken;

        using var response = await _node.Client.PutAsJsonAsync("/api/v1/devices/setting",
            new DeviceSettingRequestDto { DeviceUri = Weather.DeviceUri.ToString(), Key = "apiKey", Value = "sk-123", ProfileId = Rig }, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        body.ShouldNotContain("sk-123", customMessage: "a secret crossed back over the wire");
        var envelope = System.Text.Json.JsonSerializer.Deserialize(body, HostingJsonContext.Default.ResponseEnvelopeDeviceSettingDto).ShouldNotBeNull(body);
        var committed = envelope.Response.ShouldNotBeNull($"{(int)response.StatusCode}: {body}");
        committed.Secret.ShouldBeTrue();
        HttpUtility.ParseQueryString(new Uri(committed.DeviceUri).Query)["apiKey"].ShouldBeNull("a secret on the URI would be written into the profile");
        Store.Get(Weather.CredentialKey).ShouldBe("sk-123");
        (await Client.GetDeviceSecretAsync(Weather.DeviceUri, "apiKey", ct)).Value.ShouldNotBeNull().IsSet.ShouldBeTrue();
    }

    [Fact(Timeout = 30_000)]
    public async Task ASettingThatIsNoSecretGoesOntoTheDevicesUriInTheProfile()
    {
        var ct = TestContext.Current.CancellationToken;
        var focuser = new FakeDevice(DeviceType.Focuser, 1).DeviceUri;
        var data = new ProfileData(NoneDevice.Instance.DeviceUri, NoneDevice.Instance.DeviceUri,
            [new OTAData("Main", 800, new FakeDevice(DeviceType.Camera, 1).DeviceUri, Cover: null, Focuser: focuser, FilterWheel: null,
                PreferOutwardFocus: null, OutwardIsPositive: null)]);
        await new Profile(Rig, "Settings", data).SaveAsync(_node.External, ct);

        var committed = (await Client.SetDeviceSettingAsync(focuser, DeviceQueryKey.FocuserBacklashIn.Key, "25", Rig, ct)).Value.ShouldNotBeNull();

        committed.Secret.ShouldBeFalse();
        var stored = (await Profile.TryReadStoredAsync(_node.External, Rig, ct)).ShouldNotBeNull();
        committed.Revision.ShouldBe(stored.Revision);
        var onProfile = stored.Profile.Data.ShouldNotBeNull().OTAs[0].Focuser.ShouldNotBeNull();
        HttpUtility.ParseQueryString(onProfile.Query)[DeviceQueryKey.FocuserBacklashIn.Key].ShouldBe("25");
    }

    [Fact(Timeout = 30_000)]
    public async Task ALanClientCannotCommitASetting()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var lan = await NodeHarness.StartAsync(output, ct);

        var refused = await new TianWenNodeClient(lan.Client).SetDeviceSettingAsync(Weather.DeviceUri, "apiKey", "sk-lan", profileId: null, ct);

        refused.StatusCode.ShouldBe(403);
        lan.App.Services.GetRequiredService<ICredentialStore>().Get(Weather.CredentialKey).ShouldBeNull();
    }
}
