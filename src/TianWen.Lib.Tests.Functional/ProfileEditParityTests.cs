using Shouldly;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Fake;
using TianWen.Lib.Imaging;
using TianWen.Lib.Sequencing;
using TianWen.RemoteClient;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests.Functional;

/// <summary>
/// Every edit the GUI's Equipment tab makes to a profile, made through the node instead, stores exactly what the GUI's own
/// save stores (P3 part 5 of docs/plans/hardware-in-the-server.md, #930): the edit computed by the same
/// <see cref="EquipmentActions"/> transform, sent whole over the socket, and compared by revision, which is the hash of the
/// stored bytes. So when P6 cuts the GUI over, each write site keeps its transform and swaps only its save.
/// </summary>
[Collection("Hosting")]
#pragma warning disable CS8774 // MemberNotNull on InitializeAsync; xUnit guarantees init before tests
#pragma warning disable CS8602 // Dereference of possibly null; same reason
public class ProfileEditParityTests(ITestOutputHelper output) : IAsyncLifetime
{
    private static readonly Guid Rig = Guid.Parse("7e57ab1e-0b0e-4e5d-9a5e-000000000935");

    private static readonly ProfileData Seeded = new ProfileData(
        new FakeDevice(DeviceType.Mount, 1).DeviceUri,
        NoneDevice.Instance.DeviceUri,
        [
            new OTAData("Main", 800, new FakeDevice(DeviceType.Camera, 1).DeviceUri, Cover: null,
                Focuser: new FakeDevice(DeviceType.Focuser, 1).DeviceUri, FilterWheel: new FakeDevice(DeviceType.FilterWheel, 1).DeviceUri,
                PreferOutwardFocus: null, OutwardIsPositive: null),
            new OTAData("Guide", 240, NoneDevice.Instance.DeviceUri, Cover: null, Focuser: null, FilterWheel: null,
                PreferOutwardFocus: null, OutwardIsPositive: null),
        ]);

    /// <summary>
    /// The transform behind each of the Equipment tab's persisting write sites (AppSignalHandler.Equipment.cs and the
    /// panels that post UpdateProfileSignal), by the name of the site.
    /// </summary>
    private static readonly Dictionary<string, Func<ProfileData, ProfileData>> Edits = new Dictionary<string, Func<ProfileData, ProfileData>>
    {
        ["site"] = data => EquipmentActions.SetSite(data, -37.8, 144.9, 30),
        ["site tie-breaker"] = data => EquipmentActions.SetSiteTieBreaker(data, SiteTieBreaker.Profile),
        ["mount limits"] = data => EquipmentActions.SetMountLimits(data, new MountLimitConfiguration(Enabled: true, MeridianWarnMinutes: 15)),
        ["guider focal length"] = data => data with { GuiderFocalLength = 240 },
        ["add OTA"] = data => EquipmentActions.AddOTA(data, new OTAData("Telescope #2", 1000, NoneDevice.Instance.DeviceUri, Cover: null, Focuser: null,
            FilterWheel: null, PreferOutwardFocus: null, OutwardIsPositive: null)),
        ["remove OTA"] = data => EquipmentActions.RemoveOTA(data, 1),
        ["edit OTA"] = data => EquipmentActions.UpdateOTA(data, 0, name: "Refractor", focalLength: 480, aperture: 80, opticalDesign: OpticalDesign.Refractor),
        ["assign mount"] = data => EquipmentActions.ApplyAssignment(data, new AssignTarget.ProfileLevel("Mount"), DeviceType.Mount,
            new FakeDevice(DeviceType.Mount, 2).DeviceUri),
        ["assign OTA camera"] = data => EquipmentActions.ApplyAssignment(EquipmentActions.UnassignDevice(data, new FakeDevice(DeviceType.Camera, 2).DeviceUri),
            new AssignTarget.OTALevel(1, "Camera"), DeviceType.Camera, new FakeDevice(DeviceType.Camera, 2).DeviceUri),
        ["manual cover"] = data => EquipmentActions.ApplyAssignment(data, new AssignTarget.OTALevel(0, "Cover"), DeviceType.CoverCalibrator,
            new ManualCoverDevice().DeviceUri),
        ["filter table"] = data => EquipmentActions.SetFilterConfig(data, 0, [new InstalledFilter(Filter.Luminance, 0), new InstalledFilter(Filter.Red, 20)]),
        ["device setting"] = data => data.ReplaceDeviceUri(data.OTAs[0].Focuser!,
            DeviceSettingHelper.WithQueryParam(data.OTAs[0].Focuser!, DeviceQueryKey.FocuserBacklashIn.Key, "25")),
    };

    public static TheoryData<string> EditNames() => [.. Edits.Keys];

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

    [Theory(Timeout = 30_000)]
    [MemberData(nameof(EditNames))]
    public async Task AnEditThroughTheNodeStoresWhatTheGuisSaveStores(string edit)
    {
        var ct = TestContext.Current.CancellationToken;
        await new Profile(Rig, "Parity", Seeded).SaveAsync(_node.External, ct);
        var client = new TianWenNodeClient(_node.Client);
        var read = (await client.GetProfileAsync(Rig, ct)).Value.ShouldNotBeNull();

        var edited = Edits[edit](read.Data.ShouldNotBeNull());
        var written = (await client.UpdateProfileAsync(Rig, edited, read.Revision.ShouldNotBeNull(), name: null, ct)).Value.ShouldNotBeNull();

        // What Profile.SaveAsync writes for the same edit, the GUI's save today.
        var guis = new Profile(Rig, "Parity", Edits[edit](Seeded)).ComputeRevision();
        written.Revision.ShouldBe(guis, $"the node stored the '{edit}' edit differently from the GUI's save");
        written.Revision.ShouldNotBe(read.Revision, $"the '{edit}' edit changed nothing, so it proves nothing");
        (await Profile.TryReadStoredAsync(_node.External, Rig, ct)).ShouldNotBeNull().Revision.ShouldBe(guis);
    }
}
