using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using TianWen.Hosting;
using TianWen.Hosting.Api;
using TianWen.Hosting.Dto;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Discovery;
using TianWen.RemoteClient;
using Xunit;

namespace TianWen.Lib.Tests.Functional;

/// <summary>
/// The node's active profile is node state (P1 of docs/plans/hardware-in-the-server.md, #917, "Pinned serial ports"):
/// kept in the data root, so a node restarted by its keeper is set up as it was, and the source of the pinned serial
/// ports a discovery on the node verifies first. It used to be in memory only, null after every start, and only the
/// GUI registered a pinned-port provider, so the node probed every port blind.
/// </summary>
[Collection("NodeProcesses")]
public class NodeActiveProfileTests(ITestOutputHelper outputHelper)
{
    private static ProfileData WithPorts(string mountPort, string focuserPort) => new ProfileData(
        Mount: new Uri($"Mount://OnStepDevice/onstep?port={mountPort}"),
        Guider: new Uri("Guider://NoneDevice/none"),
        OTAs: [new OTAData("Main", 1000, Camera: new Uri("Camera://FakeDevice/cam"), Cover: null,
            Focuser: new Uri($"Focuser://GeminiDevice/gfoc?port={focuserPort}"), FilterWheel: null, PreferOutwardFocus: null, OutwardIsPositive: null)]);

    [Fact(Timeout = 30_000)]
    public async Task TheNodesPinnedPortsAreItsActiveProfilesAsTheProfileIsNow()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var node = await NodeHarness.StartAsync(outputHelper, ct);
        var pinned = node.App.Services.GetRequiredService<IPinnedSerialPortsProvider>();
        var profile = new Profile(NodeHarness.ProfileId, "Pinned", WithPorts("COM5", "COM7"));
        await profile.SaveAsync(node.External, ct);

        (await pinned.GetPinnedPortsAsync(ct)).ShouldBeEmpty("no active profile, nothing pinned");

        (await new TianWenNodeClient(node.Client).SetActiveProfileAsync(NodeHarness.ProfileId, ct)).IsSuccess.ShouldBeTrue();
        (await pinned.GetPinnedPortsAsync(ct)).Select(static p => p.Port).ShouldBe(["serial:COM5", "serial:COM7"], ignoreOrder: true);

        // Edited by another process (the GUI's Equipment tab): the next pass probes the profile as it is now.
        await profile.WithData(WithPorts("COM9", "COM7")).SaveAsync(node.External, ct);
        (await pinned.GetPinnedPortsAsync(ct)).Select(static p => p.Port).ShouldBe(["serial:COM9", "serial:COM7"], ignoreOrder: true);
    }

    [Fact(Timeout = 30_000)]
    public async Task SharingTheRigKeepsTheActiveProfileAndSwitchingItKeepsTheShare()
    {
        // Both live in one settings file: a change to one must not lose the other.
        var ct = TestContext.Current.CancellationToken;
        await using var node = await NodeHarness.StartAsync(outputHelper, ct,
            services => services.AddSingleton<INodeLogonStart>(new NoLogonEntry()), socketPath: Path.Combine(Directory.CreateTempSubdirectory("tws").FullName, "node.sock"));

        (await new TianWenNodeClient(node.Client).SetActiveProfileAsync(NodeHarness.ProfileId, ct)).IsSuccess.ShouldBeTrue();
        using (var shared = await node.Client.PutAsJsonAsync("api/v1/node/share", new NodeShareRequest { Shared = true }, HostingJsonContext.Default.NodeShareRequest, ct))
        {
            shared.IsSuccessStatusCode.ShouldBeTrue();
        }

        var file = JsonDocument.Parse(await File.ReadAllTextAsync(NodeSettings.PathIn(node.External.AppDataFolder), ct)).RootElement;
        file.GetProperty("shareOnLan").GetBoolean().ShouldBeTrue();
        file.GetProperty("activeProfileId").GetGuid().ShouldBe(NodeHarness.ProfileId);
    }

    [Fact(Timeout = 120_000)]
    public async Task ANodeRestartedByItsKeeperKeepsItsActiveProfile()
    {
        var ct = TestContext.Current.CancellationToken;
        var profileId = Guid.NewGuid();
        await using var kept = await KeptNode.StartAsync(ct, dataRoot =>
        {
            // A profile the node finds in its own data root, as the GUI would have saved it.
            var profiles = Directory.CreateDirectory(Path.Combine(dataRoot, "Profiles")).FullName;
            var dto = new ProfileDto(profileId, "Kept", WithPorts("COM5", "COM7"));
            return File.WriteAllTextAsync(Path.Combine(profiles, Profile.DeviceIdFromUUID(profileId) + ".json"),
                JsonSerializer.Serialize(dto, Profile.ProfileJsonSerializerContextIndented.ProfileDto), ct);
        });
        var first = await kept.WaitForNodeAsync(static _ => true, ct);
        (await kept.Client.SetActiveProfileAsync(profileId, ct)).IsSuccess.ShouldBeTrue();

        using (var crashing = Process.GetProcessById(first.ProcessId))
        {
            crashing.Kill();
        }
        await kept.WaitForNodeAsync(node => node.ProcessId != first.ProcessId, ct);

        var active = await kept.Client.GetActiveProfileAsync(ct);
        active.Value.ShouldNotBeNull($"the restarted node lost its active profile: {active.Error}").ProfileId.ShouldBe(profileId);
    }

    private sealed class NoLogonEntry : INodeLogonStart
    {
        public bool IsSet => false;

        public void Set(bool startAtLogon, string serverPath)
        {
        }
    }
}
