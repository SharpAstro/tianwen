using System.Collections.Immutable;
using DIR.Lib;

namespace TianWen.UI.Abstractions;

/// <summary>
/// How a key reaches the toolbar button it names.
/// </summary>
/// <remarks>
/// Declared rather than left to a switch arm, because the two routes are genuinely different and
/// the difference is not guessable from the action: <see cref="Press"/> activates the button, while
/// <see cref="OpenMenu"/> opens the panel under it WITHOUT activating. Z is the clearest case -- a
/// press on the Zoom button toggles fit/1:1, but the Z key opens the ratio menu, because the menu
/// is the only way to reach 1:N without already knowing which Ctrl+digit each ratio is.
/// </remarks>
public enum ViewerShortcutRoute
{
    /// <summary>The key does its own thing; the resolver leaves it to the switch.</summary>
    None,

    /// <summary>Press the button, exactly as a left click does, gated on the button being enabled.</summary>
    Press,

    /// <summary>Open the button's dropdown. Unhandled when the button is not painted, since there
    /// would be nothing to anchor the panel to.</summary>
    OpenMenu,
}

/// <summary>
/// One viewer keyboard binding: the chord, what it does, and the toolbar button it presses when
/// there is one.
/// </summary>
/// <param name="Chord">The chord as a reader sees it, e.g. <c>Ctrl+Shift+S</c> or <c>O / Shift+O</c>.
/// Written out rather than derived from <paramref name="Key"/> because several rows describe a PAIR
/// (a key and its Shift variant) that belongs on one line, and because a range like
/// <c>Ctrl+2 .. Ctrl+9</c> has no single key at all.</param>
/// <param name="Description">What it does, in the panel's voice.</param>
/// <param name="Key">The key, where the row is a single binding this table can resolve. Null for a
/// row that documents a pair, a range, or a gesture (the wheel, a click).</param>
/// <param name="Modifiers">Modifiers held with <paramref name="Key"/>.</param>
/// <param name="Button">The toolbar button this chord stands for, when the action HAS a button. This is
/// the field that makes a key and a button one declaration rather than two: the key acts only where the
/// host offers that button (<see cref="ViewerShortcuts.ButtonFor"/>), and a routed key presses it.</param>
/// <param name="WithShift">The row is a key and its Shift variant (<c>O / Shift+O</c>): both halves are this
/// binding, so it is found, and gated by its button, with or without Shift.</param>
public readonly record struct ViewerShortcut(
    string Chord,
    string Description,
    InputKey? Key = null,
    InputModifier Modifiers = InputModifier.None,
    ToolbarAction? Button = null,
    ViewerShortcutRoute Route = ViewerShortcutRoute.None,
    bool WithShift = false);

/// <summary>
/// Every keyboard binding the FITS viewer has, declared ONCE.
///
/// <para><b>What this replaces.</b> The same fact lived in three hand-maintained places: the
/// <c>case InputKey.X</c> arms in <c>ImageRendererBase.Input.cs</c> (the behaviour), the toolbar
/// button table (the affordance), and a prose <c>ShortcutLines</c> array in
/// <c>ImageRendererBase.Toolbar.cs</c> that the <c>?</c> panel printed (the documentation). Three
/// copies agree only while somebody remembers, and they had already drifted: a panel titled "All
/// keyboard shortcuts" was missing FIFTEEN bindings -- T, S, C, D, A, Shift+A, P, E, F, R, F1, the
/// plain +/- stretch-preset pair, Ctrl+0, Ctrl+1 and Tab. The panel is now GENERATED from this
/// table, so that particular drift cannot recur.</para>
///
/// <para><b>Why the rows are prose and not derived.</b> A chord like <c>O / Shift+O</c> documents a
/// key and its Shift variant as one idea, and <c>Ctrl+2 .. Ctrl+9</c> is a range; deriving the text
/// from an <see cref="InputKey"/> would split the first into two rows that read worse and could not
/// express the second at all. <see cref="ViewerShortcut.Key"/> is therefore populated only where the
/// row IS a single resolvable binding, and left null where the row is documentation.</para>
///
/// <para><b>Order is the reading order of the panel</b>, grouped the way someone looks for a key --
/// files, zoom, what is drawn on the picture, colour, panels, sequence, navigation -- not the order
/// the switch happens to test them in.</para>
/// </summary>
public static class ViewerShortcuts
{
    /// <summary>The binding table, in panel order.</summary>
    public static readonly ImmutableArray<ViewerShortcut> All =
    [
        // ── Files ──────────────────────────────────────────────────────────────────────────────
        // Pressed, as P, E and Shift+C are: the dialog is the HOST's, and a key that presses its button is run by
        // whichever host runs the button (tianwen-fits' controller; the Live Session preview's node, for P).
        new("Ctrl+O", "Open a file", InputKey.O, InputModifier.Ctrl, ToolbarAction.Open, ViewerShortcutRoute.Press),
        // Named, not routed: a left press on Save opens its menu, and Ctrl+S writes the clean 16-bit raster, the
        // one thing a keyboard user means by it. Its own arm does that.
        new("Ctrl+S", "Save the image as displayed (clean, 16-bit PNG)", InputKey.S, InputModifier.Ctrl,
            ToolbarAction.Save),
        new("Ctrl+Shift+S", "Save menu: with overlays, PNG depth",
            InputKey.S, InputModifier.Ctrl | InputModifier.Shift, ToolbarAction.Save,
            ViewerShortcutRoute.OpenMenu),

        // ── Zoom ───────────────────────────────────────────────────────────────────────────────
        new("Wheel / Ctrl+Wheel", "Zoom"),
        new("Ctrl + / -", "Zoom in / out"),
        new("Ctrl+0", "Zoom to fit", InputKey.D0, InputModifier.Ctrl),
        new("Ctrl+1", "Zoom 1:1", InputKey.D1, InputModifier.Ctrl),
        new("Ctrl+2 .. Ctrl+9", "Zoom 1:N"),
        new("F", "Zoom to fit", InputKey.F),
        new("R", "Zoom 1:1", InputKey.R),
        new("Z", "Zoom menu (fit / 1:1 / 1:N)", InputKey.Z, Button: ToolbarAction.Zoom,
            Route: ViewerShortcutRoute.OpenMenu),

        // ── What is drawn on the picture ───────────────────────────────────────────────────────
        new("O / Shift+O", "Annotate: WCS grid, then the catalog objects", InputKey.O,
            Button: ToolbarAction.Overlays, WithShift: true),
        new("G", "WCS grid on its own", InputKey.G, Button: ToolbarAction.Overlays),
        // Named here as well as on its own toolbar button, because it is the one viewer feature
        // that does something to the WHOLE PANE rather than to the picture, and a reader who has
        // not pressed it has no way to know the viewer can do it at all.
        new("Y", "The sky this frame was taken from, drawn behind it", InputKey.Y, Button: ToolbarAction.SkyBackdrop),
        new("S", "Detected stars", InputKey.S, Button: ToolbarAction.Stars),
        // Pressed: the scan is tens of milliseconds and the host runs it off the render thread.
        new("Shift+C", "Crop to the area every sub covered / show all", InputKey.C, InputModifier.Shift,
            ToolbarAction.AutoCrop, ViewerShortcutRoute.Press),

        // ── Tone and colour ────────────────────────────────────────────────────────────────────
        new("T", "Toggle the stretch (linear / stretched)", InputKey.T,
            Button: ToolbarAction.StretchToggle, Route: ViewerShortcutRoute.Press),
        // Two rows, not a "+ / -" pair: they are two keys, and each is found by its own.
        new("+", "Next stretch preset", InputKey.Plus, Button: ToolbarAction.StretchParams),
        new("-", "Previous stretch preset", InputKey.Minus, Button: ToolbarAction.StretchParams),
        new("H / Shift+H", "Histogram / log scale", InputKey.H, WithShift: true),
        // B earns a row for the reason its button was folded into Tone: the boost and the highlight
        // soft clip are one panel now, and the soft clip has no key of its own any more (H was it
        // until 2026-09-24), so the Tone button, and the wheel over it, is where to find it.
        new("B / Shift+B", "Curves boost / curve mode (Tone; the soft clip is on its panel)", InputKey.B,
            Button: ToolbarAction.Tone, WithShift: true),
        // W earns a row now that it opens a PANEL rather than toggling one flag: the sliders, the
        // photometric calibration and its provenance line all live behind it, and none of them is
        // reachable by guessing. N stays beside it because the two are the colour pair.
        new("W", "White balance: sliders, photometric calibration, reset",
            InputKey.W, Button: ToolbarAction.WhiteBalance, Route: ViewerShortcutRoute.OpenMenu),
        // Button named but NOT routed: a left press on this one opens its DROPDOWN in the host
        // (StandaloneViewerHost's policy tries OpenToolbarDropdown first), so routing the key as a
        // press would open a menu where the key has always toggled. See ViewerShortcutRoute.Press.
        new("N", "Neutralise the background", InputKey.N,
            Button: ToolbarAction.BackgroundNeutralize),
        new("C", "Cycle the channel view", InputKey.C, Button: ToolbarAction.Channel),
        // Same as N: Debayer has a dropdown, so the key keeps its arm (which still asks the
        // button's enabled predicate, the one duplication this row cannot yet remove).
        new("D", "Cycle the demosaic algorithm", InputKey.D, Button: ToolbarAction.Debayer),

        // ── Work on the frame ──────────────────────────────────────────────────────────────────
        // Both pressed, so the host that runs the button runs the key: the preview's solve is the node's.
        new("P", "Plate solve this frame", InputKey.P, Button: ToolbarAction.PlateSolve, Route: ViewerShortcutRoute.Press),
        new("E", "AI enhance", InputKey.E, Button: ToolbarAction.Enhance, Route: ViewerShortcutRoute.Press),
        new("A / Shift+A", "A/B compare / re-pin the before image", InputKey.A,
            Button: ToolbarAction.Compare, WithShift: true),

        // ── Panels ─────────────────────────────────────────────────────────────────────────────
        new("I", "Info panel", InputKey.I),
        new("L", "File list", InputKey.L, Button: ToolbarAction.FileList),
        new("F1", "This panel", InputKey.F1, Button: ToolbarAction.Shortcuts,
            Route: ViewerShortcutRoute.OpenMenu),

        // ── Sequence and blink ─────────────────────────────────────────────────────────────────
        new("K", "Frames / live stack (sequence)", InputKey.K),
        new("Shift+K", "Best view: the whole capture stacked once, written beside it (SER); again while it runs to cancel", InputKey.K, InputModifier.Shift),
        new("Space / Tab", "Play / pause (sequence), else blink the file list"),
        new("Shift+Space", "Blink backward"),
        new("Ctrl+Space", "Back to the frame the display is held to"),
        new("Ctrl+H", "Hold / release the display across frames", InputKey.H, InputModifier.Ctrl),

        // ── Navigation ─────────────────────────────────────────────────────────────────────────
        new("Left / Right", "Step one frame"),
        new("Home / End", "First / last frame"),
        new("Up / Down", "Previous / next file"),
        new("Click", "Select the catalogued object under the pointer"),

        // ── Window ─────────────────────────────────────────────────────────────────────────────
        new("F11", "Fullscreen", InputKey.F11),
        // Esc does BOTH, in that order, which is worth one row rather than two: a reader who has
        // just selected something needs to know the key is not going to close the viewer.
        new("Esc", "Clear the selection, else quit", InputKey.Escape),
    ];

    /// <summary>
    /// The toolbar actions whose button opens a DROPDOWN on a left press.
    /// </summary>
    /// <remarks>
    /// <para>Declared because it decides whether a key may be routed as a
    /// <see cref="ViewerShortcutRoute.Press"/>, and getting that wrong is silent. The host's press
    /// policy (<c>StandaloneViewerHost</c>) tries <c>OpenToolbarDropdown</c> first on a left press,
    /// so routing a key at one of these would open a MENU where the key has always done the thing --
    /// D would offer a demosaic list instead of cycling, N a neutralisation list instead of
    /// toggling. Both were routed that way for one commit and the tests did not see it, because
    /// they exercise the EMBEDDED default policy, which special-cases only the two popovers.</para>
    /// <para>So these keep their arms and take <see cref="ViewerShortcutRoute.OpenMenu"/> only when
    /// opening the menu IS what the key means (Z, W, F1, Ctrl+Shift+S).</para>
    /// </remarks>
    public static readonly ImmutableArray<ToolbarAction> ActionsWithADropdown =
    [
        ToolbarAction.WhiteBalance,
        ToolbarAction.Zoom,
        ToolbarAction.Save,
        ToolbarAction.Shortcuts,
        ToolbarAction.StretchLink,
        ToolbarAction.Channel,
        ToolbarAction.Debayer,
        ToolbarAction.StretchParams,
        ToolbarAction.Tone,
        ToolbarAction.BackgroundNeutralize,
    ];

    /// <summary>
    /// The toolbar button a chord stands for, routed or not, or null for a key that is the picture's own
    /// (zoom, the histogram, the info panel, a step through a sequence) or the window's (quit, full screen),
    /// which no offer governs.
    /// </summary>
    /// <remarks>
    /// The viewer acts on a key only where its host offers this button (<see cref="ToolbarOffer"/>): a key
    /// for a button the bar does not show is not the viewer's to answer. The planetary view offers no solve,
    /// so P posted one nothing ran; the Live Session preview offers no file list, so L stays the window's.
    /// It replaced a second table of keys and actions (#1142's <c>ActionOfKey</c>) that agreed with this one by hand.
    /// </remarks>
    public static ToolbarAction? ButtonFor(InputKey key, InputModifier modifiers)
    {
        foreach (var s in All)
        {
            if (s.Button is { } button && s.Key == key
                && (s.Modifiers == modifiers || (s.WithShift && modifiers == (s.Modifiers | InputModifier.Shift))))
            {
                return button;
            }
        }

        return null;
    }

    /// <summary>
    /// The routed binding for a chord, or null when no row claims it or the row routes nowhere.
    /// </summary>
    public static ViewerShortcut? TryResolveRoute(InputKey key, InputModifier modifiers)
    {
        foreach (var s in All)
        {
            if (s.Route is not ViewerShortcutRoute.None && s.Key == key && s.Modifiers == modifiers)
            {
                return s;
            }
        }

        return null;
    }

    /// <summary>
    /// The width the chord column is padded to when the panel is printed. Wide enough for the
    /// longest chord in <see cref="All"/> plus a gap; asserted by the tests rather than trusted, so
    /// a longer chord added later cannot quietly run into its own description.
    /// </summary>
    public const int ChordColumn = 21;

    /// <summary>
    /// The <c>?</c> panel's shortcut page, one row per entry. Generated rather than written out, so
    /// a binding cannot be added to the table and missed here -- which is the exact drift this type
    /// exists to end.
    /// </summary>
    public static ImmutableArray<string> HelpLines()
    {
        var lines = ImmutableArray.CreateBuilder<string>(All.Length);
        foreach (var s in All)
        {
            lines.Add(s.Chord.PadRight(ChordColumn) + s.Description);
        }

        return lines.MoveToImmutable();
    }
}
