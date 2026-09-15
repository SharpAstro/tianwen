using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DIR.Lib;
using TianWen.Lib.Astrometry.Catalogs;
using Shouldly;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests
{
    /// <summary>
    /// The sky map's two keyboard bindings are DECLARATIONS on the tree it paints, and the input router
    /// is what fires them: F3 on the map's own surface opens and closes the search window, Ctrl+F inside
    /// the open window puts the keyboard back in the box.
    /// </summary>
    /// <remarks>
    /// Written against the router rather than against the tab's key handler on purpose, because the
    /// binding no longer exists anywhere else: the host used to carry <c>if (key == F3) return false;</c>
    /// with a comment calling F3 global, and the point of the declaration is that the SAME rule now holds
    /// on every surface, including while a text field has the keyboard.
    /// </remarks>
    [Collection("Astrometry")]
    public class SkyMapSearchShortcutTests
    {
        private const int ContentW = 900;
        private const int ContentH = 800;

        private sealed record Harness(
            SkyMapTab<RgbaImage> Tab, InputRouter Router, List<KeyChord> Fired, PlannerState Planner)
        {
            public void Render()
                => Tab.Render(Planner, new RectF32(0, 0, ContentW, ContentH),
                    new FakeTimeProviderWrapper(new DateTimeOffset(2026, 6, 21, 22, 0, 0, TimeSpan.Zero)));
        }

        /// <summary>
        /// A sky map on a raster surface with a router over it. With no catalog the map paints its
        /// "loading" placeholder and the binding pass still runs, which is the point for F3 -- the
        /// declaration belongs to the tab being on screen, not to the sky being drawable. The search
        /// WINDOW is drawn from inside the sky pass, so the two tests about the box take a real one.
        /// </summary>
        private static Harness Build(RgbaImageRenderer renderer, ICelestialObjectDB? db = null)
        {
            var tab = new SkyMapTab<RgbaImage>(renderer) { FontPath = FontResolver.ResolveSystemFont() };
            var fired = new List<KeyChord>();
            var router = new InputRouter(tab.Ui, new BackgroundTaskTracker(), () => { })
            {
                Widgets = () => [tab],
            };
            router.ShortcutFired += (chord, _) => fired.Add(chord);

            return new Harness(tab, router, fired, new PlannerState
            {
                ObjectDb = db,
                SiteLatitude = 45.0,
                SiteLongitude = -75.0,
                SiteTimeZone = TimeSpan.Zero,
                PlanningDate = new DateTimeOffset(2026, 6, 21, 22, 0, 0, TimeSpan.Zero),
            });
        }

        [Fact]
        public void TheMapDeclaresF3OnTheSurfaceItPainted()
        {
            using var renderer = new RgbaImageRenderer(ContentW, ContentH);
            var harness = Build(renderer);
            harness.Render();

            var declared = harness.Tab.GetCapturedLayout()
                .Where(n => n.Node.Shortcut == new KeyChord(InputKey.F3))
                .ToArray();

            declared.Length.ShouldBe(1, "exactly one painted node may claim F3, or which one fires is an accident");
            declared[0].Node.OnActivate.ShouldNotBeNull("a chord on a node with nothing to do is swallowed silently");
        }

        [Fact]
        public void F3FiresThroughTheRouter()
        {
            using var renderer = new RgbaImageRenderer(ContentW, ContentH);
            var harness = Build(renderer);
            harness.Render();

            harness.Router.Handle(new InputEvent.KeyDown(InputKey.F3)).ShouldBeTrue();
            harness.Fired.ShouldBe([new KeyChord(InputKey.F3)]);
        }

        [Fact]
        public void F3ReachesTheMapWhileAFieldHasTheKeyboard()
        {
            using var renderer = new RgbaImageRenderer(ContentW, ContentH);
            var harness = Build(renderer);
            harness.Render();

            // Any focused field will do: the rule under test is KeyChord.BeatsFocusedField, which is a
            // property of the chord and not of which box happens to be live.
            var field = new TextInputState();
            harness.Tab.Ui.Focus.Focus(field);

            harness.Router.Handle(new InputEvent.KeyDown(InputKey.F3)).ShouldBeTrue();
            harness.Fired.ShouldBe([new KeyChord(InputKey.F3)]);
        }

        [Fact]
        public void ABareLetterDoesNotReachTheMapWhileAFieldHasTheKeyboard()
        {
            using var renderer = new RgbaImageRenderer(ContentW, ContentH);
            var harness = Build(renderer);
            harness.Render();

            harness.Tab.Ui.Focus.Focus(new TextInputState());

            // The other half of the same rule, and the reason the search box's chord carries Ctrl: a
            // bare letter typed into a field is a letter.
            harness.Router.Handle(new InputEvent.KeyDown(InputKey.F));
            harness.Fired.ShouldBeEmpty();
        }

        [Fact]
        public async Task CtrlFPutsTheKeyboardBackInTheSearchBox()
        {
            var db = await SharedCatalogDB.InitAsync(TestContext.Current.CancellationToken);
            using var renderer = new RgbaImageRenderer(ContentW, ContentH);
            var harness = Build(renderer, db);

            var search = harness.Tab.State.Search;
            search.IsOpen = true;
            search.SearchInput.Text = "M 31";
            harness.Render();

            // Focus is somewhere else entirely, which is the case the chord exists for.
            var elsewhere = new TextInputState();
            harness.Tab.Ui.Focus.Focus(elsewhere);

            harness.Router.Handle(new InputEvent.KeyDown(InputKey.F, InputModifier.Ctrl)).ShouldBeTrue();

            harness.Tab.Ui.Focus.Current.ShouldBeSameAs(search.SearchInput);
            // Seeded with its own value and SELECTED, so the next keystroke searches for something else
            // rather than appending to what is in there.
            search.SearchInput.SelectionStart.ShouldBe(0);
            search.SearchInput.SelectionEnd.ShouldBe(search.SearchInput.Text.Length);
        }

        [Fact]
        public async Task TheSearchBoxClaimsNoChordWhileTheWindowIsClosed()
        {
            var db = await SharedCatalogDB.InitAsync(TestContext.Current.CancellationToken);
            using var renderer = new RgbaImageRenderer(ContentW, ContentH);
            var harness = Build(renderer, db);
            harness.Tab.State.Search.IsOpen = false;
            harness.Render();

            // A shortcut is matched against the PAINTED tree, so a binding inside a closed panel is inert
            // with nobody saying so -- the guard a hand-written key map needs beside every key.
            harness.Tab.GetCapturedLayout()
                .ShouldNotContain(n => n.Node.Shortcut == new KeyChord(InputKey.F, InputModifier.Ctrl));
        }
    }
}
