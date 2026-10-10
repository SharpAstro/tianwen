using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;
using DIR.Lib;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.PlateSolve;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Imaging;
using TianWen.UI.Abstractions.Overlays;

namespace TianWen.UI.Abstractions
{
    partial class ImageRendererBase<TSurface>
    {
        // SER transport bar geometry, computed in ComputeLayout (default/empty for a still image): the
        // whole strip and, within it, the scrub track rect that maps cursor-X <-> frame index.
        private RectF32 _transportRect;
        private RectF32 _scrubTrackRect;

        // -----------------------------------------------------------------------
        // SER transport bar (play/pause, scrub, frame + timestamp + fps readout)
        //
        // Drawn into the reserved _transportRect (the image pane was shrunk in ComputeLayout so the strip
        // never overlaps the picture). Reads only ViewerState + the cached _source accessors -- timestamp
        // lookups hit the source's managed cache, never the lazy file-tail trailer, so nothing here does
        // disk I/O. The scrub track rect is captured for the press/drag -> frame mapping in ScrubAt.
        // -----------------------------------------------------------------------

        /// <summary>The widest label a segment of the view switch takes: a batch stack's at its end, Auto's being the wider word.</summary>
        private const string ViewSwitchWidestLabel = "Auto 100%";

        /// <summary>The play / pause button's fill, which the bar draws its mark into.</summary>
        private const string PlayPauseFillKey = "PlayPause";

        private void RenderTransportBar(ViewerState state)
        {
            var r = _transportRect;
            if (r.Width <= 0 || r.Height <= 0 || string.IsNullOrEmpty(FontPath))
            {
                _scrubTrackRect = default;
                return;
            }

            FillRect(r.X, r.Y, r.Width, r.Height, TransportBg);

            var pad = PanelPadding;
            var fs = ToolbarFontSize;
            var contentH = r.Height - pad * 2;
            if (contentH <= 0f)
            {
                // Window minimized to a sliver -- nothing usable to draw; bail before any size math.
                _scrubTrackRect = default;
                return;
            }
            var textY = r.Y + (r.Height - fs) / 2f;
            var btnX = r.X + pad;
            var btnY = r.Y + pad;
            var btnSize = contentH;
            var ink = RGBAColor32.FromFloat(0.92f, 0.92f, 0.95f, 1f);

            // The view switch (#1314 part 2): the frames, the live rolling stack about the playhead, or the whole capture's best stack,
            // one ButtonGroup where the RAW / STACK toggle was. "Live..." while the live stack's first master is still computing, and the
            // best stack's progress on its own segment while it runs (the view under it stays on show until it ends). Arranged in device
            // pixels at DesignScale.One, as the toolbar's run is, since the bar around it is.
            // Auto (A4, #1391) is the capture identified and stacked with nothing asked, Best the panel's choices; the running one of the
            // two last chosen shows its progress.
            var liveLabel = state.ShowStacked && !state.ShowBest && _source is not LiveStackPreviewSource ? "Live..." : "Live";
            var autoLabel = state.ShowAuto && state.BestStackProgress is { } autoDone
                ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"Auto {autoDone * 100:0}%")
                : "Auto";
            var bestLabel = !state.ShowAuto && state.BestStackProgress is { } done
                ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"Best {done * 100:0}%")
                : "Best";
            ReadOnlySpan<Layout.ButtonGroupOption<PlanetaryView>> views =
            [
                new(PlanetaryView.Frames, "Frames") { Hit = new HitResult.ButtonHit("ViewFrames") },
                new(PlanetaryView.Live, liveLabel) { Hit = new HitResult.ButtonHit("ViewLive") },
                new(PlanetaryView.Auto, autoLabel) { Hit = new HitResult.ButtonHit("ViewAuto") },
                new(PlanetaryView.Best, bestLabel) { Hit = new HitResult.ButtonHit("ViewBest") },
            ];
            // Every segment as wide as the widest label any of them takes, so a running best stack never moves the scrub track.
            var viewStyle = new Layout.ButtonGroupStyle(TransportTrackFill, ToolbarButtonBg, ink, ink, GuiTheme.Hover(ToolbarButtonBg))
            {
                Gap = pad,
                SegmentWidth = MeasureText(ViewSwitchWidestLabel, fs) + pad * 2,
            };
            // A live capture (no SER on screen, the GUI's planetary tab) has no recording to stack whole: Frames and Live only.
            var offered = state.SequencePath is null ? views[..2] : views;
            var viewSwitch = Layout.Builder.ButtonGroup(offered, state.PlanetaryView, state.ChoosePlanetaryView, viewStyle, fs);
            var controlsCtx = MeasureContext(scale: DesignScale.One);
            var segmentH = Layout.Engine.Measure(viewSwitch, new Layout.Size<float>(float.MaxValue, btnSize), controlsCtx).Height;

            // Play/pause: the mark of the action a press takes, a triangle when paused and two bars when playing, on a square button as
            // tall as the view switch's segments beside it. A Fill the bar draws, since no IconKind is a transport mark: the family keeps
            // a kind only with a painting in a cell too, and no terminal plays a capture.
            var playPause = Layout.Builder.Fill(key: PlayPauseFillKey)
                .WFixed(segmentH).HFixed(segmentH)
                .Bg(ToolbarButtonBg).BgHover(GuiTheme.Hover(ToolbarButtonBg))
                .Clickable(new HitResult.ButtonHit("PlayPause"), _ => { state.IsPlaying = !state.IsPlaying; state.NeedsRedraw = true; });

            // The row centred in the bar, as the readout is.
            var controls = Layout.Builder.HStack(playPause, viewSwitch).WithGap(pad);
            var controlsW = Layout.Engine.Measure(controls, new Layout.Size<float>(float.MaxValue, segmentH), controlsCtx).Width;
            var controlsY = r.Y + (r.Height - segmentH) / 2f;
            PaintLayout(ArrangeLayout(controls, new RectF32(btnX, controlsY, controlsW, segmentH), controlsCtx), controlsCtx, (fill, rect) =>
            {
                if (fill.Key != PlayPauseFillKey)
                {
                    return;
                }
                // The mark at the size a mark beside the segments' text takes, centred on the button.
                var side = MathF.Round(fs * Layout.Content.Icon.TextSizeRatio);
                var mark = new RectF32(MathF.Round(rect.X + (rect.Width - side) / 2f), MathF.Round(rect.Y + (rect.Height - side) / 2f), side, side);
                if (state.IsPlaying)
                {
                    var bar = MathF.Max(1f, MathF.Round(side * 0.34f));
                    FillRect(mark.X, mark.Y, bar, side, ink);
                    FillRect(mark.X + side - bar, mark.Y, bar, side, ink);
                }
                else
                {
                    // Play is drawn as the right caret's triangle, never declared as it: that kind means "next".
                    DrawLayoutIcon(Layout.IconKind.CaretRight, mark, ink);
                }
            });

            // Right-aligned readout: frame n/total, capture timestamp (if present), playback fps.
            var idx = state.FrameIndex;
            var total = state.FrameCount;
            var timestamp = string.Empty;
            if (_source is { HasTimestamps: true } src)
            {
                var ts = src.TimestampOf(idx);
                if (ts != DateTimeOffset.MinValue)
                {
                    timestamp = ts.ToString("HH:mm:ss.fff") + " UT   ";
                }
            }

            // Show the file's nominal capture rate (often hundreds of fps for planetary lucky-imaging);
            // the actual display advance is still capped by PlaybackFps. Fall back to PlaybackFps when the
            // source has no timestamps to derive a nominal rate.
            var fps = state.SourceFps ?? state.PlaybackFps;
            var readout = $"{idx + 1}/{total}   {timestamp}{fps:F0} fps";
            var readoutW = MeasureText(readout, fs);
            var readoutX = r.X + r.Width - pad - readoutW;
            DrawText(readout, readoutX, textY, fs, RGBAColor32.FromFloat(0.85f, 0.85f, 0.85f, 1f));

            // Scrub track fills the gap between the buttons and the readout.
            var trackX = btnX + controlsW + pad * 2;
            var trackRight = readoutX - pad * 2;
            var trackW = MathF.Max(0f, trackRight - trackX);
            if (trackW <= 0f)
            {
                _scrubTrackRect = default;
                return;
            }

            var frac = total > 1 ? (float)idx / (total - 1) : 0f;

            // The press/drag hit region is the full-height track band; _scrubTrackRect's X/Width drive the
            // px -> frame mapping in ScrubAt (Y/Height are only the clickable extent). The bar centres in the
            // strip; the handle spans the button-height content band.
            _scrubTrackRect = new RectF32(trackX, btnY, trackW, contentH);
            DrawTrackSlider(trackX, trackW, r.Y + r.Height / 2f, btnY, contentH, frac,
                TransportTrackFill, _scrubTrackRect, new TransportScrubHit(), TrackChrome, Scale);

            // DrawTrackSlider registers the band with a HIT and no handler, and a region with a hit and
            // no handler is silently dead under a router -- it consumes the press and runs nothing. So
            // the scrub arms from its own region, registered after (later registration wins) and needing
            // the press POSITION, which is what a scrub is: seek to where you pressed.
            RegisterClickable(_scrubTrackRect.X, _scrubTrackRect.Y, _scrubTrackRect.Width, _scrubTrackRect.Height,
                new TransportScrubHit(), onPress: press =>
                {
                    BeginScrubAt(press.X);
                    return null;
                });
        }

        /// <summary>
        /// Begins a transport scrub (press on the scrub track): pauses playback and seeks to the press X.
        /// Shared by the FitsViewer mouse-down path and the GUI viewer-tab path so both behave identically.
        /// </summary>
        public void BeginScrubAt(float px)
        {
            if (_state is not { } state)
            {
                return;
            }

            state.IsScrubbing = true;
            state.IsPlaying = false; // pause while scrubbing; resume is an explicit play
            ScrubAt(px);
        }

        // Maps a cursor X onto a frame index against the captured scrub track and requests that frame.
        // The SequencePlayer decodes it off the render thread, so dragging never blocks the UI.
        private void ScrubAt(float px)
        {
            if (_state is not { } state || _scrubTrackRect.Width <= 0f || state.FrameCount <= 1)
            {
                return;
            }

            var frac = TrackFrac(_scrubTrackRect, px);
            state.RequestedFrame = (int)MathF.Round(frac * (state.FrameCount - 1));
            state.NeedsRedraw = true;
        }

    }
}
