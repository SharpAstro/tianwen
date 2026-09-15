using System;
using System.Linq;
using System.Threading.Tasks;
using DIR.Lib;
using Shouldly;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests
{
    /// <summary>
    /// The F3 search modal's highlighted row is the layout tree's own <c>.BgFocus</c>, resolved against
    /// the <c>ListCursor</c> the tab opens from <c>SearchInteraction.SelectedIndex</c> -- so what lights
    /// up and what a click reaches are one list by construction, not two indices kept in step.
    /// </summary>
    /// <remarks>
    /// This asserts on the PICTURE because nothing else can. A <c>.BgFocus</c> whose cursor is never
    /// opened paints nothing at all: the rows still register their <c>ListItemHit</c>, still dispatch,
    /// still commit, and every hit-tracker assertion in the suite stays green while the highlight has
    /// silently gone. Stated as a SWAP between two renders rather than against a colour literal, so it
    /// pins that the fill FOLLOWS the cursor without restating the palette (or the blend) here.
    /// </remarks>
    [Collection("Astrometry")]
    public class SkyMapSearchHighlightTests
    {
        private const int ContentW = 900;
        private const int ContentH = 800;

        [Fact]
        public async Task TheSearchResultHighlightFollowsTheSelectedIndex()
        {
            var db = await SharedCatalogDB.InitAsync(TestContext.Current.CancellationToken);

            var (first0, second0) = RenderSearchRowPixels(db, selectedIndex: 0);
            var (first1, second1) = RenderSearchRowPixels(db, selectedIndex: 1);

            // One row is lit and the other is not, whichever is selected...
            first0.ShouldNotBe(second0);
            first1.ShouldNotBe(second1);

            // ...and moving the cursor exchanges the two fills exactly.
            first1.ShouldBe(second0);
            second1.ShouldBe(first0);
        }

        /// <summary>
        /// Opens the modal over a query with at least two hits, puts the keyboard on
        /// <paramref name="selectedIndex"/>, renders, and reads the fill of the first two result rows out
        /// of the rects the rows themselves registered. The sample x is two pixels into the row, inside
        /// its leading pad cell, so the pixel is the row's fill and never a label glyph.
        /// </summary>
        private static (RGBAColor32 First, RGBAColor32 Second) RenderSearchRowPixels(
            TianWen.Lib.Astrometry.Catalogs.ICelestialObjectDB db, int selectedIndex)
        {
            using var renderer = new RgbaImageRenderer(ContentW, ContentH);
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

            // The interaction is normally built by AppSignalHandler; built directly here because the
            // three callbacks it takes are the host's signals and none of them is what is under test.
            var search = tab.State.Search;
            search.Interaction = new SkyMapSearchInteraction(
                search, db, commit: () => { }, close: () => { }, requestRedraw: () => { });
            // A focus owner of its own: opening the search takes the keyboard through the owner rather
            // than by hand, so the call needs one even where the test is not about focus.
            SkyMapSearchActions.OpenSearch(search, db, new TextInputFocus());
            search.SearchInput.OnTextChanged?.Invoke("NGC 10");

            var results = search.Interaction.Results;
            results.Length.ShouldBeGreaterThanOrEqualTo(2, "the query must produce at least two rows to compare");
            search.Interaction.SelectedIndex = selectedIndex;

            tab.Render(planner, new RectF32(0, 0, ContentW, ContentH), new FakeTimeProviderWrapper(now));

            var rows = tab.GetRegisteredRegions()
                .Where(r => r.Result is HitResult.ListItemHit { ListId: "SearchResult" })
                .OrderBy(r => r.Y)
                .ToArray();
            rows.Length.ShouldBeGreaterThanOrEqualTo(2);

            return (PixelAt(renderer, rows[0].X + 2f, rows[0].Y + rows[0].Height / 2f),
                    PixelAt(renderer, rows[1].X + 2f, rows[1].Y + rows[1].Height / 2f));
        }

        private static RGBAColor32 PixelAt(RgbaImageRenderer renderer, float x, float y)
        {
            var surface = renderer.Surface;
            var i = ((int)y * (int)surface.Width + (int)x) * 4;
            return new RGBAColor32(surface.Pixels[i], surface.Pixels[i + 1], surface.Pixels[i + 2], surface.Pixels[i + 3]);
        }
    }
}
