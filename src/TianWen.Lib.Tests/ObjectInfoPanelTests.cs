using System;
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
            => ObjectInfoPanel.SubtitleLine(Galaxy()).ShouldBe("NGC 5194  CVn  Galaxy");

        /// <summary>
        /// A planet has no catalogue designation but does have a type and a constellation, so an empty
        /// designation must not suppress them -- nor produce the "(no designation)" placeholder, which
        /// would be saying there is nothing to show while there is.
        /// </summary>
        [Fact]
        public void AnObjectWithNoDesignationStillShowsWhatItHas()
            => ObjectInfoPanel.SubtitleLine(Galaxy() with { Canonical = "" })
                .ShouldBe("CVn  Galaxy");

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
            line.ShouldContain("SB 22.10");
            line.ShouldContain("B-V 0.60");
            line.ShouldContain("size 11.2'");
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
            var three = two with { OpenInAtlas = () => { } };

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
            UnpinBg: new RGBAColor32(0x5A, 0x3A, 0x3A, 0xFF));

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
