using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using System.Threading.Tasks;
using TianWen.Hosting.Dto;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Astrometry.PlateSolve;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Fake;
using TianWen.RemoteClient;
using Xunit;
using static TianWen.Lib.Tests.Functional.NodeWait;

namespace TianWen.Lib.Tests.Functional;

/// <summary>
/// Finding the planet through the node (#1684): a full-sensor still of the capture going on, solved, says where the mount really
/// points and what focal length the scale gives; a sync on its result makes the mount believe the solve, so the next find finds it
/// on target. The solver stands in for a real one, whose blind solve of the fake camera's field runs for minutes.
/// </summary>
[Collection("Hosting")]
public class NodePlanetaryFindTests(ITestOutputHelper outputHelper)
{
    private static readonly FakeDevice Camera = new FakeDevice(DeviceType.Camera, 1);

    private static readonly FakeDevice Mount = new FakeDevice(DeviceType.Mount, 1);

    /// <summary>What the stand-in finds: a field a degree or so off where the test syncs the mount, at 0.756"/px.</summary>
    private static readonly WCS Field = new WCS(10.1, 20.5) { CRPix1 = 2071.5, CRPix2 = 1410.5, CD1_1 = -2.1e-4, CD1_2 = 0, CD2_1 = 0, CD2_2 = -2.1e-4 };

    [Fact(Timeout = 120_000)]
    public async Task AFindSaysWhereTheMountReallyPointsAndASyncOnItPutsTheMountOnTarget()
    {
        var ct = TestContext.Current.CancellationToken;
        var solver = StandInSolver.Answering(Field);
        await using var node = await NodeHarness.StartAsync(outputHelper, ct, services => services.AddSingleton<IPlateSolverFactory>(solver));
        await node.ActivateRigAsync(Camera, Mount, ct);
        var hub = node.App.Services.GetRequiredService<IDeviceHub>();
        hub.TryGetConnectedDriver<IMountDriver>(Mount.DeviceUri, out var mount).ShouldBeTrue();
        await mount.SetTrackingAsync(true, ct);
        await mount.SyncRaDecAsync(10.0, 19.5, ct);
        var client = new TianWenNodeClient(node.Client);
        await using var window = await NodeWindow.OpenAsync(node, outputHelper, ct);

        (await client.FindPlanetGoAsync(new PlanetaryFindGoRequestDto { SyncOnly = true }, ct)).StatusCode.ShouldBe(404, "no capture, no find");
        (await client.StartPlanetaryAsync(new PlanetaryRequestDto { RoiWidth = 320, RoiHeight = 200 }, ct)).IsSuccess.ShouldBeTrue();
        await UntilAsync<string>("frames", async token => ((await client.GetPlanetaryAsync(token)).Value is { FramesReceived: > 0 } ? "on" : null, "waiting"), ct);
        (await client.FindPlanetGoAsync(new PlanetaryFindGoRequestDto { SyncOnly = true }, ct)).StatusCode.ShouldBe(404, "nothing found yet");

        var request = new PlanetaryFindRequestDto { ExposureSeconds = 0.05, Planet = CatalogIndex.Saturn };
        var done = await UntilTheJobSucceedsAsync(client, await client.FindPlanetAsync(request, ct), ct);
        outputHelper.WriteLine(done.Step);
        var found = (await client.GetPlanetaryAsync(ct)).Value.ShouldNotBeNull().Find.ShouldNotBeNull();
        (found.RaJ2000, found.DecJ2000).ShouldBe((10.1, 20.5));
        found.ScaleArcsec.ShouldBe(0.756, 1e-9);
        found.MountOffsetDeg.ShouldNotBeNull().ShouldBeInRange(0.3, 2.0, "the mount was synced about a degree away");
        found.FocalLengthToOffer.ShouldNotBeNull().ShouldNotBe(400, "the scale's focal length, offered against the profile's 400 mm");
        (found.Planet, found.PlanetSeparationDeg.HasValue).ShouldBe((CatalogIndex.Saturn, true));
        solver.Solves.ShouldBe(1);

        await UntilTheJobSucceedsAsync(client, await client.FindPlanetGoAsync(new PlanetaryFindGoRequestDto { SyncOnly = true }, ct), ct);
        (await client.GetPlanetaryAsync(ct)).Value.ShouldNotBeNull().Find.ShouldBeNull("a find is spent once it is gone to");

        await UntilTheJobSucceedsAsync(client, await client.FindPlanetAsync(request, ct), ct);
        var again = (await client.GetPlanetaryAsync(ct)).Value.ShouldNotBeNull().Find.ShouldNotBeNull();
        again.MountOffsetDeg.ShouldNotBeNull().ShouldBeLessThan(0.01, "the mount now believes the solve");

        (await client.StopPlanetaryAsync(ct)).IsSuccess.ShouldBeTrue();
    }
}
