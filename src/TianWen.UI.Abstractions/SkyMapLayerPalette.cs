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

        /// <summary>How far down the right edge the panel starts, design units.</summary>
        public const float TopOffset = 8f;

        private static readonly RGBAColor32 PanelBg     = new(0x18, 0x18, 0x20, 0xE0);
        private static readonly RGBAColor32 HeaderInk   = new(0x90, 0x90, 0x9C, 0xFF);
        private static readonly RGBAColor32 RowOnBg     = new(0x33, 0x42, 0x52, 0xFF);
        private static readonly RGBAColor32 RowOffBg    = new(0x22, 0x22, 0x2A, 0xFF);
        private static readonly RGBAColor32 RowHoverBg  = new(0x3E, 0x4E, 0x60, 0xFF);
        private static readonly RGBAColor32 KeyChipBg   = new(0x2C, 0x2C, 0x36, 0xFF);
        private static readonly RGBAColor32 OnInk       = new(0xE8, 0xE8, 0xF0, 0xFF);
        private static readonly RGBAColor32 OffInk      = new(0x9A, 0x9A, 0xA6, 0xFF);
        private static readonly RGBAColor32 DisabledInk = new(0x5A, 0x5A, 0x64, 0xFF);

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
        public static Layout.Node Build(SkyMapState state, float fontSize, Action<SkyMapLayer> onToggle)
        {
            ArgumentNullException.ThrowIfNull(state);
            ArgumentNullException.ThrowIfNull(onToggle);

            var layers = SkyMapLayers.All;

            // Header plus one row per layer. Built into an array rather than a params span because the
            // count is the table's, not a literal.
            var children = new Layout.Node[layers.Length + 1];
            children[0] = Layout.Builder.Text("LAYERS", fontSize * 0.85f, HeaderInk,
                    TextAlign.Near, TextAlign.Center)
                .RowH(RowHeight * 0.9f);

            for (var i = 0; i < layers.Length; i++)
            {
                // Copy out of the ImmutableArray so the click lambda captures a value rather than the
                // loop's index into a collection it would have to re-read.
                var layer = layers[i];
                var available = layer.Available(state);
                var on = available && layer.IsOn(state);
                var ink = !available ? DisabledInk : on ? OnInk : OffInk;

                var row = Layout.Builder.HStack(
                        Layout.Builder.Text(layer.KeyLabel, fontSize, ink,
                                TextAlign.Center, TextAlign.Center)
                            .WFixed(13f).HStar().Bg(KeyChipBg),
                        Layout.Builder.Text(layer.Label, fontSize, ink,
                                TextAlign.Near, TextAlign.Center)
                            .WStar().HStar())
                    .WithGap(5f)
                    .RowH(RowHeight)
                    .Bg(on ? RowOnBg : RowOffBg);

                // A layer with nothing to draw is shown and dimmed rather than hidden: a row that comes
                // and goes is a palette whose shape moves under the pointer, and "Milky Way, greyed"
                // tells the reader the texture is missing where an absent row tells them nothing.
                if (available)
                {
                    row = row.BgHover(RowHoverBg)
                        .Clickable(new HitResult.ButtonHit(RowAction(in layer)),
                            _ => onToggle(layer), CursorKind.Pointer);
                }

                children[i + 1] = row;
            }

            var panel = Layout.Builder.VStack(children)
                .WFixed(PanelWidth)
                .Bg(PanelBg)
                .Pad(5f)
                .WithGap(2f);

            return Layout.Builder.Anchored(panel, Layout.DockSide.Right,
                offsetAlong: TopOffset, margin: Margin);
        }
    }
}
