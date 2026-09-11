using System;
using System.Threading.Tasks;
using DIR.Lib;
using Shouldly;
using TianWen.Lib.Devices;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests
{
    /// <summary>
    /// The atlas's selected-object info panel must swallow a press on its own BLANK area -- not just
    /// its buttons and close X -- or a click meant to read the panel falls through and re-selects
    /// whatever is on the sky map underneath it.
    /// </summary>
    /// <remarks>
    /// Recorded as deliberately open when the viewer's own copy of this panel shipped (the atlas's
    /// panel drew its background/border with two plain <c>Bg</c> fills and no <c>Clickable</c>, so a
    /// press there fell to <c>SkyMapTab.HandleDragStart</c> exactly as if the panel were not there).
    /// Fixed here and in the viewer TOGETHER (<c>ImageRendererBase.SelectionPanel.cs</c>) rather than
    /// one host quietly ahead of the other -- both panels share <see cref="ObjectInfoPanel"/> and both
    /// had the same gap for the same reason.
    /// </remarks>
    [Collection("Astrometry")]
    public class SkyMapInfoPanelClickTests
    {
        private const int ContentW = 640;
        private const int ContentH = 480;

        private static (SkyMapTab<RgbaImage> Tab, RectF32 Content, PlannerState Planner, ITimeProvider Clock)
            BuildTabWithInfoPanel(TianWen.Lib.Astrometry.Catalogs.ICelestialObjectDB db, RgbaImageRenderer renderer)
        {
            const int w = ContentW, h = ContentH;
            var tab = new SkyMapTab<RgbaImage>(renderer) { FontPath = FontResolver.ResolveSystemFont() };

            var now = new DateTimeOffset(2026, 6, 21, 22, 0, 0, TimeSpan.Zero);
            var planner = new PlannerState
            {
                ObjectDb = db,
                SiteLatitude = 45.0,
                SiteLongitude = -75.0,
                SiteTimeZone = TimeSpan.Zero,
                PlanningDate = now,
            };

            // A plain (non-comet, non-mount) position selection, which is what makes the panel take
            // the Goto/View-in-Planner/Pin button row and the 205px no-curve height.
            tab.State.Search.InfoPanel = TianWen.UI.Abstractions.SkyMapInfoPanelData.FromPosition(
                "Test Object", 5.0, 20.0, 45.0, -75.0, now, default);

            return (tab, new RectF32(0, 0, w, h), planner, new FakeTimeProviderWrapper(now));
        }

        /// <summary>
        /// Where the panel's own body sits, at dpiScale 1 -- asked of <see cref="ObjectInfoPanel"/>
        /// rather than restated as literals, which is the same reason <c>DrawInfoPanel</c> stopped
        /// carrying its own: a test holding a third copy of the box would keep passing against a
        /// panel that had moved out from under the press it aims at.
        /// </summary>
        private static (float X, float Y) PanelBody(RectF32 content, float fromTopFraction)
        {
            // A plain (non-comet, non-mount) selection: the six site-bearing rows, no sparkline, and
            // the Goto / View in Planner / Pin row -- exactly what BuildTabWithInfoPanel sets up.
            var options = new ObjectInfoPanel.PanelDisplayOptions(ShowAltAz: true, ShowRiseSet: true);
            var actions = new ObjectInfoPanel.PanelActions(
                Close: () => { }, Goto: () => { }, ViewInPlanner: () => { }, TogglePin: () => { });

            var ph = ObjectInfoPanel.DesignHeight(in options, in actions);
            var px = content.X + 12f;
            var py = MathF.Max(content.Y, content.Y + content.Height - ph - 32f);
            return (px + 20f, py + (ph * fromTopFraction));
        }

        [Fact]
        public async Task APressOnThePanelsBlankAreaIsClaimed()
        {
            var db = await SharedCatalogDB.InitAsync(TestContext.Current.CancellationToken);
            using var renderer = new RgbaImageRenderer(ContentW, ContentH);
            var (tab, content, planner, clock) = BuildTabWithInfoPanel(db, renderer);

            tab.Render(planner, content, clock);

            // Half way down the panel: clear of the close X (the top DesignCloseSize) and of the
            // button row (the bottom DesignButtonHeight plus its margin).
            var (x, y) = PanelBody(content, 0.5f);
            var hit = tab.HitTestAndDispatch(x, y);

            // Named, not merely non-null: any region claiming that pixel would satisfy a bare
            // ShouldNotBeNull, and the one this test is about is the panel's own background.
            hit.ShouldBeOfType<HitResult.ButtonHit>().Action.ShouldBe("InfoPanelBackground",
                "a press on the panel's own blank body must be claimed by the panel, not fall through to the map underneath it");
        }

        [Fact]
        public async Task APressOutsideThePanelIsNotClaimedByIt()
        {
            var db = await SharedCatalogDB.InitAsync(TestContext.Current.CancellationToken);
            using var renderer = new RgbaImageRenderer(ContentW, ContentH);
            var (tab, content, planner, clock) = BuildTabWithInfoPanel(db, renderer);

            tab.Render(planner, content, clock);

            // Top-right corner of the content rect: well clear of the panel, which sits bottom-left.
            var hit = tab.HitTestAndDispatch(content.Width - 5f, 5f);

            (hit is HitResult.ButtonHit { Action: "InfoPanelBackground" }).ShouldBeFalse(
                "the background region must not reach outside the panel it belongs to");
        }
    }
}
