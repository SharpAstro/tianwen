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
        // -----------------------------------------------------------------------
        // Input handling
        // -----------------------------------------------------------------------

        public override bool HandleInput(InputEvent evt) => evt switch
        {
            InputEvent.KeyDown(var key, var modifiers) => HandleViewerKey(key, modifiers),
            InputEvent.MouseDown(var px, var py, _, _, _) => HandleViewerMouseDown(px, py),
            InputEvent.MouseMove(var px, var py) => HandleViewerMouseMove(px, py),
            InputEvent.MouseUp(_, _, _) => HandleViewerMouseUp(),
            InputEvent.Scroll(var delta, var mx, var my, _) => HandleViewerScroll(delta, mx, my),
            _ => false
        };

        private bool HandleViewerKey(InputKey key, InputModifier modifiers)
        {
            if (_state is not { } state)
            {
                return false;
            }

            // Dropdowns get first crack at keyboard so Escape/Enter/Arrows route
            // to the open overlay before falling through to global shortcuts
            // (e.g. Escape would otherwise quit via RequestExitSignal).
            if (state.ToolbarDropdown.HandleKeyDown(key))
            {
                state.NeedsRedraw = true;
                return true;
            }

            var ctrl = (modifiers & InputModifier.Ctrl) != 0;
            var shift = (modifiers & InputModifier.Shift) != 0;

            // SER transport keys take priority while a sequence is loaded -- they deliberately claim
            // Space / arrows / Home / End / Up / Down (Up/Down would otherwise step the file list) for
            // playback. Seeks route through state.RequestedFrame so decode stays off the render thread.
            if (state.IsSequence && !ctrl && HandleTransportKey(key, state))
            {
                state.NeedsRedraw = true;
                return true;
            }

            if (ctrl)
            {
                switch (key)
                {
                    case InputKey.Plus:
                        ViewerActions.ZoomIn(state);
                        return true;
                    case InputKey.Minus:
                        ViewerActions.ZoomOut(state);
                        return true;
                    case InputKey.D0:
                        ViewerActions.ZoomToFit(state);
                        return true;
                    case InputKey.D1:
                        ViewerActions.ZoomToActual(state);
                        return true;
                    case >= InputKey.D2 and <= InputKey.D9:
                        ViewerActions.ZoomTo(state, 1f / (key - InputKey.D0));
                        return true;
                }
            }

            switch (key)
            {
                case InputKey.Escape:
                    PostSignal(new RequestExitSignal());
                    return true;
                case InputKey.F11:
                    PostSignal(new ToggleFullscreenSignal());
                    return true;
                case InputKey.T:
                    ViewerActions.ToggleStretch(state);
                    return true;
                case InputKey.S:
                    state.ShowStarOverlay = !state.ShowStarOverlay;
                    return true;
                case InputKey.C:
                    if (_document is not null)
                    {
                        ViewerActions.CycleChannelView(state, _document.UnstretchedImage.ChannelCount);
                    }
                    return true;
                case InputKey.D:
                    ViewerActions.CycleDebayerAlgorithm(state);
                    return true;
                case InputKey.I:
                    state.ShowInfoPanel = !state.ShowInfoPanel;
                    return true;
                case InputKey.K:
                    // Toggle the live rolling-window stack vs the raw frame (sequence-only). The controller
                    // keeps showing the raw frame until the first master is built.
                    if (state.IsSequence)
                    {
                        state.ShowStacked = !state.ShowStacked;
                        state.WaveletDirty = true; // push the current sharpen state when (re)entering stacked
                        state.NeedsTextureUpdate = true;
                        state.NeedsRedraw = true;
                    }
                    return true;
                case InputKey.L:
                    state.ShowFileList = !state.ShowFileList;
                    return true;
                case InputKey.Plus:
                    ViewerActions.CycleStretchPreset(state);
                    return true;
                case InputKey.Minus:
                    ViewerActions.CycleStretchPreset(state, reverse: true);
                    return true;
                case InputKey.B:
                    if (shift)
                    {
                        ViewerActions.CycleCurvesMode(state);
                    }
                    else
                    {
                        ViewerActions.CycleCurvesBoost(state);
                    }
                    return true;
                case InputKey.G:
                    state.ShowGrid = !state.ShowGrid;
                    return true;
                case InputKey.O:
                    state.ShowOverlays = !state.ShowOverlays;
                    state.NeedsRedraw = true;
                    return true;
                case InputKey.H:
                    ViewerActions.CycleHdr(state);
                    return true;
                case InputKey.V:
                    if (shift)
                    {
                        state.HistogramLogScale = !state.HistogramLogScale;
                    }
                    else
                    {
                        state.ShowHistogram = !state.ShowHistogram;
                    }
                    return true;
                case InputKey.P:
                    PostSignal(new PlateSolveSignal());
                    return true;
                case InputKey.E:
                    // AI enhance — only where a SharpenPipeline is wired (the button is hidden otherwise).
                    if (EnhanceAvailable)
                    {
                        PostSignal(new EnhanceImageSignal());
                    }
                    return true;
                case InputKey.F:
                    ViewerActions.ZoomToFit(state);
                    return true;
                case InputKey.N:
                    TryToggleBackgroundNeutralization(state);
                    return true;
                case InputKey.W:
                    TryStartColorCalibration(state);
                    return true;
                case InputKey.R:
                    ViewerActions.ZoomToActual(state);
                    return true;
                case InputKey.Up:
                    if (state.SelectedFileIndex > 0)
                    {
                        ViewerActions.SelectFile(state, state.SelectedFileIndex - 1);
                    }
                    return true;
                case InputKey.Down:
                    if (state.SelectedFileIndex < state.ImageFileNames.Count - 1)
                    {
                        ViewerActions.SelectFile(state, state.SelectedFileIndex + 1);
                    }
                    return true;
                default:
                    return false;
            }
        }

        // SER transport keys (sequence-only): play/pause, step, jump to ends, speed. Step/Home/End pause
        // and request a frame; the SequencePlayer decodes it off the render thread next tick.
        private bool HandleTransportKey(InputKey key, ViewerState state)
        {
            switch (key)
            {
                case InputKey.Space:
                case InputKey.Tab:
                    state.IsPlaying = !state.IsPlaying;
                    return true;
                case InputKey.Left:
                    state.IsPlaying = false;
                    state.RequestedFrame = Math.Max(0, state.FrameIndex - 1);
                    return true;
                case InputKey.Right:
                    state.IsPlaying = false;
                    state.RequestedFrame = Math.Min(state.FrameCount - 1, state.FrameIndex + 1);
                    return true;
                case InputKey.Home:
                    state.IsPlaying = false;
                    state.RequestedFrame = 0;
                    return true;
                case InputKey.End:
                    state.IsPlaying = false;
                    state.RequestedFrame = state.FrameCount - 1;
                    return true;
                case InputKey.Up:
                    ViewerActions.CyclePlaybackSpeed(state, faster: true);
                    return true;
                case InputKey.Down:
                    ViewerActions.CyclePlaybackSpeed(state, faster: false);
                    return true;
                default:
                    return false;
            }
        }

        private void TryStartColorCalibration(ViewerState state)
        {
            if (_document?.Stars is { Count: >= 5 }
                && _document.ColorCalibration is null
                && (_document.UnstretchedImage.ChannelCount >= 3
                    || _document.UnstretchedImage.ImageMeta.SensorType is SensorType.RGGB)
                && _document.TryBeginColorCalibration())
            {
                state.StatusMessage = "Calibrating color...";
                state.NeedsRedraw = true;
                var docForTask = _document;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var db = CelestialObjectDB is { IsValueCreated: true } lazy
                            ? await lazy.WithCancellation(CancellationToken.None)
                            : null!;

                        // Try SPCC first, fall back to sky-background method.
                        // Capture-by-local (docForTask) so the task always
                        // clears the in-flight flag on the doc it started for,
                        // even if the user has navigated away in the meantime.
                        var (matched, diag) = await docForTask.ComputeSpccColorCalibrationAsync(db);
                        if (matched <= 0)
                            (matched, diag) = await docForTask.ComputeColorCalibrationAsync(db);
                        if (docForTask.ColorCalibration is { } wb)
                        {
                            state.ColorCalibrationEnabled = true;
                            if (state.StretchMode is StretchMode.Unlinked)
                            {
                                state.StretchMode = StretchMode.Linked;
                            }
                            System.Console.Error.WriteLine($"[ColorCal] {diag}");
                            state.StatusMessage = matched > 0
                                ? $"WB ({matched}★): R={wb.Item1:F3} G=1.000 B={wb.Item3:F3}"
                                : null;
                        }
                        else
                        {
                            System.Console.Error.WriteLine($"[ColorCal] FAIL: {diag}");
                            state.StatusMessage = $"Calibration failed: {diag}";
                        }
                    }
                    finally
                    {
                        docForTask.EndColorCalibration();
                        state.NeedsRedraw = true;
                    }
                });
            }
        }

        private void TryToggleBackgroundNeutralization(ViewerState state)
        {
            if (state.BackgroundNeutralizationEnabled)
            {
                _document!.BackgroundNeutralization = null;
                state.BackgroundNeutralizationEnabled = false;
                state.NeedsRedraw = true;
                return;
            }

            var gains = _document?.ComputeBackgroundNeutralization(state.BackgroundNeutralizationMethod);
            if (gains is { } g)
            {
                state.BackgroundNeutralizationEnabled = true;
                state.NeedsRedraw = true;
                System.Console.Error.WriteLine($"[BgNeut/{state.BackgroundNeutralizationMethod}] R={g.R:F3} G={g.G:F3} B={g.B:F3}");
            }
        }

        // -----------------------------------------------------------------------
        // Mouse handling
        // -----------------------------------------------------------------------

        /// <summary>
        /// Handles mouse down: hit-tests toolbar/file list, then starts panning.
        /// Returns <c>true</c> if the event was consumed by hit-test, <c>false</c>
        /// if panning was started (caller may need to handle toolbar actions via
        /// <see cref="ViewerActions.HandleToolbarAction"/>).
        /// </summary>
        private bool HandleViewerMouseDown(float px, float py)
        {
            if (_state is not { } state)
            {
                return false;
            }

            state.MouseScreenPosition = (px, py);

            // Unified hit test — OnClick handlers fire for self-contained actions (e.g. HistogramLog)
            var hit = HitTestAndDispatch(px, py);

            if (hit is HitResult.ButtonHit { Action: var action } && Enum.TryParse<ToolbarAction>(action, out var toolbarAction))
            {
                ViewerActions.HandleToolbarAction(state, _document, toolbarAction);
                if (toolbarAction is ToolbarAction.ColorCalibrate or ToolbarAction.SpccCalibrate)
                {
                    TryStartColorCalibration(state);
                }
                else if (toolbarAction is ToolbarAction.BackgroundNeutralize)
                {
                    TryToggleBackgroundNeutralization(state);
                }
                return true;
            }

            if (hit is HitResult.ListItemHit { ListId: "FileList", Index: var fileIndex })
            {
                ViewerActions.SelectFile(state, fileIndex);
                return true;
            }

            if (hit is ResizeHandleHit { Id: "FileList" })
            {
                state.IsResizingFileList = true;
                state.NeedsRedraw = true;
                return true;
            }

            if (hit is TransportScrubHit)
            {
                BeginScrubAt(px);
                return true;
            }

            if (hit is WhiteBalanceSliderHit { Channel: var wbChannel })
            {
                BeginWhiteBalanceDragAt(wbChannel, px);
                return true;
            }

            if (hit is WaveletSliderHit { Band: var wlBand })
            {
                BeginWaveletDragAt(wlBand, px);
                return true;
            }

            if (hit is not null)
            {
                return true; // OnClick already handled it (e.g. HistogramLog, PlayPause)
            }

            // No hit — start panning, but ONLY when the press is inside the image viewport. Otherwise a press
            // in the side panels / toolbar gaps / letterbox would grab the image and pan it (e.g. clicking the
            // planetary control panel must not drag the stream). Confines the drag to its viewport.
            var imgArea = _layout.ImageArea;
            var inViewport = px >= imgArea.X && px < imgArea.X + imgArea.Width
                          && py >= imgArea.Y && py < imgArea.Y + imgArea.Height;
            if (inViewport)
            {
                ViewerActions.BeginPan(state, px, py);
            }
            return false;
        }

        private bool HandleViewerMouseMove(float px, float py)
        {
            if (_state is not { } state)
            {
                return false;
            }

            state.MouseScreenPosition = (px, py);

            // Transport scrub drag: continuously seek to the dragged frame (decoded off the render thread).
            if (state.IsScrubbing)
            {
                ScrubAt(px);
                return true;
            }

            // White-balance slider drag: continuously re-derive the WB multiplier from cursor-X.
            if (state.WhiteBalanceDragChannel >= 0)
            {
                UpdateWhiteBalanceDrag(px);
                return true;
            }

            // Wavelet-layer slider drag: continuously re-derive the per-layer gain from cursor-X.
            if (state.WaveletDragBand >= 0)
            {
                UpdateWaveletDrag(px);
                return true;
            }

            // File-list resize drag: width tracks the cursor's X position in
            // DPI-independent units. Clamped by FileListWidthBase's setter.
            if (state.IsResizingFileList)
            {
                state.FileListWidthBase = px / DpiScale;
                state.NeedsRedraw = true;
                return true;
            }

            // Panning always needs a redraw (image position changes)
            if (state.IsPanning)
            {
                ViewerActions.UpdatePan(state, px, py);
                return true;
            }

            // Only redraw when cursor moves to a different image pixel
            var prevPos = state.CursorImagePosition;
            // Image-area pane rect (origin + size) from the single layout pass.
            var area = _layout.ImageArea;
            ViewerActions.UpdateCursorFromScreenPosition(_document, state, px, py, area.X, area.Y, area.Width, area.Height);
            return state.CursorImagePosition != prevPos;
        }

        private bool HandleViewerMouseUp()
        {
            if (_state is { } state)
            {
                if (state.IsScrubbing)
                {
                    state.IsScrubbing = false;
                    state.NeedsRedraw = true;
                }
                if (state.WhiteBalanceDragChannel >= 0)
                {
                    state.WhiteBalanceDragChannel = -1;
                    state.NeedsRedraw = true;
                }
                if (state.WaveletDragBand >= 0)
                {
                    state.WaveletDragBand = -1;
                    state.NeedsRedraw = true;
                }
                if (state.IsResizingFileList)
                {
                    state.IsResizingFileList = false;
                    state.NeedsRedraw = true;
                }
                ViewerActions.EndPan(state);
                return true;
            }
            return false;
        }

        private bool HandleViewerScroll(float scrollY, float mouseX, float mouseY)
        {
            if (_state is not { } state)
            {
                return false;
            }

            // Scroll file list when hovering over it (pane rect from the single arranged layout).
            var fileListPane = _layout.FileList;
            if (state.ShowFileList && mouseX >= fileListPane.X && mouseX < fileListPane.X + fileListPane.Width
                && mouseY > fileListPane.Y)
            {
                ViewerActions.ScrollFileList(state, -(int)scrollY * 3);
                return true;
            }

            // Zoom: inside the image viewport (image-area pane rect from the single layout pass)
            var area = _layout.ImageArea;
            var inImageViewport = mouseX >= area.X && mouseX < area.X + area.Width
                               && mouseY >= area.Y && mouseY < area.Y + area.Height;

            if (inImageViewport)
            {
                var zoomFactor = scrollY > 0 ? 1.15f : 1f / 1.15f;
                var oldZoom = state.Zoom;
                var newZoom = MathF.Max(0.01f, oldZoom * zoomFactor);

                var cx = mouseX - area.X - area.Width / 2f - state.PanOffset.X;
                var cy = mouseY - area.Y - area.Height / 2f - state.PanOffset.Y;

                state.PanOffset = (
                    state.PanOffset.X - cx * (newZoom / oldZoom - 1f),
                    state.PanOffset.Y - cy * (newZoom / oldZoom - 1f)
                );

                state.ZoomToFit = false;
                state.Zoom = newZoom;
                return true;
            }

            return false;
        }
    }
}
