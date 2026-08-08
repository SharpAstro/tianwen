using System;
using DIR.Lib;
using Shouldly;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests
{
    /// <summary>
    /// Pins the PROPERTIES the three palettes were chosen for, not their hex values. The values also
    /// live in <c>docs/plans/colour-theme-mocks/</c> (the studio and the PNG renderer), which are
    /// design artifacts free to drift once the decision is made -- so the guarantees have to be
    /// asserted here, against the palettes the app actually paints with.
    /// </summary>
    // In the "UI" collection because two of these call GuiTheme.Apply, which mutates PROCESS-WIDE
    // state. They restore it in a finally, but that is not atomic with respect to a test running in
    // parallel -- and PlannerTabLayoutTests renders real pixels offline, so a theme flip landing
    // mid-render is exactly the kind of thing that would flake once a month and never reproduce.
    [Collection("UI")]
    public class GuiThemeTests
    {
        // WCAG relative luminance. RGBAColor32.Luminance is the NTSC gamma-encoded approximation,
        // which is fine for a "is this dark" test and wrong for a contrast ratio.
        private static double Linear(byte c)
        {
            var v = c / 255.0;
            return v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        }

        private static double Luminance(RGBAColor32 c)
            => 0.2126 * Linear(c.Red) + 0.7152 * Linear(c.Green) + 0.0722 * Linear(c.Blue);

        private static double Contrast(RGBAColor32 a, RGBAColor32 b)
        {
            double x = Luminance(a), y = Luminance(b);
            return (Math.Max(x, y) + 0.05) / (Math.Min(x, y) + 0.05);
        }

        public static TheoryData<string> DaylightStates => new() { "Light", "Dark" };

        private static UiPalette ByName(string name) => name switch
        {
            "Light" => GuiTheme.LightPalette,
            "Dark" => GuiTheme.DarkPalette,
            _ => GuiTheme.NightPalette,
        };

        // Light and Dark have the luminance headroom for a full three-step text ladder, so they are
        // held to AAA for body text and AA for the de-emphasised one. Night is exempt and has its
        // own, weaker guarantee below -- see the ceiling test.
        [Theory]
        [MemberData(nameof(DaylightStates))]
        public void LightAndDarkClearAaaForBodyAndAaForDim(string state)
        {
            var p = ByName(state);

            Contrast(p.BodyText, p.PanelBg).ShouldBeGreaterThanOrEqualTo(7.0);
            Contrast(p.DimText, p.PanelBg).ShouldBeGreaterThanOrEqualTo(4.5);
            Contrast(p.Accent, p.PanelBg).ShouldBeGreaterThanOrEqualTo(4.5);
        }

        [Theory]
        [MemberData(nameof(DaylightStates))]
        public void EverySeverityIsLegibleOnItsPanel(string state)
        {
            var p = ByName(state);

            Contrast(p.Info, p.PanelBg).ShouldBeGreaterThanOrEqualTo(4.5);
            Contrast(p.Warn, p.PanelBg).ShouldBeGreaterThanOrEqualTo(4.5);
            Contrast(p.Error, p.PanelBg).ShouldBeGreaterThanOrEqualTo(4.5);
        }

        // Every (text role, surface role) pair the UI actually paints, checked in one place. This exists
        // because eyeballing screenshots missed three real failures that arithmetic caught at once:
        // SeparatorStrong used as placeholder text (1.5-1.9:1, invisible in ALL three states), DimText on
        // the green Success fill (1.4:1), and Night's own Info at 2.46:1. A role lookup makes a colour
        // consistent, not legible; only the pairing is legible or not.
        //
        // Night is held to a LOWER floor than Light and Dark, and that is not a concession to sloppiness:
        // red on black caps at 5.25:1, so the whole ladder lives under that ceiling and a 4.5 floor would
        // leave no room for a secondary weight at all. 3.4 keeps every pair perceptible while preserving
        // the body/dim distinction; the standing rule is that anything which MUST be read uses BodyText.
        public static TheoryData<string, string, string> TextPairs => new()
        {
            { "BodyText",   "PanelBg",   "body text on a panel" },
            { "BodyText",   "ContentBg", "body text on the backdrop" },
            { "BodyText",   "HeaderBg",  "body text on chrome" },
            { "BodyText",   "Selection", "text on a selected row" },
            { "DimText",    "PanelBg",   "secondary text on a panel" },
            { "DimText",    "ContentBg", "secondary text on the backdrop" },
            { "DimText",    "HeaderBg",  "the status bar clock" },
            { "HeaderText", "HeaderBg",  "a header label" },
            { "HeaderText", "PanelBg",   "a panel header" },
            { "Accent",     "PanelBg",   "an accented value" },
            { "Accent",     "HeaderBg",  "a pinned planning date" },
            { "Info",       "PanelBg",   "an info message" },
            { "Warn",       "PanelBg",   "a warning" },
            { "Warn",       "HeaderBg",  "the status message in the top bar" },
            { "Error",      "PanelBg",   "an error" },
            { "Success",    "PanelBg",   "a positive mark" },
        };

        private static RGBAColor32 Role(UiPalette p, string name) => name switch
        {
            "ContentBg" => p.ContentBg,
            "PanelBg" => p.PanelBg,
            "HeaderBg" => p.HeaderBg,
            "Selection" => p.Selection,
            "BodyText" => p.BodyText,
            "DimText" => p.DimText,
            "HeaderText" => p.HeaderText,
            "Accent" => p.Accent,
            "Info" => p.Info,
            "Warn" => p.Warn,
            "Error" => p.Error,
            "Success" => p.Success,
            _ => throw new ArgumentOutOfRangeException(nameof(name), name, "unknown role"),
        };

        [Theory]
        [MemberData(nameof(TextPairs))]
        public void EveryTextPairIsPerceptibleInEveryState(string text, string surface, string usage)
        {
            foreach (var state in new[] { "Light", "Dark", "Night" })
            {
                var p = ByName(state);
                var floor = state == "Night" ? 3.4 : 4.5;
                var ratio = Contrast(Role(p, text), Role(p, surface));

                ratio.ShouldBeGreaterThanOrEqualTo(
                    floor,
                    $"{state}: {text} on {surface} ({usage}) is {ratio:F2}:1, below the {floor:F1} floor");
            }
        }

        // The one text pair deliberately left OUT of the matrix above, recorded here so it is a decision
        // rather than a gap: Night's DimText on Selection is 2.94:1.
        //
        // Lightening Selection to lift it is the wrong trade, and the numbers say so. A selection fill is
        // SUPPOSED to be subtle: it measures 1.27:1 in Light and 1.28:1 in Dark against the panel it sits
        // on, and Night's 1.21:1 is the same order, so it is not anomalous. Pushing Night's lighter to
        // "fix" the dim label drops BodyText on Selection from 4.11 to about 2.9, trading a pair that
        // works for a fill nobody asked to be louder. So the standing rule resolves it instead: anything
        // that must be read on a selected row uses BodyText.
        [Theory]
        [InlineData("Light")]
        [InlineData("Dark")]
        [InlineData("Night")]
        public void ASelectionFillIsSubtleInEveryStateByDesign(string state)
        {
            var p = ByName(state);

            // Present, but nowhere near a text-contrast ratio; all three states sit in a narrow band.
            var visibility = Contrast(p.Selection, p.PanelBg);
            visibility.ShouldBeGreaterThan(1.15);
            visibility.ShouldBeLessThan(1.6);

            // Which is what makes BodyText the required choice on a selected row.
            Contrast(p.BodyText, p.Selection).ShouldBeGreaterThanOrEqualTo(state == "Night" ? 3.4 : 4.5);
        }

        [Fact]
        public void NightDimTextOnSelectionIsKnowinglyBelowTheFloor()
        {
            var p = GuiTheme.NightPalette;

            Contrast(p.DimText, p.Selection).ShouldBeLessThan(3.4);
            Contrast(p.BodyText, p.Selection).ShouldBeGreaterThanOrEqualTo(3.4);
        }

        // A filled semantic chip takes ink from its FILL, never a text role. The bug this pins shipped in
        // the chrome sweep: Connect All fills with Success and labelled itself DimText, which measured
        // 1.4:1 on the Dark green. Any FIXED ink is wrong too, because a semantic fill's lightness flips
        // between states, so the check is that InkOn's answer clears AA on the fill in every state.
        [Theory]
        [InlineData("Success")]
        [InlineData("Warn")]
        [InlineData("Error")]
        [InlineData("Info")]
        public void InkOnAFilledChipIsLegibleAgainstThatFill(string fillRole)
        {
            foreach (var state in new[] { "Light", "Dark", "Night" })
            {
                var fill = Role(ByName(state), fillRole);
                var ratio = Contrast(GuiTheme.InkOn(fill), fill);

                ratio.ShouldBeGreaterThanOrEqualTo(
                    4.5, $"{state}: ink on the {fillRole} fill is {ratio:F2}:1");
            }
        }

        // The home board's status dot is a THREE-way mark -- offline / online / running, drawn from
        // DimText / Success / Info -- so those three must be three colours. Leaving Success to its
        // accent fallback shipped a real regression: in Dark, Info IS Accent, so online and running
        // drew the identical dot and the board silently lost a state. The fallback is right only
        // where the green channel is unavailable, and that is Night alone (below).
        [Theory]
        [MemberData(nameof(DaylightStates))]
        public void SuccessIsItsOwnMarkWhereThereIsGreenToSpend(string state)
        {
            var p = ByName(state);

            p.Success.ShouldNotBe(p.Info);
            p.Success.ShouldNotBe(p.Accent);
            p.Success.ShouldNotBe(p.DimText);
            Contrast(p.Success, p.PanelBg).ShouldBeGreaterThanOrEqualTo(4.5);
        }

        // ...and in Night it deliberately IS the accent. The mode's whole point is that green cannot
        // be spent, so a positive mark has to borrow a hue that exists rather than invent one.
        [Fact]
        public void NightSuccessBorrowsTheAccentBecauseGreenIsUnavailable()
            => GuiTheme.NightPalette.Success.ShouldBe(GuiTheme.NightPalette.Accent);

        // C2c introduced DERIVED fills (Mix of two roles) for buttons, alt rows and the sky ramp. A
        // derived colour can land anywhere, so its label needs the same guarantee a stated role gets,
        // and only InkOn can give it: the mix moves with the theme, so no fixed ink can be right.
        [Theory]
        [InlineData("Light")]
        [InlineData("Dark")]
        [InlineData("Night")]
        public void EveryDerivedButtonFillCanCarryItsLabel(string state)
        {
            try
            {
                GuiTheme.Apply(ByState(state), desktopIsDark: state != "Light");

                (string Name, RGBAColor32 Fill)[] fills =
                [
                    ("NeutralButtonBg", GuiTheme.NeutralButtonBg),
                    ("PrimaryButtonBg", GuiTheme.PrimaryButtonBg),
                    ("GoButtonBg", GuiTheme.GoButtonBg),
                    ("CautionButtonBg", GuiTheme.CautionButtonBg),
                    ("DangerButtonBg", GuiTheme.DangerButtonBg),
                ];

                foreach (var (name, fill) in fills)
                {
                    var ratio = Contrast(GuiTheme.InkOn(fill), fill);
                    ratio.ShouldBeGreaterThanOrEqualTo(4.5, $"{state}: ink on {name} is {ratio:F2}:1");
                }
            }
            finally
            {
                // Static app state: hand it back the way every other test expects to find it.
                GuiTheme.Apply(UiThemeState.Dark, desktopIsDark: true);
            }
        }

        // The twilight ramp has ONE job beyond looking like sky: civil must read brighter than nautical,
        // which must read brighter than astronomical. That ordering is why SkyBand is anchored on black
        // rather than on ContentBg -- anchoring on the page inverts it in Light, where "further from the
        // ground" means darker, so civil twilight would render darker than astronomical in one state.
        [Theory]
        [InlineData("Light")]
        [InlineData("Dark")]
        [InlineData("Night")]
        public void TheTwilightRampKeepsItsOrderingInEveryState(string state)
        {
            try
            {
                GuiTheme.Apply(ByState(state), desktopIsDark: state != "Light");

                var civil = Luminance(GuiTheme.SkyBand(0.26f));
                var nautical = Luminance(GuiTheme.SkyBand(0.19f));
                var astro = Luminance(GuiTheme.SkyBand(0.13f));

                civil.ShouldBeGreaterThan(nautical, $"{state}: civil twilight must be the brightest band");
                nautical.ShouldBeGreaterThan(astro, $"{state}: nautical must sit above astronomical");

                // And labels over that sky stay legible, which BodyText could not manage: it is near-black
                // in Light, where the plot is still a dark sky panel.
                Contrast(GuiTheme.SkyInk(), GuiTheme.SkyBand(0.09f)).ShouldBeGreaterThanOrEqualTo(4.5);
            }
            finally
            {
                // Static app state: hand it back the way every other test expects to find it.
                GuiTheme.Apply(UiThemeState.Dark, desktopIsDark: true);
            }
        }

        // An alternate row must be VISIBLE against the panel and still subtle enough not to read as a
        // selection. Deriving it (halfway to ContentBg) is what makes it correct in both directions:
        // darker than the panel on a dark ground, lighter on a light one, with nobody picking a sign.
        [Theory]
        [InlineData("Light")]
        [InlineData("Dark")]
        [InlineData("Night")]
        public void AnAlternateRowIsVisibleButQuieterThanASelection(string state)
        {
            try
            {
                GuiTheme.Apply(ByState(state), desktopIsDark: state != "Light");

                var banding = Contrast(GuiTheme.AltRowBg, GuiTheme.Palette.PanelBg);
                banding.ShouldBeGreaterThan(1.0, $"{state}: the alt row is indistinguishable from the panel");
                banding.ShouldBeLessThan(Contrast(GuiTheme.Palette.Selection, GuiTheme.Palette.PanelBg) + 0.15);

                Contrast(GuiTheme.Palette.BodyText, GuiTheme.AltRowBg)
                    .ShouldBeGreaterThanOrEqualTo(state == "Night" ? 3.4 : 4.5);
            }
            finally
            {
                // Static app state: hand it back the way every other test expects to find it.
                GuiTheme.Apply(UiThemeState.Dark, desktopIsDark: true);
            }
        }

        private static UiThemeState ByState(string state) => state switch
        {
            "Light" => UiThemeState.Light,
            "Dark" => UiThemeState.Dark,
            "Night" => UiThemeState.Night,
            _ => throw new ArgumentOutOfRangeException(nameof(state), state, "unknown state"),
        };

        // The rule Night exists to keep: blue is the most rod-stimulating channel per unit radiance
        // (V' ~0.61 at the sRGB blue primary against ~0.0155 at red), so it is zero everywhere. A
        // single stray blue component would undo the mode's whole purpose while still looking red.
        [Fact]
        public void NightSpendsNoBlueAtAll()
        {
            var p = GuiTheme.NightPalette;

            RGBAColor32[] roles =
            [
                p.ContentBg, p.PanelBg, p.HeaderBg, p.Separator, p.SeparatorStrong,
                p.BodyText, p.DimText, p.HeaderText,
                p.Accent, p.AccentAlt, p.Selection, p.Focus,
                p.Info, p.Warn, p.Error, p.Success,
            ];

            foreach (var role in roles)
            {
                role.Blue.ShouldBe((byte)0);
            }
        }

        // Green carries 71.5% of relative luminance, so a true amber warn out-shouts a red error --
        // the reason Warn is a burnt orange here rather than the amber it is in Dark. If someone
        // "corrects" it back toward amber this goes red.
        [Fact]
        public void NightErrorOutshoutsWarn()
            => Luminance(GuiTheme.NightPalette.Error)
                .ShouldBeGreaterThan(Luminance(GuiTheme.NightPalette.Warn));

        // The second chart trace must not be drawn in the warning colour. Ember's own Night had
        // AccentAlt == Warn, which is invisible in a swatch row and obvious on a two-trace graph.
        [Fact]
        public void NightAccentAltDoesNotCollideWithWarn()
            => GuiTheme.NightPalette.AccentAlt.ShouldNotBe(GuiTheme.NightPalette.Warn);

        // Red on black caps at 5.25:1, so the whole Night ladder lives under that ceiling. Body
        // still clears AA; DimText provably cannot also clear it AND stay distinguishable from body,
        // which is why the rule is "anything that must be read uses BodyText".
        [Fact]
        public void NightBodyClearsAaAndDimIsDeliberatelyBelowIt()
        {
            var p = GuiTheme.NightPalette;
            var body = Contrast(p.BodyText, p.PanelBg);
            var dim = Contrast(p.DimText, p.PanelBg);

            body.ShouldBeGreaterThanOrEqualTo(4.5);
            body.ShouldBeLessThan(5.3);          // the red-on-black ceiling, not a target
            dim.ShouldBeLessThan(body);          // still reads as secondary
            dim.ShouldBeGreaterThanOrEqualTo(3.0);
        }

        [Fact]
        public void IsDarkAgreesWithTheStates()
        {
            GuiTheme.LightPalette.IsDark.ShouldBeFalse();
            GuiTheme.DarkPalette.IsDark.ShouldBeTrue();
            GuiTheme.NightPalette.IsDark.ShouldBeTrue();
        }

        // A desktop has no way to ask for dark adaptation, so inferring Night from "the OS is dark"
        // would drop an observer into red chrome at their desk.
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void SystemNeverResolvesToNight(bool desktopIsDark)
            => GuiTheme.Resolve(UiThemeState.System, desktopIsDark)
                .ShouldBe(desktopIsDark ? GuiTheme.DarkPalette : GuiTheme.LightPalette);

        // Apply reports whether the palette MOVED, which is what lets a consumer rebuild anything it
        // projects from the palette only then instead of every frame.
        [Fact]
        public void ApplyReportsOnlyRealChanges()
        {
            try
            {
                // Establish a known starting palette without asserting on the return value: the
                // suite's entry state is Dark, so an Apply(Dark) here would legitimately report no
                // change and the assertions below would be testing the wrong transition.
                GuiTheme.Apply(UiThemeState.Light, desktopIsDark: false);
                GuiTheme.Palette.ShouldBe(GuiTheme.LightPalette);

                GuiTheme.Apply(UiThemeState.Dark, desktopIsDark: true).ShouldBeTrue();
                GuiTheme.Apply(UiThemeState.Dark, desktopIsDark: true).ShouldBeFalse();

                // System + a dark desktop resolves to the same palette Dark did, so nothing moved
                // even though the STATE did.
                GuiTheme.Apply(UiThemeState.System, desktopIsDark: true).ShouldBeFalse();
                GuiTheme.State.ShouldBe(UiThemeState.System);

                GuiTheme.Apply(UiThemeState.System, desktopIsDark: false).ShouldBeTrue();
                GuiTheme.Palette.ShouldBe(GuiTheme.LightPalette);

                GuiTheme.Apply(UiThemeState.Night, desktopIsDark: false).ShouldBeTrue();
                GuiTheme.Palette.ShouldBe(GuiTheme.NightPalette);
            }
            finally
            {
                // Static app state: hand it back the way every other test expects to find it.
                GuiTheme.Apply(UiThemeState.Dark, desktopIsDark: true);
            }
        }

        // F12 (SharpCap's night-vision key). Toggling off must return to where Night was entered
        // FROM, not to a fixed default: an observer who plans in Light by day would otherwise be
        // dumped into Dark at dawn by a key they pressed to get OUT of a mode.
        [Theory]
        [InlineData("Light")]
        [InlineData("Dark")]
        public void NightTogglesOnAndBackToWhereItCameFrom(string from)
        {
            try
            {
                var origin = from == "Light" ? UiThemeState.Light : UiThemeState.Dark;
                GuiTheme.Apply(origin, desktopIsDark: origin == UiThemeState.Dark);

                GuiTheme.ToggleNight().ShouldBeTrue();
                GuiTheme.State.ShouldBe(UiThemeState.Night);
                GuiTheme.Palette.ShouldBe(GuiTheme.NightPalette);

                GuiTheme.ToggleNight().ShouldBeTrue();
                GuiTheme.State.ShouldBe(origin);
                GuiTheme.Palette.ShouldBe(ByName(from));
            }
            finally
            {
                GuiTheme.Apply(UiThemeState.Dark, desktopIsDark: true);
            }
        }

        // System is a state, not a palette, so toggling out of Night must restore the STATE -- if it
        // restored the resolved palette instead, a rig left on System would be pinned to whatever the
        // desktop happened to be at the moment F12 was first pressed.
        [Fact]
        public void TogglingOutOfNightRestoresSystemAsAState()
        {
            try
            {
                GuiTheme.Apply(UiThemeState.System, desktopIsDark: true);

                GuiTheme.ToggleNight().ShouldBeTrue();
                GuiTheme.State.ShouldBe(UiThemeState.Night);

                // Palette does not move (System + dark desktop already resolved to DarkPalette), but
                // the STATE does -- which is exactly the case a palette-only check would miss.
                GuiTheme.ToggleNight().ShouldBeTrue();
                GuiTheme.State.ShouldBe(UiThemeState.System);
                GuiTheme.Palette.ShouldBe(GuiTheme.DarkPalette);
            }
            finally
            {
                GuiTheme.Apply(UiThemeState.Dark, desktopIsDark: true);
            }
        }

        // The viewer follows the shared state rather than holding a second scheme, which is what
        // makes it go Night with everything else.
        [Fact]
        public void ViewerThemeFollowsTheSharedPalette()
        {
            try
            {
                GuiTheme.Apply(UiThemeState.Night, desktopIsDark: true);
                ViewerTheme.Palette.ShouldBe(GuiTheme.NightPalette);
                // Its own metrics stay its own: an 18px base font against the GUI's 14px.
                ViewerTheme.Metrics.BaseFontSize.ShouldBe(18f);
                GuiTheme.Metrics.BaseFontSize.ShouldBe(14f);
            }
            finally
            {
                GuiTheme.Apply(UiThemeState.Dark, desktopIsDark: true);
            }
        }
    }
}
