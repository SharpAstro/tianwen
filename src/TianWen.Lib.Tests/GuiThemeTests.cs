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
