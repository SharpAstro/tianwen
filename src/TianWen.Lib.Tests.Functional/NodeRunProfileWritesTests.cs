using NSubstitute;
using Shouldly;
using System;
using System.Collections.Immutable;
using System.Threading.Tasks;
using System.Web;
using TianWen.Lib.Astrometry.Focus;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Fake;
using Xunit;
using static TianWen.Lib.Tests.Functional.NodeWait;

namespace TianWen.Lib.Tests.Functional;

/// <summary>
/// What a node's run writes back into the profile it was started from as it ends (P3 part 3 of
/// docs/plans/hardware-in-the-server.md, #930): the per-focuser backlash it inferred, mirrored into the profile's focuser
/// URIs so the next run starts from it, as the GUI does at a session's end.
/// </summary>
[Collection("Hosting")]
#pragma warning disable CS8774 // MemberNotNull on InitializeAsync; xUnit guarantees init before tests
#pragma warning disable CS8602 // Dereference of possibly null; same reason
public class NodeRunProfileWritesTests(ITestOutputHelper output) : IAsyncLifetime
{
    private static readonly Uri Focuser = new FakeDevice(DeviceType.Focuser, 1).DeviceUri;

    private NodeHarness? _node;

    [System.Diagnostics.CodeAnalysis.MemberNotNull(nameof(_node))]
    public async ValueTask InitializeAsync() => _node = await NodeHarness.StartAsync(output, TestContext.Current.CancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (_node is not null)
        {
            await _node.DisposeAsync();
        }
    }

    private static ProfileData Rig => new ProfileData(
        new FakeDevice(DeviceType.Mount, 1).DeviceUri, NoneDevice.Instance.DeviceUri,
        [new OTAData("Main", 800, new FakeDevice(DeviceType.Camera, 1).DeviceUri, Cover: null, Focuser: Focuser, FilterWheel: null,
            PreferOutwardFocus: null, OutwardIsPositive: null)]);

    /// <summary>A run of the node's harness profile, whose session inferred <paramref name="estimates"/>, to its end.</summary>
    private async Task RunToTheEndAsync(ImmutableDictionary<Uri, BacklashEstimateRecord> estimates)
    {
        var ct = TestContext.Current.CancellationToken;
        _node.Factory.OnCreated = controlled => controlled.Session.FocuserBacklashEstimates.Returns(estimates);
        _node.Factory.Initialised.TrySetResult();
        var controlled = await _node.StartSessionAsync(ct);
        controlled.EndsOnItsOwn.SetResult();
        controlled.Finalise.SetResult();
        await controlled.Ended.Task.WaitAsync(ct);
    }

    [Fact(Timeout = 60_000)]
    public async Task AnEndedRunLeavesItsBacklashOnItsProfilesFocuser()
    {
        var ct = TestContext.Current.CancellationToken;
        await new Profile(NodeHarness.ProfileId, "Rig", Rig).SaveAsync(_node.External, ct);

        await RunToTheEndAsync(ImmutableDictionary<Uri, BacklashEstimateRecord>.Empty
            .Add(Focuser, new BacklashEstimateRecord(EwmaIn: 30, EwmaOut: 45, Samples: 5, DateTimeOffset.UnixEpoch)));

        var focuser = await UntilAsync<Uri>("the run's backlash on the focuser's URI", async token =>
        {
            var focuserUri = (await Profile.TryReadStoredAsync(_node.External, NodeHarness.ProfileId, token))?.Profile.Data?.OTAs[0].Focuser;
            var query = focuserUri is null ? null : HttpUtility.ParseQueryString(focuserUri.Query);
            return (query?[DeviceQueryKey.FocuserBacklashIn.Key] is not null ? focuserUri : null, focuserUri?.ToString() ?? "no profile");
        }, ct);
        var backlash = HttpUtility.ParseQueryString(focuser.Query);
        backlash[DeviceQueryKey.FocuserBacklashIn.Key].ShouldBe("30");
        backlash[DeviceQueryKey.FocuserBacklashOut.Key].ShouldBe("45");
    }
}
