using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Hosting;
using TianWen.Hosting.WebSocket;
using TianWen.Lib.Devices;
using Xunit;

namespace TianWen.Lib.Tests.Functional;

/// <summary>
/// A follow connects with no dialogue and SAYS so (#2040): the node's reconcile writes the followed device into the profile
/// and adds one line to its notification feed, which every client's Notifications tab shows.
/// </summary>
[Collection("Hosting")]
public class NodeProfilesFollowTests(ITestOutputHelper outputHelper)
{
    private static Uri GeminiFocuser(string port)
        => new Uri($"Focuser://GeminiFocuserDevice/GeminiFocuser_{port}?port=serial:{port}&firmware=312#Gemini Focuser Pro on {port}");

    [Fact(Timeout = 60_000)]
    public async Task AFocuserFollowedToAnotherPortIsWrittenAndSaidInTheNodesNotifications()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var node = await NodeHarness.StartAsync(outputHelper, ct);
        var (stored, profiles) = await BenchProfileAsync(node, ct);
        await node.Node.SetActiveProfileAsync(stored.Profile.ProfileId, ct);

        (await profiles.ReconcileAllAsync(ct)).ShouldBe(1, "the selected profile follows the focuser");

        var reread = (await Profile.TryReadStoredAsync(node.External, stored.Profile.ProfileId, ct)).ShouldNotBeNull();
        reread.Profile.Data.ShouldNotBeNull().OTAs[0].Focuser.ShouldNotBeNull().DeviceKey.ShouldBe(GeminiFocuser("COM4").DeviceKey);
        node.Node.Notifications.ShouldContain(n => n.Message == "Bench: Gemini Focuser Pro: followed to COM4 (was COM3)",
            string.Join(" | ", node.Node.Notifications.Select(n => n.Message)));
    }

    /// <summary>
    /// The evidence rungs are the SELECTED profile's alone (#2040): a stored profile the node has not selected keeps its
    /// focuser on COM3, and the feed says nothing about it.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task AProfileThatIsNotSelectedIsNotFollowedByTheModelAlone()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var node = await NodeHarness.StartAsync(outputHelper, ct);
        var (stored, profiles) = await BenchProfileAsync(node, ct);
        await node.Node.SetActiveProfileAsync(Guid.NewGuid(), ct);

        (await profiles.ReconcileAllAsync(ct)).ShouldBe(0, "another rig's profile is never rewritten by the model alone");

        var reread = (await Profile.TryReadStoredAsync(node.External, stored.Profile.ProfileId, ct)).ShouldNotBeNull();
        reread.Profile.Data.ShouldNotBeNull().OTAs[0].Focuser.ShouldNotBeNull().DeviceKey.ShouldBe(GeminiFocuser("COM3").DeviceKey);
        node.Node.Notifications.ShouldNotContain(n => n.Message.StartsWith("Bench:", StringComparison.Ordinal),
            string.Join(" | ", node.Node.Notifications.Select(n => n.Message)));
    }

    /// <summary>
    /// With no profile made active (nothing has called SetActiveProfileAsync yet), a node holding ONE profile has that one
    /// selected (the owner, 2026-11-07): there is no other rig its hardware could be.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task ALoneProfileIsSelectedWhileNoneIsActive()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var node = await NodeHarness.StartAsync(outputHelper, ct);
        var (stored, profiles) = await BenchProfileAsync(node, ct);

        (await profiles.ReconcileAllAsync(ct)).ShouldBe(1, "the one profile is the selected one");

        var reread = (await Profile.TryReadStoredAsync(node.External, stored.Profile.ProfileId, ct)).ShouldNotBeNull();
        reread.Profile.Data.ShouldNotBeNull().OTAs[0].Focuser.ShouldNotBeNull().DeviceKey.ShouldBe(GeminiFocuser("COM4").DeviceKey);
    }

    /// <summary>
    /// One unit moved is moved for every profile that holds it (the owner, 2026-11-07): a second profile holding the focuser
    /// the selected one followed follows it too, and each says so in the feed.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task AnotherProfileHoldingTheSameUnitFollowsTheSelectedOne()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var node = await NodeHarness.StartAsync(outputHelper, ct);
        var (stored, profiles) = await BenchProfileAsync(node, ct, "Bench", "Spare");
        await node.Node.SetActiveProfileAsync(stored.Profile.ProfileId, ct);

        (await profiles.ReconcileAllAsync(ct)).ShouldBe(2, "both profiles hold the unit that moved");

        var messages = node.Node.Notifications.Select(n => n.Message).ToList();
        messages.ShouldContain("Bench: Gemini Focuser Pro: followed to COM4 (was COM3)", string.Join(" | ", messages));
        messages.ShouldContain("Spare: Gemini Focuser Pro: followed to COM4 (was COM3)", string.Join(" | ", messages));
    }

    /// <summary>
    /// Stored profiles (one "Bench" unless named), each with a Gemini focuser on COM3, and the node's profile writer over a
    /// discovery that finds it on COM4. The first profile is answered.
    /// </summary>
    private static async Task<(StoredProfile Stored, NodeProfiles Profiles)> BenchProfileAsync(NodeHarness node, CancellationToken ct, params string[] names)
    {
        var camera = new Uri("Camera://FakeDevice/FakeCamera1#Fake Camera 1");
        var data = new ProfileData(NoneDevice.Instance.DeviceUri, NoneDevice.Instance.DeviceUri,
            [new OTAData("Main", 400, camera, null, GeminiFocuser("COM3"), null, null, null)]);
        var stored = new List<StoredProfile>();
        foreach (var name in names is [] ? ["Bench"] : names)
        {
            stored.Add(await new Profile(Guid.NewGuid(), name, data).SaveStoredAsync(node.External, ct));
        }
        var discovery = new FollowDiscovery([.. stored.Select(s => s.Profile)], GeminiFocuser("COM4"));
        var profiles = new NodeProfiles(node.External, discovery, node.App.Services.GetRequiredService<EventHub>(), node.Node,
            SystemTimeProvider.Instance, NullLogger<NodeProfiles>.Instance);
        return (stored[0], profiles);
    }

    /// <summary>Discovery as the reconcile sees it: the stored profiles, and the focuser now on another port.</summary>
    private sealed class FollowDiscovery(Profile[] profiles, Uri focuser) : IDeviceDiscovery
    {
        public IEnumerable<DeviceType> RegisteredDeviceTypes => [DeviceType.Profile, DeviceType.Focuser];

        public IEnumerable<DeviceBase> RegisteredDevices(DeviceType deviceType) => deviceType switch
        {
            DeviceType.Profile => profiles,
            DeviceType.Focuser => [new Found(focuser)],
            _ => [],
        };

        public ValueTask DiscoverAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask DiscoverOnlyDeviceType(DeviceType type, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask<bool> CheckSupportAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(true);
    }

    private sealed record Found(Uri DeviceUri) : DeviceBase(DeviceUri);
}
