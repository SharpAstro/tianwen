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
/// <param name="Button">The toolbar button this chord presses, when the action HAS a button. This is
/// the field that makes a key and a button one declaration rather than two.</param>
public readonly record struct ViewerShortcut(
    string Chord,
    string Description,
    InputKey? Key = null,
    InputModifier Modifiers = InputModifier.None,
    ToolbarAction? Button = null,
    ViewerShortcutRoute Route = ViewerShortcutRoute.None);

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
        new("Ctrl+O", "Open a file", InputKey.O, InputModifier.Ctrl),
        new("Ctrl+S", "Save the image as displayed (clean, 16-bit PNG)", InputKey.S, InputModifier.Ctrl),
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
        new("O / Shift+O", "Annotate: WCS grid, then the catalog objects"),
        new("G", "WCS grid on its own", InputKey.G),
        // Named here as well as on its own toolbar button, because it is the one viewer feature
        // that does something to the WHOLE PANE rather than to the picture, and a reader who has
        // not pressed it has no way to know the viewer can do it at all.
        new("Y", "The sky this frame was taken from, drawn behind it", InputKey.Y),
        new("S", "Detected stars", InputKey.S),
        new("Shift+C", "Crop to the area every sub covered / show all", InputKey.C, InputModifier.Shift),

        // ── Tone and colour ────────────────────────────────────────────────────────────────────
        new("T", "Toggle the stretch (linear / stretched)", InputKey.T,
            Button: ToolbarAction.StretchToggle, Route: ViewerShortcutRoute.Press),
        new("+ / -", "Next / previous stretch preset"),
        new("H / Shift+H", "Histogram / log scale"),
        // B earns a row for the reason its button was folded into Tone: the boost and the highlight
        // soft clip are one panel now, and the soft clip has no key of its own any more (H was it
        // until 2026-09-24), so the Tone button, and the wheel over it, is where to find it.
        new("B / Shift+B", "Curves boost / curve mode (Tone; the soft clip is on its panel)"),
        // W earns a row now that it opens a PANEL rather than toggling one flag: the sliders, the
        // photometric calibration and its provenance line all live behind it, and none of them is
        // reachable by guessing. N stays beside it because the two are the colour pair.
        new("W", "White balance: sliders, photometric calibration, reset",
            InputKey.W, Button: ToolbarAction.WhiteBalance, Route: ViewerShortcutRoute.OpenMenu),
        new("N", "Neutralise the background", InputKey.N,
            Button: ToolbarAction.BackgroundNeutralize, Route: ViewerShortcutRoute.Press),
        new("C", "Cycle the channel view", InputKey.C),
        new("D", "Cycle the demosaic algorithm", InputKey.D,
            Button: ToolbarAction.Debayer, Route: ViewerShortcutRoute.Press),

        // ── Work on the frame ──────────────────────────────────────────────────────────────────
        new("P", "Plate solve this frame", InputKey.P),
        new("E", "AI enhance", InputKey.E),
        new("A / Shift+A", "A/B compare / re-pin the before image"),

        // ── Panels ─────────────────────────────────────────────────────────────────────────────
        new("I", "Info panel", InputKey.I),
        new("L", "File list", InputKey.L),
        new("F1", "This panel", InputKey.F1, Button: ToolbarAction.Shortcuts,
            Route: ViewerShortcutRoute.OpenMenu),

        // ── Sequence and blink ─────────────────────────────────────────────────────────────────
        new("K", "Raw / stacked view (sequence)", InputKey.K),
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
