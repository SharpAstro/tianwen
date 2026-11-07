using System;
using System.Linq;
using DIR.Lib;
using Shouldly;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests
{
    /// <summary>
    /// The object info panel shared by the sky atlas and the FITS viewer: what it says, and which of
    /// its buttons exist.
    /// </summary>
    /// <remarks>
    /// <para><b>The strings are the part worth pinning.</b> The atlas panel grew six rows, a comet
    /// sparkline and three actions over months, and the viewer needed the same panel -- so the content
    /// was hoisted while placement stayed with each host. What a second copy would have drifted on is
    /// exactly this: which fields are omitted when the catalogue does not know them, how the subtitle
    /// composes, and which buttons a host that offers no mount is shown.</para>
    /// <para>Asserted against the builders directly rather than by arranging a layout tree: the tree
    /// is one <c>VStack</c> of those strings, so a test that arranges it would be testing the layout
    /// engine, and a wrong string would read as a correct one at the wrong pixel.</para>
    /// </remarks>
    [Collection("UI")]
    public class ObjectInfoPanelTests
    {
        private static SkyMapInfoPanelData Galaxy() => new SkyMapInfoPanelData(
            Name: "Whirlpool Galaxy",
            Canonical: "NGC 5194",
            ObjType: ObjectType.Galaxy,
            Constellation: Constellation.CanesVenatici,
            RA: 13.4979,
            Dec: 47.1953,
            VMag: 8.4f,
            BMinusV: 0.6f,
            AltDeg: 42.1,
            AzDeg: 118.4,
            RiseTime: new DateTimeOffset(2026, 8, 16, 19, 12, 0, TimeSpan.Zero),
            TransitTime: new DateTimeOffset(2026, 8, 16, 23, 48, 0, TimeSpan.Zero),
            SetTime: new DateTimeOffset(2026, 8, 17, 4, 21, 0, TimeSpan.Zero),
            Circumpolar: false,
            NeverRises: false,
            AngularSizeDeg: 11.2 / 60.0,
            Shape: null,
            Index: CatalogIndex.NGC5194,
            SurfaceBrightness: 22.1f);

        // --- the subtitle composes from whichever pieces exist ---

        [Fact]
        public void TheSubtitleJoinsDesignationConstellationAndType()
            => ObjectInfoPanel.SubtitleLine(Galaxy()).ShouldBe("NGC 5194 · CVn · Galaxy");

        /// <summary>
        /// A planet has no catalogue designation but does have a type and a constellation, so an empty
        /// designation must not suppress them -- nor produce the "(no designation)" placeholder, which
        /// would be saying there is nothing to show while there is.
        /// </summary>
        [Fact]
        public void AnObjectWithNoDesignationStillShowsWhatItHas()
            => ObjectInfoPanel.SubtitleLine(Galaxy() with { Canonical = "" })
                .ShouldBe("CVn · Galaxy");

        /// <summary>Only when all three are missing does the row say so rather than sit blank.</summary>
        [Fact]
        public void WithNothingKnownTheSubtitleSaysSo()
            => ObjectInfoPanel.SubtitleLine(Galaxy() with
            {
                Canonical = "",
                Constellation = default,
                ObjType = ObjectType.Unknown,
            }).ShouldBe("(no designation)");

        // --- brightness fields are omitted, not dashed ---

        [Fact]
        public void TheBrightnessLineCarriesEveryFieldTheCatalogueKnows()
        {
            var line = ObjectInfoPanel.BrightnessLine(Galaxy());

            line.ShouldContain("mag 8.40");
            line.ShouldContain("SB 22.1");
            line.ShouldContain("B-V 0.60");
            line.ShouldContain("size 11.2'");
        }

        /// <summary>
        /// Surface brightness is shown to the precision the catalogue keeps it at. It is stored as a Half,
        /// which steps in 1/64 between 16 and 32, so OpenNGC's 23.60 read back as 23.59375 and was printed
        /// "SB 23.59" (reported 2026-09-28 on IC 5247); a tenth is what the stored value can carry.
        /// </summary>
        [Fact]
        public void SurfaceBrightnessIsShownToOneDecimal()
        {
            var stored = (float)(Half)23.60f;
            stored.ShouldNotBe(23.60f, "the fixture needs a value the Half cannot hold exactly");

            var line = ObjectInfoPanel.BrightnessLine(Galaxy() with { SurfaceBrightness = stored });

            line.ShouldContain("SB 23.6");
            line.ShouldNotContain("23.59");
        }

        /// <summary>
        /// An unknown field leaves NO trace, so the row shortens instead of filling with dashes -- the
        /// exception being magnitude, which shows a dash: a brightness row with no brightness in it
        /// reads as a rendering fault rather than as missing data.
        /// </summary>
        [Fact]
        public void UnknownBrightnessFieldsAreOmittedExceptMagnitude()
        {
            var line = ObjectInfoPanel.BrightnessLine(Galaxy() with
            {
                VMag = float.NaN,
                BMinusV = float.NaN,
                SurfaceBrightness = float.NaN,
                AngularSizeDeg = null,
            });

            line.ShouldBe("mag -");
        }

        // --- rise / transit / set collapses where three times are meaningless ---

        [Fact]
        public void RiseSetShowsThreeTimesInTheStatedOffset()
        {
            var line = ObjectInfoPanel.RiseSetLine(Galaxy(), TimeSpan.FromHours(2));

            // 19:12 UTC at +02:00 is 21:12 -- the offset is applied, not ignored.
            line.ShouldBe("Rise 21:12   Transit 01:48   Set 06:21");
        }

        [Fact]
        public void ACircumpolarObjectStatesOnlyItsTransit()
            => ObjectInfoPanel.RiseSetLine(Galaxy() with { Circumpolar = true }, TimeSpan.Zero)
                .ShouldBe("Circumpolar   Transit 23:48");

        [Fact]
        public void AnObjectThatNeverRisesSaysThatInstead()
            => ObjectInfoPanel.RiseSetLine(Galaxy() with { NeverRises = true }, TimeSpan.Zero)
                .ShouldBe("Never rises (below horizon)");

        [Fact]
        public void AMissingTimeIsDashesRatherThanAnEpoch()
            => ObjectInfoPanel.FormatHHMM(null, TimeSpan.Zero).ShouldBe("--:--");

        // --- the rows a host cannot answer are absent, not blank ---

        /// <summary>
        /// The default options are the cheapest answer: the four rows that need no site. A viewer
        /// opening a frame whose header carries no location gets exactly these.
        /// </summary>
        [Fact]
        public void WithoutASiteThePanelIsFourRows()
        {
            var options = new ObjectInfoPanel.PanelDisplayOptions();

            ObjectInfoPanel.RowCount(in options).ShouldBe(4);
        }

        [Fact]
        public void ASiteAddsAltAzAndRiseSetAndAFootnoteAddsItsOwn()
        {
            var withSite = new ObjectInfoPanel.PanelDisplayOptions(ShowAltAz: true, ShowRiseSet: true);
            ObjectInfoPanel.RowCount(in withSite).ShouldBe(6);

            var noted = withSite with { Footnote = "at capture, 2026-08-16" };
            ObjectInfoPanel.RowCount(in noted).ShouldBe(7);

            ObjectInfoPanel.DesignTextBlockHeight(in noted)
                .ShouldBeGreaterThan(ObjectInfoPanel.DesignTextBlockHeight(in withSite));
        }

        /// <summary>An empty footnote is not a row -- null and "" mean the same thing here.</summary>
        [Fact]
        public void AnEmptyFootnoteIsNotARow()
        {
            var options = new ObjectInfoPanel.PanelDisplayOptions(Footnote: "");

            ObjectInfoPanel.RowCount(in options).ShouldBe(4);
        }

        /// <summary>
        /// A solar-system body's disk row is counted only when it says something: the text is the row, so a panel
        /// without an aspect (DiskLine's empty answer) cannot get an empty row and a height that does not match it.
        /// </summary>
        [Fact]
        public void TheDiskRowIsARowOnlyWhenItHasText()
        {
            var withSite = new ObjectInfoPanel.PanelDisplayOptions(ShowAltAz: true, ShowRiseSet: true);
            var positionOnly = SkyMapInfoPanelData.FromPosition("A position", 5.0, 20.0, 45.0, -75.0, DateTimeOffset.UnixEpoch, default);

            ObjectInfoPanel.RowCount(withSite with { Disk = ObjectInfoPanel.DiskLine(in positionOnly) }).ShouldBe(6);
            ObjectInfoPanel.RowCount(withSite with { Disk = "diam 34.2\"" }).ShouldBe(7);
        }

        // --- an action is a callback, so a button can never be offered with nothing behind it ---

        /// <summary>
        /// A host that offers no actions gets no button row at all, and no height reserved for one.
        /// </summary>
        [Fact]
        public void NoActionsMeansNoButtonRow()
        {
            var actions = new ObjectInfoPanel.PanelActions();
            var options = new ObjectInfoPanel.PanelDisplayOptions();

            actions.HasButtons.ShouldBeFalse();
            ObjectInfoPanel.BuildButtonRow(in actions, in Palette).ShouldBeNull();

            var withButton = actions with { Goto = () => { } };
            withButton.HasButtons.ShouldBeTrue();
            ObjectInfoPanel.DesignHeight(in options, in withButton)
                .ShouldBeGreaterThan(ObjectInfoPanel.DesignHeight(in options, in actions));
        }

        /// <summary>Close alone is not a button ROW: it is the affordance in the corner.</summary>
        [Fact]
        public void CloseIsNotPartOfTheButtonRow()
        {
            var actions = new ObjectInfoPanel.PanelActions(Close: () => { });

            actions.HasButtons.ShouldBeFalse();
            ObjectInfoPanel.BuildButtonRow(in actions, in Palette).ShouldBeNull();
            ObjectInfoPanel.BuildCloseButton(in actions, in Palette).ShouldNotBeNull();
        }

        /// <summary>And a host that offers no Close gets no X, so the panel cannot be dismissed by one.</summary>
        [Fact]
        public void WithoutACloseActionThereIsNoCloseButton()
        {
            var actions = new ObjectInfoPanel.PanelActions();

            ObjectInfoPanel.BuildCloseButton(in actions, in Palette).ShouldBeNull();
        }

        /// <summary>
        /// The mount entry's Solve &amp; Sync REPLACES the ordinary buttons rather than joining them: a
        /// goto to where the mount already reports being is a no-op, and pinning it makes no sense.
        /// </summary>
        [Fact]
        public void SolveSyncReplacesTheOrdinaryActions()
        {
            var actions = new ObjectInfoPanel.PanelActions(
                Goto: () => { },
                ViewInPlanner: () => { },
                TogglePin: () => { },
                SolveSync: () => { });

            var row = ObjectInfoPanel.BuildButtonRow(in actions, in Palette).ShouldNotBeNull();

            // One button between the two spacers, whatever else was passed.
            CountLeaves(row).ShouldBe(3);
        }

        [Fact]
        public void EachActionAddsExactlyOneButton()
        {
            var one = new ObjectInfoPanel.PanelActions(Goto: () => { });
            var two = one with { TogglePin = () => { } };
            var three = two with { ViewInPlanner = () => { } };

            // Leaves are: leading Star spacer, then per button a gap (all but the first) plus the
            // button itself, then the trailing margin spacer.
            CountLeaves(ObjectInfoPanel.BuildButtonRow(in one, in Palette).ShouldNotBeNull()).ShouldBe(3);
            CountLeaves(ObjectInfoPanel.BuildButtonRow(in two, in Palette).ShouldNotBeNull()).ShouldBe(5);
            CountLeaves(ObjectInfoPanel.BuildButtonRow(in three, in Palette).ShouldNotBeNull()).ShouldBe(7);
        }

        private static readonly ObjectInfoPanel.PanelPalette Palette = new ObjectInfoPanel.PanelPalette(
            Background: new RGBAColor32(0x10, 0x10, 0x1C, 0xE0),
            Border: new RGBAColor32(0x50, 0x50, 0x60, 0xFF),
            Text: new RGBAColor32(0xDD, 0xDD, 0xDD, 0xFF),
            DimText: new RGBAColor32(0x80, 0x80, 0x88, 0xFF),
            ButtonText: new RGBAColor32(0xDD, 0xDD, 0xDD, 0xFF),
            GotoBg: new RGBAColor32(0x5A, 0x3A, 0x5A, 0xFF),
            DisabledBg: new RGBAColor32(0x38, 0x38, 0x3C, 0xFF),
            ViewBg: new RGBAColor32(0x3A, 0x4A, 0x5A, 0xFF),
            PinBg: new RGBAColor32(0x3A, 0x5A, 0x3A, 0xFF),
            UnpinBg: new RGBAColor32(0x5A, 0x3A, 0x3A, 0xFF),
            Link: new RGBAColor32(0x7A, 0xB4, 0xE8, 0xFF));

        // --- a link leaves the app, so it is a link and not a button ---

        /// <summary>
        /// The article and the sky atlas are a row of their own: a host offering only links gets no button
        /// row, and the links still take their height.
        /// </summary>
        [Fact]
        public void LinksAreARowOfTheirOwnNotButtons()
        {
            var actions = new ObjectInfoPanel.PanelActions(
                ArticleUrl: "https://en.wikipedia.org/wiki/Horsehead_Nebula", AtlasUrl: "https://example.org/atlas");
            var options = new ObjectInfoPanel.PanelDisplayOptions();

            actions.HasButtons.ShouldBeFalse();
            actions.HasLinks.ShouldBeTrue();
            ObjectInfoPanel.BuildButtonRow(in actions, in Palette).ShouldBeNull();

            // The two links, the gap between them and the trailing spacer that keeps them left.
            CountLeaves(ObjectInfoPanel.BuildLinkRow(in actions, in Palette).ShouldNotBeNull()).ShouldBe(4);
            ObjectInfoPanel.DesignHeight(in options, in actions)
                .ShouldBeGreaterThan(ObjectInfoPanel.DesignHeight(in options, new ObjectInfoPanel.PanelActions()));
        }

        /// <summary>A missing or empty URL is a link that is not offered, exactly as a null callback is a button.</summary>
        [Fact]
        public void NoUrlMeansNoLink()
        {
            var actions = new ObjectInfoPanel.PanelActions(Goto: () => { }, ArticleUrl: null, AtlasUrl: "");

            actions.HasLinks.ShouldBeFalse();
            ObjectInfoPanel.BuildLinkRow(in actions, in Palette).ShouldBeNull();
        }

        /// <summary>
        /// The link row sits under everything else and above the buttons, with its top stated once for both
        /// hosts: nothing it draws can land on the button row.
        /// </summary>
        [Fact]
        public void TheLinkRowSitsAboveTheButtons()
        {
            var options = new ObjectInfoPanel.PanelDisplayOptions(ShowAltAz: true, ShowRiseSet: true);
            var actions = new ObjectInfoPanel.PanelActions(Goto: () => { }, ArticleUrl: "https://en.wikipedia.org/wiki/M31");

            var height = ObjectInfoPanel.DesignHeight(in options, in actions);
            var linkTop = ObjectInfoPanel.DesignLinkRowTop(in options, in actions);
            var buttonTop = height - ObjectInfoPanel.DesignButtonHeight - 8f;

            (linkTop + ObjectInfoPanel.DesignLinkRowHeight).ShouldBeLessThan(buttonTop);
            linkTop.ShouldBeGreaterThanOrEqualTo(ObjectInfoPanel.DesignTextBlockHeight(in options));
        }

        // --- the night curve's slot and its line (#2059) ---

        private static AtlasNightCurve Curve(float rating = 3.5f, TimeSpan? usable = null, bool shaded = true, bool window = true)
            => new AtlasNightCurve(
                Samples: [],
                Step: TimeSpan.FromMinutes(15),
                AstroDark: new DateTimeOffset(2025, 12, 4, 18, 30, 0, TimeSpan.Zero),
                AstroTwilight: new DateTimeOffset(2025, 12, 5, 4, 30, 0, TimeSpan.Zero),
                MinAltitude: 20,
                Shaded: shaded,
                UsableTime: usable ?? TimeSpan.FromMinutes(255),
                WindowStart: window ? new DateTimeOffset(2025, 12, 4, 20, 30, 0, TimeSpan.Zero) : null,
                WindowEnd: window ? new DateTimeOffset(2025, 12, 5, 0, 45, 0, TimeSpan.Zero) : null,
                Rating: rating);

        /// <summary>The usable time as h:mm, the window in the panel's own zone and the rating as the planner shows it.</summary>
        [Fact]
        public void TheNightLineStatesUsableTimeWindowAndRating()
            => ObjectInfoPanel.NightLine(Curve(), TimeSpan.FromHours(1))
                .ShouldBe("Usable 4:15   21:30 to 01:45   3.5★");

        /// <summary>A time under an hour keeps its leading zero minutes, and the zone moves the window, not the time.</summary>
        [Fact]
        public void TheNightLineHoldsItsFormatForShortAndZonedTimes()
        {
            ObjectInfoPanel.NightLine(Curve(rating: 0.5f, usable: TimeSpan.FromMinutes(45)), TimeSpan.Zero)
                .ShouldBe("Usable 0:45   20:30 to 00:45   0.5★");
            ObjectInfoPanel.NightLine(Curve(), TimeSpan.FromHours(-5))
                .ShouldBe("Usable 4:15   15:30 to 19:45   3.5★");
        }

        /// <summary>An object that never gets above the minimum says so, with the minimum, rather than printing 0:00 and a window of dashes.</summary>
        [Fact]
        public void TheNightLineSaysWhenNothingCounts()
            => ObjectInfoPanel.NightLine(Curve(usable: TimeSpan.Zero, window: false), TimeSpan.Zero)
                .ShouldBe("Not above 20° in the dark");

        /// <summary>A body with no shade (the Moon) has no line, and a score is never what the line shows.</summary>
        [Fact]
        public void ABodyWithNoShadeHasNoNightLine()
            => ObjectInfoPanel.NightLine(Curve(shaded: false), TimeSpan.Zero).ShouldBeNull();

        /// <summary>
        /// The curve's section takes exactly its own height, the line inside it rather than in the rows, and
        /// leaves the comet sparkline's slot and a host that passes nothing (the FITS viewer) as they were.
        /// </summary>
        [Fact]
        public void TheNightCurveSectionHasItsOwnHeightAndLeavesTheRowsAlone()
        {
            var actions = new ObjectInfoPanel.PanelActions();
            var plain = new ObjectInfoPanel.PanelDisplayOptions(ShowAltAz: true, ShowRiseSet: true);
            var withCurve = plain with { NightCurve = Curve() };

            ObjectInfoPanel.RowCount(in withCurve).ShouldBe(ObjectInfoPanel.RowCount(in plain));
            ObjectInfoPanel.DesignHeight(in withCurve, in actions)
                .ShouldBe(ObjectInfoPanel.DesignHeight(in plain, in actions) + ObjectInfoPanel.DesignNightCurveSectionHeight);

            // Beside the sparkline, each takes its own.
            var both = withCurve with { Sparkline = true };
            ObjectInfoPanel.DesignHeight(in both, in actions)
                .ShouldBe(ObjectInfoPanel.DesignHeight(in withCurve, in actions) + ObjectInfoPanel.DesignSparklineHeight + 4f);

            new ObjectInfoPanel.PanelDisplayOptions().NightCurve.ShouldBeNull();
            ObjectInfoPanel.BuildNightCurveSection(in plain, in Palette).ShouldBeNull();
            ObjectInfoPanel.BuildNightCurveSection(in withCurve, in Palette).ShouldNotBeNull();
        }

        /// <summary>
        /// A curve still being scored reserves its section, so the panel is as tall before it lands as after (nothing moves under the
        /// pointer), with its slot empty and no line. A host that never offers a curve (the FITS viewer) reserves nothing.
        /// </summary>
        [Fact]
        public void APendingNightCurveReservesItsSection()
        {
            var actions = new ObjectInfoPanel.PanelActions();
            var plain = new ObjectInfoPanel.PanelDisplayOptions(ShowAltAz: true, ShowRiseSet: true);
            var pending = plain with { NightCurvePending = true };
            var landed = plain with { NightCurve = Curve() };

            ObjectInfoPanel.HasNightCurveSection(in pending).ShouldBeTrue();
            ObjectInfoPanel.HasNightCurveSection(in plain).ShouldBeFalse();
            ObjectInfoPanel.DesignHeight(in pending, in actions).ShouldBe(ObjectInfoPanel.DesignHeight(in landed, in actions));
            ObjectInfoPanel.BuildNightCurveSection(in pending, in Palette).ShouldNotBeNull();
        }

        /// <summary>
        /// A score that failed (an unplaceable body) keeps the section's height and says so in its line, so the panel does not shrink
        /// and jump when the answer lands as "no curve" (review note on #2059).
        /// </summary>
        [Fact]
        public void AnUnavailableNightCurveKeepsItsSectionAndSaysSo()
        {
            var actions = new ObjectInfoPanel.PanelActions();
            var plain = new ObjectInfoPanel.PanelDisplayOptions(ShowAltAz: true, ShowRiseSet: true);
            var unavailable = plain with { NightCurveUnavailable = true };
            var pending = plain with { NightCurvePending = true };

            ObjectInfoPanel.HasNightCurveSection(in unavailable).ShouldBeTrue();
            ObjectInfoPanel.DesignHeight(in unavailable, in actions).ShouldBe(ObjectInfoPanel.DesignHeight(in pending, in actions));

            var section = ObjectInfoPanel.BuildNightCurveSection(in unavailable, in Palette).ShouldBeOfType<Layout.Node.Stack>();
            // Any(...) rather than ShouldContain(...): Shouldly's ShouldContain takes an expression tree, which cannot hold an 'is' pattern.
            section.Children.Any(c => c is Layout.Node.Leaf { Content: Layout.Content.Text { Value: ObjectInfoPanel.NightCurveUnavailableLine } }).ShouldBeTrue();
        }

        /// <summary>
        /// Every control that can act lights under the pointer and a link says it is one with the hand
        /// pointer. A disabled Goto does not light, since a press on it does nothing. Raised 2026-09-18: no
        /// panel button in the app lit under the pointer.
        /// </summary>
        [Fact]
        public void EveryControlThatCanActDeclaresAHover()
        {
            var actions = new ObjectInfoPanel.PanelActions(
                Close: () => { }, Goto: () => { }, GotoEnabled: false, ViewInPlanner: () => { }, TogglePin: () => { },
                ArticleUrl: "https://en.wikipedia.org/wiki/M31", AtlasUrl: "https://example.org/atlas");

            var buttons = ObjectInfoPanel.BuildButtonRow(in actions, in Palette).ShouldBeOfType<Layout.Node.Stack>();
            var clickable = buttons.Children.Where(c => c.Hit is not null).ToList();
            clickable.Count.ShouldBe(2, "View in Planner and Pin; the disabled Goto registers nothing");
            clickable.ShouldAllBe(c => c.HoverBackground != null);
            buttons.Children.Single(c => c is Layout.Node.Leaf { Content: Layout.Content.Text { Value: "Goto" } })
                .HoverBackground.ShouldBeNull("a disabled Goto does nothing, so it does not light");

            var links = ObjectInfoPanel.BuildLinkRow(in actions, in Palette).ShouldBeOfType<Layout.Node.Stack>()
                .Children.Where(c => c.Hit is HitResult.LinkHit).ToList();
            links.Count.ShouldBe(2);
            links.ShouldAllBe(l => l.HoverBackground != null && l.Cursor == CursorKind.Pointer);

            ObjectInfoPanel.BuildCloseButton(in actions, in Palette).ShouldNotBeNull().HoverBackground.ShouldNotBeNull();
        }

        /// <summary>
        /// The disc says where its texture came from (#1567, #1608): every atlas body's credit is a line of the disc section, arranged
        /// UNDER the disc's slot and inside the section's height, which the panel reserves whether or not the credit has arrived, so the
        /// panel does not jump when the texture lands. Solar System Scope's CC BY 4.0 asks for exactly this wherever its maps show.
        /// </summary>
        [Fact]
        public void EveryAtlasBodysCreditIsDrawnUnderItsDisc()
        {
            using var renderer = new RgbaImageRenderer(400, 400);
            var context = new PixelMeasureContext<RgbaImage>(renderer, FontResolver.ResolveSystemFont());
            var unknown = new ObjectInfoPanel.PanelDisplayOptions(Disc: true);
            foreach (var body in TianWen.Lib.Imaging.Planetary.PlanetTextureAtlas.Layout)
            {
                var credit = TianWen.Lib.Imaging.Planetary.PlanetTextureAtlas.Credit(body);
                var options = new ObjectInfoPanel.PanelDisplayOptions(Disc: true, DiscCredit: credit);
                ObjectInfoPanel.DesignMediaHeight(in options).ShouldBe(ObjectInfoPanel.DesignMediaHeight(in unknown));
                var height = ObjectInfoPanel.DesignMediaHeight(in options);
                var media = ObjectInfoPanel.BuildMediaSections(in options, in Palette).ShouldNotBeNull();
                var arranged = Layout.Engine.Arrange(media, new Rect<float>(0, 0, ObjectInfoPanel.DesignWidth, height), context);

                var disc = arranged.Single(a => a.Node is Layout.Node.Leaf { Content: Layout.Content.Fill { Key: ObjectInfoPanel.DiscFillKey } }).Bounds;
                var line = arranged.Where(a => a.Node is Layout.Node.Leaf { Content: Layout.Content.Text { Value: var value } } && value == credit)
                    .ShouldHaveSingleItem($"{body}: the credit \"{credit}\" is not in the panel").Bounds;
                line.Y.ShouldBeGreaterThanOrEqualTo(disc.Y + disc.Height, $"{body}: the credit is not under the disc");
                (line.Y + line.Height).ShouldBeLessThanOrEqualTo(height, $"{body}: the credit falls outside the section");
                line.Height.ShouldBeGreaterThan(0f);
            }
        }

        /// <summary>
        /// How many children the row has. Counting rather than inspecting content: what matters is that
        /// an action produces a button and a missing action produces nothing, which is a count.
        /// <c>Length</c> rather than <c>Count</c>: ImmutableArray only exposes the latter through
        /// IReadOnlyCollection, which is the repo's own stated convention for these.
        /// </summary>
        private static int CountLeaves(Layout.Node node)
            => node is Layout.Node.Stack stack ? stack.Children.Length : 1;
    }
}
