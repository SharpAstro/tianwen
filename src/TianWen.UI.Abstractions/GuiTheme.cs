using DIR.Lib;

namespace TianWen.UI.Abstractions
{
    /// <summary>
    /// TianWen's chrome theme: the three concrete palettes (Light / Dark / Night) and the currently
    /// resolved one. Single source of truth for the shared chrome colours and base metrics that
    /// were once duplicated as <c>private static readonly</c> constants across every tab.
    /// Tab-specific colours (sky map, guide graph, plate-solve overlays, planner pins) still live
    /// with their owner -- but they now need a per-state variant too, so a NEW one belongs here
    /// unless it is genuinely one tab's business.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The resolved palette is a single reference, swapped by <see cref="Apply"/>.</b> Readers
    /// take <see cref="Palette"/> per frame, and a state that resolved on every read would both
    /// re-derive constantly and let a reader observe a torn pair (the state changed but the
    /// desktop's light/dark answer not yet, or the reverse). One reference write is atomic, so a
    /// frame sees one palette or the other and never a mixture. This is also what makes DIR.Lib's
    /// "derive when the theme MOVES, not per frame" advice structural here rather than advisory.
    /// </para>
    /// <para>
    /// A process-wide static rather than a widget property, unlike <c>DpiScale</c> and
    /// <c>FontPath</c>: those genuinely differ per window, whereas the theme is one user
    /// preference for the whole app, and threading it through every tab would buy nothing.
    /// </para>
    /// <para>
    /// Values chosen 2026-08-07 from <c>docs/plans/colour-theme-mocks/studio.html</c>: the "Plate"
    /// core for Light and Dark, the "Ember" core for Night.
    /// </para>
    /// </remarks>
    public static class GuiTheme
    {
        /// <summary>Cool neutrals on paper white. Daytime planning, stacking, reviewing subs.</summary>
        public static UiPalette LightPalette { get; } = new UiPalette
        {
            ContentBg       = new RGBAColor32(0xf2, 0xf4, 0xf6, 0xff),
            PanelBg         = new RGBAColor32(0xff, 0xff, 0xff, 0xff),
            HeaderBg        = new RGBAColor32(0xe9, 0xed, 0xf1, 0xff),
            Separator       = new RGBAColor32(0xd8, 0xde, 0xe5, 0xff),
            SeparatorStrong = new RGBAColor32(0xbc, 0xc5, 0xcf, 0xff),
            BodyText        = new RGBAColor32(0x14, 0x18, 0x1d, 0xff),
            DimText         = new RGBAColor32(0x5a, 0x62, 0x6c, 0xff),
            Accent          = new RGBAColor32(0x0a, 0x63, 0xa8, 0xff),
            Selection       = new RGBAColor32(0xd6, 0xe6, 0xf5, 0xff),
            Info            = new RGBAColor32(0x0a, 0x63, 0xa8, 0xff),
            Warn            = new RGBAColor32(0x8a, 0x50, 0x00, 0xff),
            Error           = new RGBAColor32(0xb0, 0x2a, 0x20, 0xff),
            Success         = new RGBAColor32(0x1a, 0x7f, 0x4b, 0xff),
        };

        /// <summary>
        /// The default, and a retune of the scheme this app always had rather than a replacement:
        /// same cool-neutral family, but the ramp gains a real blue bias (the old
        /// <c>#1e1e28</c> had R=G with only blue lifted, which reads faintly violet) and every text
        /// pair gains contrast -- body 10.29:1 -> 13.78:1, dim 4.66:1 -> 5.57:1, accent 6.96:1 ->
        /// 9.21:1 against <see cref="UiPalette.PanelBg"/>.
        /// </summary>
        /// <remarks>
        /// <see cref="UiPalette.Success"/> is stated rather than left to its accent fallback, and the
        /// bug that forced it is why Light states one too. The home board's status dot is a THREE-way
        /// mark (offline / online / running) drawn from <c>DimText</c> / <c>Success</c> / <c>Info</c>
        /// -- and here <c>Info</c> IS <c>Accent</c>, so an unstated Success collapsed online and
        /// running onto the identical dot. The accent fallback is correct only where the green
        /// channel is genuinely unavailable, which is Night alone.
        /// </remarks>
        public static UiPalette DarkPalette { get; } = new UiPalette
        {
            ContentBg       = new RGBAColor32(0x10, 0x13, 0x18, 0xff),
            PanelBg         = new RGBAColor32(0x17, 0x1b, 0x22, 0xff),
            HeaderBg        = new RGBAColor32(0x1e, 0x23, 0x2c, 0xff),
            Separator       = new RGBAColor32(0x2a, 0x30, 0x39, 0xff),
            SeparatorStrong = new RGBAColor32(0x3c, 0x44, 0x4f, 0xff),
            BodyText        = new RGBAColor32(0xe2, 0xe6, 0xec, 0xff),
            DimText         = new RGBAColor32(0x8b, 0x93, 0x9f, 0xff),
            Accent          = new RGBAColor32(0x7c, 0xc4, 0xff, 0xff),
            Selection       = new RGBAColor32(0x22, 0x30, 0x3d, 0xff),
            Info            = new RGBAColor32(0x7c, 0xc4, 0xff, 0xff),
            Warn            = new RGBAColor32(0xe8, 0xa3, 0x3c, 0xff),
            Error           = new RGBAColor32(0xff, 0x7a, 0x70, 0xff),
            Success         = new RGBAColor32(0x4c, 0xc3, 0x8a, 0xff),
        };

        /// <summary>
        /// Dark-adaptation mode: blue is zero in every role, and green is spent only where hue
        /// separation has to be bought. See <see cref="UiThemeState.Night"/> for why, and for the
        /// two rules the values encode.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <see cref="UiPalette.Warn"/> is a burnt orange rather than a true amber, and that is not
        /// a taste call. Green carries 71.5% of relative luminance, so an amber warn out-shouts a
        /// red error: dialling it back to <c>#cc5c00</c> (0.205) puts it under <c>Error</c>
        /// <c>#ff1500</c> (0.218) while keeping the hues apart. The pair is only about 22 degrees
        /// apart, the tightest call in the palette, so severity should also carry a form cue
        /// (a filled stripe against an outlined one) rather than resting on hue alone.
        /// </para>
        /// <para>
        /// <see cref="UiPalette.AccentAlt"/> is muted specifically so a two-trace chart does not
        /// draw its second series in the warning colour.
        /// </para>
        /// </remarks>
        public static UiPalette NightPalette { get; } = new UiPalette
        {
            ContentBg       = new RGBAColor32(0x00, 0x00, 0x00, 0xff),
            PanelBg         = new RGBAColor32(0x0c, 0x04, 0x00, 0xff),
            HeaderBg        = new RGBAColor32(0x18, 0x08, 0x00, 0xff),
            Separator       = new RGBAColor32(0x2e, 0x12, 0x00, 0xff),
            SeparatorStrong = new RGBAColor32(0x4d, 0x1e, 0x00, 0xff),
            BodyText        = new RGBAColor32(0xe0, 0x4a, 0x00, 0xff),
            DimText         = new RGBAColor32(0xb8, 0x3c, 0x00, 0xff),
            Accent          = new RGBAColor32(0xff, 0x6a, 0x00, 0xff),
            AccentAlt       = new RGBAColor32(0xa8, 0x3c, 0x00, 0xff),
            Selection       = new RGBAColor32(0x3a, 0x10, 0x00, 0xff),
            Info            = new RGBAColor32(0x8c, 0x30, 0x00, 0xff),
            Warn            = new RGBAColor32(0xcc, 0x5c, 0x00, 0xff),
            Error           = new RGBAColor32(0xff, 0x15, 0x00, 0xff),
        };

        /// <summary>Shared base (unscaled) layout metrics. Identical across states.</summary>
        public static UiMetrics Metrics { get; } = new UiMetrics(
            BaseFontSize: 14f,
            Padding:      8f,
            HeaderHeight: 28f,
            ItemHeight:   24f,
            ButtonHeight: 28f);

        private static UiTheme _current = new UiTheme { Palette = DarkPalette, Metrics = Metrics };

        /// <summary>The state the user picked. Change it through <see cref="Apply"/>.</summary>
        public static UiThemeState State { get; private set; } = UiThemeState.Dark;

        /// <summary>
        /// The desktop's own light/dark answer as last reported by the host. Only consulted when
        /// <see cref="State"/> is <see cref="UiThemeState.System"/>.
        /// </summary>
        public static bool DesktopIsDark { get; private set; } = true;

        /// <summary>The palette every consumer paints with. One reference read, never torn.</summary>
        public static UiPalette Palette => _current.Palette;

        /// <summary>The combined theme (palette + metrics).</summary>
        public static UiTheme Theme => _current;

        /// <summary>
        /// Adopt a state, plus the desktop's current light/dark answer (which the host reads from
        /// SDL once per frame and only matters under <see cref="UiThemeState.System"/>). Resolves
        /// once and swaps the result in as a single reference write.
        /// </summary>
        /// <returns>True when the resolved palette actually changed, so the caller can rebuild
        /// whatever it projects from the palette -- a tab bar's colours, a cached gradient -- only
        /// then, rather than every frame.</returns>
        public static bool Apply(UiThemeState state, bool desktopIsDark)
        {
            State = state;
            DesktopIsDark = desktopIsDark;

            var resolved = Resolve(state, desktopIsDark);
            if (ReferenceEquals(resolved, _current.Palette))
            {
                return false;
            }

            _current = new UiTheme { Palette = resolved, Metrics = Metrics };
            return true;
        }

        /// <summary>
        /// Text to lay ON a filled colour chip, chosen from the fill's own lightness.
        /// </summary>
        /// <remarks>
        /// A semantic fill's lightness flips between states: <see cref="UiPalette.Warn"/> is a dark ochre
        /// in Light and a bright amber in Dark, and <see cref="UiPalette.Success"/> is a deep green in
        /// Light and a light one in Dark. So ANY fixed ink is legible in one state and invisible in
        /// another, and a de-emphasised role is wrong in all of them: a <c>DimText</c> label on the green
        /// Connect All fill measured about 1.4:1. Ask here instead of picking a colour per call site.
        /// </remarks>
        public static RGBAColor32 InkOn(RGBAColor32 fill) => fill.Luminance < 0x80
            ? new RGBAColor32(0xff, 0xff, 0xff, 0xff)
            : new RGBAColor32(0x14, 0x10, 0x08, 0xff);

        // What Night was toggled ON from, so toggling OFF restores it rather than guessing. Seeded to
        // the startup state so an F12 pressed before anything else has touched the theme still returns
        // somewhere sensible.
        private static UiThemeState _stateBeforeNight = UiThemeState.Dark;

        /// <summary>
        /// Toggles dark-adaptation mode, the SharpCap F12 gesture. Turning it ON remembers the state it
        /// came from; turning it OFF restores that state rather than assuming a default, so an observer
        /// who runs the app in Light by day, or on System, gets their own scheme back at dawn instead of
        /// whichever one the app happened to ship with.
        /// </summary>
        /// <returns>True when the resolved palette actually changed, same contract as
        /// <see cref="Apply"/>. Note it can be false even though the state moved -- toggling off a Night
        /// that was entered from System on a dark desktop lands back on the same
        /// <see cref="DarkPalette"/> reference.</returns>
        public static bool ToggleNight()
        {
            if (State == UiThemeState.Night)
            {
                return Apply(_stateBeforeNight, DesktopIsDark);
            }

            _stateBeforeNight = State;
            return Apply(UiThemeState.Night, DesktopIsDark);
        }

        /// <summary>
        /// Which palette a state resolves to. Pure, so the settings UI can preview a state without
        /// adopting it. <see cref="UiThemeState.System"/> never resolves to
        /// <see cref="NightPalette"/>: a desktop has no way to ask for dark adaptation, and
        /// inferring it from "the OS is in dark mode" would drop an observer into red chrome at
        /// their desk.
        /// </summary>
        public static UiPalette Resolve(UiThemeState state, bool desktopIsDark) => state switch
        {
            UiThemeState.Light => LightPalette,
            UiThemeState.Dark => DarkPalette,
            UiThemeState.Night => NightPalette,
            _ => desktopIsDark ? DarkPalette : LightPalette,
        };
    }
}
