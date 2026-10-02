using System.IO;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Lib.Devices;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The Best stack panel's telescope is remembered between runs of the viewer, which has no profile to read it from (#1159): what is
/// saved comes back, and with nothing saved the panel keeps its defaults.
/// </summary>
public class PlanetaryTelescopePersistenceTests(ITestOutputHelper output)
{
    [Fact]
    public async Task ASavedTelescopeComesBackAndNothingSavedLeavesTheDefaults()
    {
        // A folder of its own: the caller-named one FakeExternal defaults to outlives a run, and its file would read as already saved.
        var root = Directory.CreateTempSubdirectory("tianwen-telescope-");
        var external = new FakeExternal(output, root);
        var ct = TestContext.Current.CancellationToken;

        var fresh = new ViewerState();
        await PlanetaryTelescopePersistence.LoadAsync(fresh, external, ct);
        fresh.PlanetaryApertureMm.ShouldBeNull();
        fresh.PlanetaryDesign.ShouldBe(OpticalDesign.Newtonian);

        var saved = new ViewerState { PlanetaryApertureMm = 203, PlanetaryDesign = OpticalDesign.SCT };
        await PlanetaryTelescopePersistence.SaveAsync(saved, external, ct);

        var restored = new ViewerState();
        await PlanetaryTelescopePersistence.LoadAsync(restored, external, ct);
        restored.PlanetaryApertureMm.ShouldBe(203);
        restored.PlanetaryDesign.ShouldBe(OpticalDesign.SCT);
        root.Delete(recursive: true);
    }

    [Fact]
    public void TheApertureStepsThroughTheCommonOnesAndBelowTheSmallestToNone()
    {
        var state = new ViewerState();
        state.SteppedAperture(up: true).ShouldBe(60);
        state.PlanetaryApertureMm = 250;
        state.SteppedAperture(up: true).ShouldBe(254);
        state.SteppedAperture(up: false).ShouldBe(235);
        state.PlanetaryApertureMm = 60;
        state.SteppedAperture(up: false).ShouldBeNull();
        state.PlanetaryApertureMm = 508;
        state.SteppedAperture(up: true).ShouldBe(508);
    }
}
