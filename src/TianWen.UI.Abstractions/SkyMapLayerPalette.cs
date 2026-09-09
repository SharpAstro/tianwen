using System;
using DIR.Lib;

// Layout is aliased project-wide (a global using), so the tree reads as Layout.Builder.* here without
// a local alias and the collision-prone barewords (Node, Content) stay out of scope.

namespace TianWen.UI.Abstractions
{
    /// <summary>
    /// The sky map's layer palette: one clickable row per <see cref="SkyMapLayers.All"/> entry,
    /// floating against the content area's right edge.
    ///
    /// <para><b>Pure on purpose.</b> It returns a <see cref="Layout.Node"/> and touches no surface, so
    /// its geometry and its click bindings are testable with a stub measure context, exactly as
    /// <c>PlannerTab.BuildFrameLayout</c> is. The tab renders what this returns and binds the clicks
    /// from the same arranged rect, so draw == hit by construction.</para>
    ///
    /// <para><b>Each row prints its key, so the palette IS the legend.</b> The ten layers were
    /// reachable only by a bare letter with nothing on screen naming it -- and on the web build, which
    /// hosts the same tab on a device with no keyboard, not reachable at all. A row that says
    /// <c>G  Grid</c> answers both at once, and teaches the key to a reader who would rather press
    /// it.</para>
    /// </summary>
    /// <remarks>
    /// The colours are this map's own dark literals rather than <see cref="GuiTheme"/>'s palette,
    /// matching the search and info panels beside it: a star chart is dark in every theme, and chrome
    /// drawn over it has to stay legible against the sky rather than against the app.
    /// </remarks>
    public static class SkyMapLayerPalette
    {
        /// <summary>Panel width in DESIGN units; the renderer applies the DPI scale.</summary>
        public const float PanelWidth = 124f;

        /// <summary>Height of one layer row, design units.</summary>
        public const float RowHeight = 19f;

        /// <summary>Inset from the edge it floats against, design units.</summary>
        public const float Margin = 10f;

        /// <summary>
        /// How far down the right edge the panel starts, design units. Equal to <see cref="Margin"/>
        /// deliberately: the clamp keeps the panel at least a margin inside the rect on every side, so
        /// a smaller default is silently overridden and the panel does not sit where the constant
        /// says. It was 8 against a margin of 10, which measured as a 2-unit lie.
        /// </summary>
        public const float TopOffset = Margin;

        /// <summary>Untouched for this long, the panel starts to recede. Seconds.</summary>
        public const float IdleDelaySeconds = 2.5f;

        /// <summary>How long the recede takes once it starts. Seconds.</summary>
        public const float FadeSeconds = 0.4f;

        /// <summary>
        /// What the panel fades TO, as a factor on every colour's alpha. Not to nothing: a reader who
        /// has forgotten the panel is there should still see that it is, and which layers are lit,
        /// without moving the pointer to find out.
        /// </summary>
        public const float IdleAlpha = 0.40f;

        /// <summary>
        /// Two grip presses closer together than this are a double-click, which collapses the panel.
        /// Timed here rather than read off the event because a clickable region's handler is given
        /// modifiers and nothing else: the host counts clicks (<c>GuiEventHandlerBase</c> uses it for
        /// select-all in a text field) but does not forward the count to a region's callback.
        /// </summary>
        public const float DoubleClickSeconds = 0.4f;

        /// <summary>Whether a grip press this soon after the last one is the second of a pair.</summary>
        public static bool IsDoubleClick(float secondsSinceLastPress)
            => secondsSinceLastPress <= DoubleClickSeconds;

        /// <summary>
        /// The fade factor for a panel last engaged <paramref name="idleSeconds"/> ago. Hover or a
        /// live drag holds it fully present. Pure, so the curve is testable without a clock.
        /// </summary>
        public static float FadeFor(float idleSeconds, bool engaged)
            => engaged || idleSeconds <= IdleDelaySeconds ? 1f
                : idleSeconds >= IdleDelaySeconds + FadeSeconds ? IdleAlpha
                : 1f - (1f - IdleAlpha) * ((idleSeconds - IdleDelaySeconds) / FadeSeconds);

        // Alpha is the point of these, not decoration. This panel sits ON the sky, and an opaque card
        // is a hole punched in the thing the reader came to look at -- the first cut set 0xE0 on the
        // panel and then 0xFF on every row, which is most of its area, so it was opaque in all but
        // name. The ON rows stay the most solid of the three because "which layers are on" is the one
        // thing the panel says that the old status-strip hint could not, and that has to survive a
        // bright star field behind it.
        private static readonly RGBAColor32 PanelBg     = new(0x14, 0x14, 0x1C, 0x9C);
        private static readonly RGBAColor32 HeaderInk   = new(0x9A, 0x9A, 0xA8, 0xFF);
        private static readonly RGBAColor32 GripBg      = new(0x2A, 0x2A, 0x36, 0xB4);
        private static readonly RGBAColor32 RowOnBg     = new(0x37, 0x48, 0x5C, 0xDC);
        private static readonly RGBAColor32 RowOffBg    = new(0x20, 0x20, 0x2A, 0xA0);
        private static readonly RGBAColor32 RowHoverBg  = new(0x44, 0x56, 0x6A, 0xE6);
        private static readonly RGBAColor32 KeyChipBg   = new(0x2C, 0x2C, 0x36, 0xC8);
        private static readonly RGBAColor32 OnInk       = new(0xE8, 0xE8, 0xF0, 0xFF);
        private static readonly RGBAColor32 OffInk      = new(0x9A, 0x9A, 0xA6, 0xFF);
        private static readonly RGBAColor32 DisabledInk = new(0x5A, 0x5A, 0x64, 0xFF);

        /// <summary>
        /// The action id the grip carries, so the render pass can find its arranged rect and a
        /// mouse-down can be tested against the very rect that was drawn.
        /// </summary>
        public const string GripAction = "SkyMapLayerPaletteGrip";

        /// <summary>The action id a row's <see cref="HitResult.ButtonHit"/> carries, keyed by the
        /// layer's key label so a test can name a row without depending on its index.</summary>
        public static string RowAction(in SkyMapLayer layer) => "SkyMapLayer:" + layer.KeyLabel;

        /// <summary>
        /// Builds the palette, floated against <see cref="Layout.DockSide.Right"/> of whatever rect it is
        /// arranged into. Arrange it against the map's own content rect: the clamp is the engine's, so
        /// a window resize or a sidebar opening under the panel shoves it back into view instead of
        /// stranding it off-screen.
        /// </summary>
        /// <param name="state">Read for each layer's on/off and availability.</param>
        /// <param name="fontSize">Row text size in DESIGN units.</param>
        /// <param name="onToggle">Invoked with the layer a click landed on.</param>
        /// <param name="onGripPress">
        /// Invoked when the grip is pressed, to begin a drag. A press, not a release: the host
        /// dispatches a widget's clickable regions from its mouse-DOWN handler, which is also why the
        /// grip has to be a region at all rather than a rect the tab hit-tests itself -- a press that
        /// lands on any region never reaches the tab's own <c>MouseDown</c> path.
        /// </param>
        /// <param name="fade">
        /// Alpha factor over every colour in the panel, from <see cref="FadeFor"/>. 1 is fully
        /// present; lower is the idle recede.
        /// </param>
        public static Layout.Node Build(SkyMapState state, float fontSize, Action<SkyMapLayer> onToggle,
            Action onGripPress, float fade = 1f)
        {
            ArgumentNullException.ThrowIfNull(state);
            ArgumentNullException.ThrowIfNull(onToggle);
            ArgumentNullException.ThrowIfNull(onGripPress);

            var layers = SkyMapLayers.All;
            // WithAlpha PREMULTIPLIES by the mask it is given, so the mask IS the fade -- passing
            // "this colour's alpha times the fade" would apply the alpha twice and dim a panel at rest.
            RGBAColor32 Faded(RGBAColor32 c) => c.WithAlpha((byte)Math.Clamp(fade * 255f, 0f, 255f));

            // Header plus one row per layer. Built into an array rather than a params span because the
            // count is the table's, not a literal.
            // Collapsed, the panel is its own title bar: enough to say it is there, where it is, and
            // how many layers are lit, in one row. Double-clicking the grip is what gets here and back.
            var rowCount = state.LayerPaletteCollapsed ? 0 : layers.Length;
            var children = new Layout.Node[rowCount + 1];

            // The header IS the grip. A separate handle would cost a row and teach nothing: a title
            // bar is where a reader already tries to drag a panel from, and the Move cursor over it
            // says so before they try. Pressing it BEGINS the drag; the moves and the release are the
            // tab's, since only the tab sees them.
            var lit = 0;
            foreach (var l in layers)
            {
                if (l.Available(state) && l.IsOn(state))
                {
                    lit++;
                }
            }

            children[0] = Layout.Builder.Text(
                    state.LayerPaletteCollapsed ? $"LAYERS  {lit}/{layers.Length}" : "LAYERS",
                    fontSize * 0.85f, Faded(HeaderInk),
                    TextAlign.Near, TextAlign.Center)
                .RowH(RowHeight * 0.9f)
                .Bg(Faded(GripBg))
                .Clickable(new HitResult.ButtonHit(GripAction), _ => onGripPress(), CursorKind.Move);

            for (var i = 0; i < rowCount; i++)
            {
                // Copy out of the ImmutableArray so the click lambda captures a value rather than the
                // loop's index into a collection it would have to re-read.
                var layer = layers[i];
                var available = layer.Available(state);
                var on = available && layer.IsOn(state);
                var ink = Faded(!available ? DisabledInk : on ? OnInk : OffInk);

                var row = Layout.Builder.HStack(
                        Layout.Builder.Text(layer.KeyLabel, fontSize, ink,
                                TextAlign.Center, TextAlign.Center)
                            .WFixed(13f).HStar().Bg(Faded(KeyChipBg)),
                        Layout.Builder.Text(layer.Label, fontSize, ink,
                                TextAlign.Near, TextAlign.Center)
                            .WStar().HStar())
                    .WithGap(5f)
                    .RowH(RowHeight)
                    .Bg(Faded(on ? RowOnBg : RowOffBg));

                // A layer with nothing to draw is shown and dimmed rather than hidden: a row that comes
                // and goes is a palette whose shape moves under the pointer, and "Milky Way, greyed"
                // tells the reader the texture is missing where an absent row tells them nothing.
                if (available)
                {
                    row = row.BgHover(Faded(RowHoverBg))
                        .Clickable(new HitResult.ButtonHit(RowAction(in layer)),
                            _ => onToggle(layer), CursorKind.Pointer);
                }

                children[i + 1] = row;
            }

            var panel = Layout.Builder.VStack(children)
                .WFixed(PanelWidth)
                .Bg(Faded(PanelBg))
                .Pad(5f)
                .WithGap(2f);

            return Layout.Builder.Anchored(panel, Layout.DockSide.Right,
                offsetAlong: state.LayerPaletteOffset, margin: Margin);
        }
    }
}
