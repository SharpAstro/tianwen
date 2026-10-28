using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
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
        // SER transport bar geometry, computed in ComputeLayout (default/empty for a still image).
        private RectF32 _transportRect;

        // The scrub track is DIR.Lib's slider over the frame indices (#1489): its press seeks to where it landed and
        // its drag goes on seeking until the button comes up, and both pause playback. Caller-owned, as every
        // slider's state is, so a tree rebuilt each frame keeps reading the position the last drag left.
        private SliderState? _scrub;

        /// <summary>The scrub track's slider state. Test seam: the one painted region carrying it is the track.</summary>
        internal SliderState Scrub => _scrub ??= new SliderState
        {
            Step = 1f,
            OnChanged = frame =>
            {
                if (_state is not { FrameCount: > 1 } state)
                {
                    return;
                }

                state.IsPlaying = false; // pause while scrubbing; resume is an explicit play
                state.RequestedFrame = (int)frame;
                state.NeedsRedraw = true;
            },
        };

        // -----------------------------------------------------------------------
        // SER transport bar (play/pause, scrub, frame + timestamp + fps readout)
        //
        // Drawn into the reserved _transportRect (the image pane was shrunk in ComputeLayout so the strip
        // never overlaps the picture). Reads only ViewerState + the cached _source accessors -- timestamp
        // lookups hit the source's managed cache, never the lazy file-tail trailer, so nothing here does
        // disk I/O. The scrub track rect is captured for the press/drag -> frame mapping in ScrubAt.
        // -----------------------------------------------------------------------

        // The view switch's fixed words, named once for the segments and for the width they are sized by.
        private const string RawLabel = "Raw";
        private const string StackLabel = "Stack";
        private const string StackPendingLabel = "Stack...";

        // Every word a segment can take: a live capture offers Raw and Stack, a SER also the Auto stack's stage words.
        private static readonly string[] LiveViewLabels = [RawLabel, StackLabel, StackPendingLabel];
        private static readonly string[] SerViewLabels = [.. LiveViewLabels, .. ViewerState.AutoSegmentLabels];

        /// <summary>The play / pause button's fill, which the bar draws its mark into.</summary>
        private const string PlayPauseFillKey = "PlayPause";

        private void RenderTransportBar(ViewerState state)
        {
            var r = _transportRect;
            if (r.Width <= 0 || r.Height <= 0 || string.IsNullOrEmpty(FontPath))
            {
                return;
            }

            FillRect(r.X, r.Y, r.Width, r.Height, TransportBg);

            var pad = PanelPadding;
            var fs = ToolbarFontSize;
            var contentH = r.Height - pad * 2;
            if (contentH <= 0f)
            {
                // Window minimized to a sliver -- nothing usable to draw; bail before any size math.
                return;
            }
            var textY = r.Y + (r.Height - fs) / 2f;
            var btnX = r.X + pad;
            var btnY = r.Y + pad;
            var btnSize = contentH;
            var ink = RGBAColor32.FromFloat(0.92f, 0.92f, 0.95f, 1f);

            // The view switch (#1314 part 2): the frames, the live rolling stack about the playhead, or the whole capture's Auto stack
            // (A4, #1391; the Best view folded into it, #817), one ButtonGroup labelled Raw / Stack / Auto: "Live" read as the camera's live
            // video as much as the live stack (the owner, 2026-10-21), so the labels went back to the RAW / STACK toggle's words. "Stack..."
            // while the live stack's first master is still computing, and the Auto stack's progress on its own segment while it runs (the view under it
            // stays on show until it ends). Arranged in device pixels at DesignScale.One, as the toolbar's run is, since the bar around it is.
            var stackLabel = state.ShowStacked && !state.ShowAuto && _source is not LiveStackPreviewSource ? StackPendingLabel : StackLabel;
            var autoLabel = state.AutoSegmentLabel;
            ReadOnlySpan<Layout.ButtonGroupOption<PlanetaryView>> views =
            [
                new(PlanetaryView.Frames, RawLabel) { Hit = new HitResult.ButtonHit("ViewFrames"), Tooltip = ViewerShortcuts.RawStack.TooltipText },
                new(PlanetaryView.Live, stackLabel) { Hit = new HitResult.ButtonHit("ViewLive"), Tooltip = ViewerShortcuts.RawStack.TooltipText },
                new(PlanetaryView.Auto, autoLabel) { Hit = new HitResult.ButtonHit("ViewAuto"), Tooltip = ViewerShortcuts.AutoView.TooltipText },
            ];
            // Every segment as wide as the widest label any of them takes, so a running Auto stack never moves the scrub track.
            // A live capture (no SER on screen, the GUI's planetary tab) has no recording to stack whole: Raw and Stack only, and its
            // segments take only those labels' width, never the Auto stack's stage words.
            var live = state.IsLiveCapture;
            var widestLabels = live ? LiveViewLabels : SerViewLabels;
            var segmentText = 0f;
            foreach (var label in widestLabels)
            {
                segmentText = MathF.Max(segmentText, MeasureText(label, fs));
            }
            var viewStyle = PlanetaryChoiceStyle with
            {
                Gap = pad,
                SegmentWidth = segmentText + pad * 2,
            };
            var offered = live ? views[..2] : views;
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

            // The row centred in the bar, as the readout is. A live capture has no play/pause (#1716): its newest frame is on show.
            var controls = live ? viewSwitch : Layout.Builder.HStack(playPause, viewSwitch).WithGap(pad);
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

            // Scrub track fills the gap between the buttons and the readout; none for a live capture, which has nothing to scrub (#1716).
            if (live)
            {
                return;
            }
            var trackX = btnX + controlsW + pad * 2;
            var trackRight = readoutX - pad * 2;
            var trackW = MathF.Max(0f, trackRight - trackX);
            if (trackW <= 0f)
            {
                return;
            }

            // The full-height band between the controls and the readout is the slider's: the bar centres in the
            // strip and the handle spans the button-height content band, at the widget's own scale as the track
            // always drew. The frame on show is its value, so a seek's handle lands when the frame does.
            var scrub = Scrub;
            scrub.Min = 0f;
            scrub.Max = MathF.Max(0f, total - 1);
            scrub.Value = Math.Clamp(idx, 0, Math.Max(0, total - 1));
            RenderLayout(Layout.Builder.Slider(scrub, TransportTrackFill, TrackChrome),
                new RectF32(trackX, btnY, trackW, contentH));
        }
    }
}
