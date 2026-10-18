using System;
using System.Linq;
using System.Threading.Tasks;
using DIR.Lib;
using Shouldly;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Imaging.Planetary;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The orrery's info panel (#1656, docs/plans/atlas-planet-detail.md, "A5: what is open"): a click on a body opens the sky's object
/// panel for it over the orrery, its disc the body as seen from the orrery's centre; Escape closes the panel before the view.
/// </summary>
[Collection("Astrometry")]
public sealed class OrreryPanelTests(ITestOutputHelper output)
{
    private static readonly DateTimeOffset Instant = OrreryTests.Instant;

    /// <summary>The owner's rule: the disc is seen from the centre; the centre itself from the Sun, and the Sun from the Earth.</summary>
    [Theory]
    [InlineData("Jupiter", "Sol", "Sol")]
    [InlineData("Jupiter", "Earth", "Earth")]
    [InlineData("Sol", "Mars", "Mars")]
    [InlineData("Earth", "Earth", "Sol")]
    [InlineData("Mars", "Mars", "Sol")]
    [InlineData("Sol", "Sol", "Earth")]
    public void TheDiscIsSeenFromTheCentre(string selected, string centre, string vantage)
        => OrreryState.PanelVantage(Body(selected), Body(centre)).ShouldBe(Body(vantage));

    private static CatalogIndex Body(string name) => Enum.Parse<CatalogIndex>(name);

    /// <summary>
    /// <see cref="PlanetView.FromVantage"/> against the sky's own aspect: Venus seen from the Sun is full, and seen from the Earth it
    /// has the phase angle and the apparent diameter <see cref="PhysicalEphemeris.Compute"/> gives the sky (within light time).
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task FromTheSunVenusIsFullAndFromTheEarthItIsTheSkysVenus()
    {
        var scene = new OrreryScene();
        scene.Update(Instant);
        await scene.WhenOrbitsBuiltAsync();
        scene.TryFind(CatalogIndex.Venus, out var venus).ShouldBeTrue();
        scene.TryFind(CatalogIndex.Earth, out var earth).ShouldBeTrue();
        var at = (venus.X, venus.Y, venus.Z);

        var fromSun = PlanetView.FromVantage(CatalogIndex.Venus, Instant, at, (0, 0, 0), out var toSunAu).ToAspect(Instant);
        var fromEarth = PlanetView.FromVantage(CatalogIndex.Venus, Instant, at, (earth.X, earth.Y, earth.Z), out var toEarthAu).ToAspect(Instant);
        var sky = PhysicalEphemeris.Compute(CatalogIndex.Venus, Instant);
        var diameter = PlanetView.AngularDiameterArcsec(CatalogIndex.Venus, toEarthAu);
        output.WriteLine($"from the Sun ({toSunAu:F3} AU): phase {fromSun.PhaseAngle:F2} deg; from the Earth ({toEarthAu:F3} AU): phase {fromEarth.PhaseAngle:F2} deg, diameter {diameter:F2}\"; the sky's {sky.PhaseAngle:F2} deg, {sky.AngularDiameterArcsec:F2}\"");

        fromSun.PhaseAngle.ShouldBeLessThan(1e-3);
        fromEarth.PhaseAngle.ShouldBe(sky.PhaseAngle, 0.5);
        diameter.ShouldBe(sky.AngularDiameterArcsec, sky.AngularDiameterArcsec * 0.01);
    }

    /// <summary>
    /// The view opened on Mars keeps Mars's panel; a click on Jupiter, the Sun, the Moon and the Earth each opens that body's panel,
    /// inside the view and above the strip, at 1x and 1.5x (where this framing leaves the Moon undrawn, so there is none to click). The
    /// Earth's says nothing it cannot see of itself: no position, no altitude.
    /// </summary>
    [Theory(Timeout = 120_000)]
    [InlineData(1f)]
    [InlineData(1.5f)]
    public async Task AClickOnABodyOpensItsPanel(float dpiScale)
    {
        using var rig = await OrreryTests.RigAsync(TestContext.Current.CancellationToken, dpiScale);
        OrreryTests.Select(rig, CatalogIndex.Mars);
        rig.Tab.OpenOrrery();
        await OrreryTests.SettleAsync(rig);
        rig.Tab.LastOrreryPanel.ShouldNotBeNull().Index.ShouldBe(CatalogIndex.Mars, "the body the view was opened on lost its panel");

        var clicked = 0;
        foreach (var (body, name) in new[] { (CatalogIndex.Jupiter, "Jupiter"), (CatalogIndex.Sol, "Sun"), (CatalogIndex.Moon, "Moon"), (CatalogIndex.Earth, "Earth") })
        {
            if (rig.Tab.OrreryDrawn.All(d => d.Index != body))
            {
                output.WriteLine($"{name}: not drawn in this framing");
                body.ShouldBe(CatalogIndex.Moon, $"{name} is not drawn");
                dpiScale.ShouldBeGreaterThan(1f, "the Moon is not drawn at 1x");
                continue;
            }
            Click(rig, body);
            clicked++;
            var panel = rig.Tab.LastOrreryPanel.ShouldNotBeNull($"a click on {name} opened no panel");
            output.WriteLine($"{name}: \"{panel.Name}\", disc seen from {rig.Tab.LastOrreryPanelVantage}, {ObjectInfoPanel.DiskLine(in panel)}");
            panel.Index.ShouldBe(body);
            panel.Name.ShouldContain(name);
            rig.Tab.LastPanelDiscRect.ShouldNotBeNull($"{name}'s panel has no disc");

            var box = OrreryTests.PaintedButtons(rig, "InfoPanelBackground")[0];
            box.X.ShouldBeGreaterThanOrEqualTo(0);
            box.Y.ShouldBeGreaterThanOrEqualTo(0);
            (box.X + box.Width).ShouldBeLessThanOrEqualTo(OrreryTests.Width);
            (box.Y + box.Height).ShouldBeLessThanOrEqualTo(OrreryTests.Height - (24f * dpiScale), $"{name}'s panel runs into the strip");
        }
        clicked.ShouldBeGreaterThanOrEqualTo(3);
        var earth = rig.Tab.LastOrreryPanel.ShouldNotBeNull();
        ObjectInfoPanel.CoordinateLine(in earth).ShouldBe("RA -   Dec -");
    }

    /// <summary>
    /// The disc follows the centre: Venus's panel, with the view on the Sun, shows it full; the Earth's panel's Centre puts the camera on
    /// the Earth, and Venus clicked then keeps the camera there and is seen from the Earth, at the sky's phase.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task CentredOnTheEarthVenusIsSeenAsTheSkySeesIt()
    {
        using var rig = await OrreryTests.RigAsync(TestContext.Current.CancellationToken);
        OrreryTests.Select(rig, CatalogIndex.Venus);
        rig.Tab.OpenOrrery();
        await OrreryTests.SettleAsync(rig);
        rig.Tab.LastOrreryPanelVantage.ShouldBe(CatalogIndex.Sol);
        rig.Tab.LastOrreryPanel.ShouldNotBeNull().Aspect.ShouldNotBeNull().PhaseAngle.ShouldBeLessThan(1e-3, "from the Sun Venus is not full");

        Click(rig, CatalogIndex.Earth);
        rig.Tab.State.Orrery.Focus.ShouldBe(CatalogIndex.Earth);
        Press(rig, ObjectInfoPanel.CentreAction);
        rig.Tab.State.Orrery.CentredOnFocus.ShouldBeTrue();
        rig.Tab.State.Orrery.Centre.ShouldBe(CatalogIndex.Earth);
        rig.Tab.LastOrreryPanelVantage.ShouldBe(CatalogIndex.Sol, "the centre itself is seen from the Sun");

        Click(rig, CatalogIndex.Venus);
        rig.Tab.State.Orrery.Centre.ShouldBe(CatalogIndex.Earth, "a click moved the centre");
        rig.Tab.LastOrreryPanelVantage.ShouldBe(CatalogIndex.Earth);
        var phase = rig.Tab.LastOrreryPanel.ShouldNotBeNull().Aspect.ShouldNotBeNull().PhaseAngle;
        var sky = PhysicalEphemeris.Compute(CatalogIndex.Venus, Instant).PhaseAngle;
        output.WriteLine($"Venus from the Earth: phase {phase:F2} deg, the sky's {sky:F2} deg");
        phase.ShouldBe(sky, 0.5);
    }

    /// <summary>A comet's panel: its own name and magnitude, no disc (a comet is a dot).</summary>
    [Fact(Timeout = 120_000)]
    public async Task ACometsPanelHasNoDisc()
    {
        using var rig = await OrreryTests.RigAsync(TestContext.Current.CancellationToken);
        var comet = OrreryTests.SelectComet(rig);
        rig.Tab.OpenOrrery();
        await OrreryTests.SettleAsync(rig);
        var panel = rig.Tab.LastOrreryPanel.ShouldNotBeNull("the comet the view was opened on has no panel");
        panel.Index.ShouldBe(comet.CatalogIndex);
        panel.ObjType.ShouldBe(ObjectType.Comet);
        float.IsNaN(panel.VMag).ShouldBeFalse();
        rig.Tab.LastPanelDiscRect.ShouldBeNull();
        rig.Tab.LastOrreryPanelVantage.ShouldBeNull();
    }

    /// <summary>Escape closes the panel and leaves the view open; a second closes the view. A click on nothing closes the panel too.</summary>
    [Fact(Timeout = 120_000)]
    public async Task EscapeClosesThePanelThenTheView()
    {
        using var rig = await OrreryTests.RigAsync(TestContext.Current.CancellationToken);
        OrreryTests.Select(rig, CatalogIndex.Mars);
        rig.Tab.OpenOrrery();
        await OrreryTests.SettleAsync(rig);
        rig.Tab.State.Orrery.Selected.ShouldBe(CatalogIndex.Mars);

        rig.Router.Handle(new InputEvent.KeyDown(InputKey.Escape)).ShouldBeTrue();
        rig.Render();
        rig.Tab.State.Orrery.Selected.ShouldBeNull();
        rig.Tab.LastOrreryPanel.ShouldBeNull();
        rig.Tab.State.Orrery.IsOpen.ShouldBeTrue("the first Escape closed the view");

        Click(rig, CatalogIndex.Jupiter);
        rig.Tab.State.Orrery.Selected.ShouldBe(CatalogIndex.Jupiter);
        var (x, y) = Nothing(rig);
        rig.Tab.HandleInput(new InputEvent.MouseDown(x, y)).ShouldBeTrue();
        rig.Tab.HandleInput(new InputEvent.MouseUp(x, y)).ShouldBeTrue();
        rig.Render();
        rig.Tab.State.Orrery.Selected.ShouldBeNull("a click on nothing left the panel open");

        rig.Router.Handle(new InputEvent.KeyDown(InputKey.Escape)).ShouldBeTrue();
        rig.Tab.State.Orrery.IsOpen.ShouldBeFalse();
    }

    // A press that does not move, on the body's drawn place: what a click on it is.
    private static void Click(OrreryTests.Rig rig, CatalogIndex body)
    {
        var drawn = rig.Tab.OrreryDrawn.Single(d => d.Index == body);
        rig.Tab.HandleInput(new InputEvent.MouseDown(drawn.X, drawn.Y)).ShouldBeTrue();
        rig.Tab.HandleInput(new InputEvent.MouseUp(drawn.X, drawn.Y)).ShouldBeTrue();
        rig.Render();
    }

    // A press and release on a painted button, through the router, as a pointer would.
    private static void Press(OrreryTests.Rig rig, string action)
    {
        var button = OrreryTests.PaintedButtons(rig, action)[0];
        var (x, y) = (button.X + (button.Width / 2), button.Y + (button.Height / 2));
        rig.Router.Handle(new InputEvent.MouseDown(x, y));
        rig.Router.Handle(new InputEvent.MouseUp(x, y));
        rig.Render();
    }

    // A point of the view at least 40 px from every drawn body, right of where the panel sits and above the strip.
    private static (float X, float Y) Nothing(OrreryTests.Rig rig)
    {
        for (var y = 40f; y < OrreryTests.Height - 80; y += 20)
        {
            for (var x = OrreryTests.Width - 40f; x > OrreryTests.Width / 2; x -= 20)
            {
                if (rig.Tab.OrreryDrawn.All(d => MathF.Sqrt(((d.X - x) * (d.X - x)) + ((d.Y - y) * (d.Y - y))) > d.Radius + 40))
                {
                    return (x, y);
                }
            }
        }
        throw new InvalidOperationException("no empty place in the view");
    }
}
