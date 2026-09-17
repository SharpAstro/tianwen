using System;
using DIR.Lib;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Imaging;

namespace TianWen.UI.Abstractions
{
    partial class ImageRendererBase<TSurface>
    {
        /// <summary>
        /// The viewer's own chrome for the shared object panel -- theme-derived, unlike the atlas's
        /// hand-picked literals, per the user's choice ("floating, like the atlas" for placement; the
        /// colours are a parameter precisely so re-tinting one host does not touch the other).
        /// </summary>
        /// <remarks>
        /// <see cref="ObjectInfoPanel.PanelPalette.GotoBg"/>, <c>DisabledBg</c>, <c>PinBg</c> and
        /// <c>UnpinBg</c> are never painted here -- the viewer offers only Close and Open-in-atlas, no
        /// mount actions -- so they take the same neutral fill as everything else rather than inventing
        /// meaning for colours nothing draws.
        /// </remarks>
        private static ObjectInfoPanel.PanelPalette SelectionPanelPalette
            => new ObjectInfoPanel.PanelPalette(
                Background: ViewerTheme.InfoPanelBg,
                Border: ViewerTheme.Palette.SeparatorStrong,
                Text: ViewerTheme.Palette.BodyText,
                DimText: ViewerTheme.Palette.DimText,
                ButtonText: ViewerTheme.Palette.BodyText,
                GotoBg: ToolbarButtonBg,
                DisabledBg: ToolbarButtonBg,
                ViewBg: ToolbarButtonBg,
                PinBg: ToolbarButtonBg,
                UnpinBg: ToolbarButtonBg);

        /// <summary>
        /// Draws an object's picture into its slot on the selection panel. The base draws nothing, so the slot
        /// shows its dark frame: a renderer-agnostic viewer cannot draw an image, and the Vulkan host overrides it.
        /// </summary>
        /// <param name="image">The picture, whose <see cref="ObjectArticleImage.ThumbnailUrl"/> the host fetches.</param>
        /// <param name="rect">The slot, in surface pixels. Its width is what the host asks Wikimedia for.</param>
        protected virtual void DrawObjectPicture(in ObjectArticleImage image, RectF32 rect)
        {
        }

        /// <summary>
        /// Draws the selected object's floating info panel over the picture, bottom-left of the image
        /// area -- the same corner the atlas floats its own in, and for the same reason: it is a
        /// dedicated panel over the content rather than a docked strip, so it can carry Alt/Az and
        /// rise/transit/set without competing with the frame's own metadata for space.
        /// </summary>
        /// <remarks>
        /// <para><b>Site-dependent rows are gated on the BAKED altitude, not re-derived from the
        /// header.</b> <see cref="ImageRendererBase{TSurface}.BuildSelectionPanelData"/> already ran
        /// the "is the site AND the capture instant known" test once, at the click, and a NaN
        /// <see cref="SkyMapInfoPanelData.AltDeg"/> is the one signal it leaves behind that the answer
        /// was no -- reading it back here means the two can never disagree about which rows are
        /// honest.</para>
        /// <para><b>The footnote names the CAPTURE instant, never "now".</b> A photograph's Alt/Az
        /// answers "where was it when this was shot", and restating that as the reader's current wall
        /// clock would be a different, wrong answer to a question nobody asked.</para>
        /// </remarks>
        private void RenderSelectionPanel(ViewerState state)
        {
            // Chromeless hosts get no panel. The docked section this replaced was suppressed there by
            // their own ShowInfoPanel: false, and a live preview embedded in a session tab is the one
            // place a floating panel over the picture would be an intrusion rather than the point.
            // It is the same rule the status bar, the dropdowns and the tooltip already follow.
            if (state.HideChrome
                || state.SelectedObject is not { } selection
                || string.IsNullOrEmpty(FontPath))
            {
                return;
            }

            var dpiScale = DpiScale;
            var area = _layout.ImageArea;

            var haveSiteRows = !double.IsNaN(selection.AltDeg);
            DateTimeOffset? capturedAt = null;
            if (haveSiteRows && _document is { } doc)
            {
                var meta = doc.UnstretchedImage.ImageMeta;
                capturedAt = FrameSiteResolver.CapturedAt(in meta);
            }

            var palette = SelectionPanelPalette;
            var options = new ObjectInfoPanel.PanelDisplayOptions(
                ShowAltAz: haveSiteRows,
                ShowRiseSet: haveSiteRows,
                TimeZone: capturedAt?.Offset ?? TimeSpan.Zero,
                Footnote: capturedAt is { } cap ? $"at capture, {cap:yyyy-MM-dd HH:mm}" : null,
                Picture: ObjectInfoPanel.PictureFor(selection.Index, LoadedCatalog));

            var actions = new ObjectInfoPanel.PanelActions(
                Close: () =>
                {
                    state.SelectedObject = null;
                    state.StatusMessage = null;
                    state.PictureExpanded = false;
                    state.NeedsRedraw = true;
                },
                OpenInAtlas: () =>
                {
                    var img = _document?.UnstretchedImage;
                    var fovDeg = img is { } i
                        ? SkyAtlasLink.FieldOfViewDeg(_document?.Wcs, i.Width, i.Height)
                        : (double?)null;
                    var token = SkyAtlasLink.TokenFor(selection.Canonical, selection.Name);
                    PostSignal(new OpenUrlSignal(SkyAtlasLink.For(
                        selection.RA, selection.Dec, fovDeg, img?.ImageMeta.ExposureStartTime, token)));
                    state.StatusMessage = "Opening the sky atlas...";
                    state.NeedsRedraw = true;
                });

            var pw = ObjectInfoPanel.DesignWidth * dpiScale;
            var ph = ObjectInfoPanel.DesignHeight(in options, in actions) * dpiScale;
            var px = area.X + (12f * dpiScale);

            // Never climbs out of the TOP of the image area. Nothing clips this panel (the picture's
            // clip is closed by the time it draws), so in a pane shorter than the panel it has to
            // overflow SOMEWHERE -- and the clamp chooses which end is lost. Overflowing downward
            // costs the button row; overflowing upward costs the object's NAME, which is the row the
            // panel exists for and the one the ring on the picture is silently agreeing with.
            var py = MathF.Max(area.Y, area.Y + area.Height - ph - (12f * dpiScale));

            // The border rect is the panel's whole visual extent, so ONE no-op Clickable there swallows
            // a press anywhere on the panel -- its blank area included, not just its buttons -- rather
            // than letting it fall through to a pan or a re-selected object underneath. Registered on
            // the border layer rather than the background one so the two rects do not both claim the
            // region; matches the atlas's own panel, fixed together rather than one host at a time.
            RenderLayout(Layout.Builder.Spacer().Bg(palette.Border)
                    .Clickable(new HitResult.ButtonHit("SelectionPanelBackground"), _ => { }),
                new RectF32(px - 1, py - 1, pw + 2, ph + 2));
            RenderLayout(Layout.Builder.Spacer().Bg(palette.Background), new RectF32(px, py, pw, ph));

            var textX = px + (10f * dpiScale);
            var textW = pw - (40f * dpiScale);
            var textBlockH = ObjectInfoPanel.DesignTextBlockHeight(in options) * dpiScale;
            RenderLayout(ObjectInfoPanel.BuildTextRows(in selection, in options, in palette),
                new RectF32(textX, py, textW, textBlockH), scale: Scale);

            // Under the rows and above the buttons, as in the atlas: at the top the close affordance would sit
            // on the picture.
            if (options.Picture is not { } picture)
            {
                // Nothing to expand: this object's article kept no picture.
                state.PictureExpanded = false;
            }
            else
            {
                RenderLayout(ObjectInfoPanel.BuildPictureSection(in picture, in palette, onOpen: () =>
                    {
                        state.PictureExpanded = true;
                        state.NeedsRedraw = true;
                    }),
                    new RectF32(px, py + textBlockH + (8f * dpiScale), pw, ObjectInfoPanel.DesignPictureSectionHeight(in picture) * dpiScale),
                    scale: Scale,
                    drawFill: (fill, rect) =>
                    {
                        if (fill.Key == ObjectInfoPanel.PictureFillKey)
                        {
                            DrawObjectPicture(in picture, rect);
                        }
                    });
            }

            var btnH = ObjectInfoPanel.DesignButtonHeight * dpiScale;
            var btnY = py + ph - btnH - (8f * dpiScale);
            if (ObjectInfoPanel.BuildButtonRow(in actions, in palette) is { } buttonRow)
            {
                RenderLayout(buttonRow, new RectF32(px, btnY, pw, btnH), scale: Scale);
            }

            var closeSize = ObjectInfoPanel.DesignCloseSize * dpiScale;
            if (ObjectInfoPanel.BuildCloseButton(in actions, in palette) is { } closeNode)
            {
                RenderLayout(closeNode,
                    new RectF32(px + pw - closeSize, py, closeSize, closeSize), scale: Scale);
            }

            // The large view covers the photograph, so it is drawn after the panel. The same host hook fills
            // it, at a rect big enough that a wider standard thumbnail is asked for.
            if (state.PictureExpanded && options.Picture is { } expanded)
            {
                var pictureRect = ObjectInfoPanel.LargePictureRect(area, in expanded, dpiScale);
                var (scrim, body) = ObjectInfoPanel.BuildLargePicture(in expanded, in palette, pictureRect.Height / dpiScale, () =>
                {
                    state.PictureExpanded = false;
                    state.NeedsRedraw = true;
                });
                RenderLayout(scrim, area);
                RenderLayout(body,
                    new RectF32(pictureRect.X, pictureRect.Y, pictureRect.Width,
                        pictureRect.Height + (ObjectInfoPanel.DesignRowHeight * dpiScale)),
                    scale: Scale,
                    drawFill: (fill, rect) =>
                    {
                        if (fill.Key == ObjectInfoPanel.LargePictureFillKey)
                        {
                            DrawObjectPicture(in expanded, rect);
                        }
                    });
            }
        }
    }
}
