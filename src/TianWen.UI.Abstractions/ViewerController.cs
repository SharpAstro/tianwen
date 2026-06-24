using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TianWen.Lib.Astrometry.PlateSolve;
using TianWen.Lib.Imaging;

namespace TianWen.UI.Abstractions;

/// <summary>
/// Orchestrates file loading, star detection, and DI-dependent toolbar actions
/// for the FITS viewer. Owns document lifecycle and background task management.
/// Does not depend on SDL/Vulkan — display concerns stay in Program.cs.
/// </summary>
public sealed class ViewerController(
    ViewerState state,
    IDocumentCache documentCache,
    IFileDialogHelper fileDialog,
    IPlateSolverFactory plateSolverFactory,
    ILogger<ViewerController> logger)
{
    private Task? _loadTask;
    private Task? _backgroundTask;
    private Task? _starDetectionTask;
    private CancellationTokenSource? _starDetectionCts;

    // Per-load cancellation: a load in flight is cancelled when the user navigates to a different file,
    // so a slow open (e.g. a large SER off a spinning disk) doesn't pin the load slot and stall the
    // switch. _loadingPath is the file the in-flight _loadTask is opening.
    private CancellationTokenSource? _loadCts;
    private string? _loadingPath;

    // Previous sources (e.g. a SerPreviewSource / LiveStackPreviewSource holding a memory-mapped file)
    // replaced by a new load. Disposed in ReleaseCompletedTasks (post-frame, UI thread) once no longer
    // busy, so no in-flight render/decode/stack still reads them.
    private readonly List<IDisposable> _pendingDispose = new();

    // Sequence playback (SER). The player is touched ONLY on the render thread (TickPlayback /
    // IsPlaybackActive, both called from OnRender / CheckNeedsRedraw) -- it rebinds itself when Source
    // changes, so the background load thread never races it. The clock is monotonic elapsed-seconds for
    // wall-clock frame advance (animation timing, not a display clock).
    private readonly SequencePlayer _player = new SequencePlayer();
    private readonly System.Diagnostics.Stopwatch _playbackClock = System.Diagnostics.Stopwatch.StartNew();
    private IPreviewSource? _playerBoundSource;

    /// <summary>
    /// The currently loaded document, or null when the active source is not a document (e.g. a SER
    /// sequence). Still-only features (plate solve, star detection) operate on this.
    /// </summary>
    public AstroImageDocument? Document { get; private set; }

    // The raw source: the document (still) or the SerPreviewSource (SER). Always the playback driver -- the
    // SequencePlayer advances THIS even while the stacked view is shown, so the playhead keeps moving and
    // the live stack can follow it.
    private IPreviewSource? _rawSource;

    // The live rolling-window stack over the same SER (null for stills / non-stacked). Built lazily; shown
    // only once it has produced its first master (HasMaster).
    private LiveStackPreviewSource? _liveSource;

    /// <summary>
    /// The source the renderer previews. The raw frame normally; the live rolling-window stack when
    /// <see cref="ViewerState.ShowStacked"/> is set AND that stack has a master to show (otherwise the raw
    /// frame keeps showing while the first stack computes). For a still image this is the same object as
    /// <see cref="Document"/>; for a SER the raw source is a sequence source and <see cref="Document"/> is null.
    /// </summary>
    public IPreviewSource? Source => state.ShowStacked && _liveSource is { HasMaster: true } ? _liveSource : _rawSource;

    /// <summary>
    /// Fires with the loaded filename after a document is successfully opened.
    /// Subscribers must be thread-safe (fires on a background thread).
    /// </summary>
    public event Action<string>? FileLoaded;

    /// <summary>
    /// True when a file-load task is in flight.
    /// Used by the render loop to gate <c>CheckNeedsRedraw</c>.
    /// </summary>
    public bool IsLoadPending => _loadTask is { IsCompleted: false };

    /// <summary>
    /// Checks <see cref="ViewerState.RequestedFilePath"/> and, if set, starts loading
    /// on a background thread. Only one load runs at a time.
    /// Must be called every frame from OnRender.
    /// </summary>
    public void HandleFileRequest(CancellationToken appToken)
    {
        if (state.RequestedFilePath is not { } requestedPath)
        {
            return;
        }

        // A load is already running. If the user has since picked a different file, cancel the stale
        // load so it abandons its (now-pointless) open + decode + stats and frees the slot promptly;
        // the newest RequestedFilePath is then started on a later frame (latest-wins). A repeat request
        // for the same in-flight file is left alone to finish.
        if (_loadTask is { IsCompleted: false })
        {
            // Capture the in-flight load's CTS so we cancel exactly that instance (not whatever _loadCts
            // may later point at). The render thread sets _loadTask + _loadCts together, so they pair up.
            if (_loadCts is { } loadCts
                && !string.Equals(requestedPath, _loadingPath, StringComparison.OrdinalIgnoreCase))
            {
                loadCts.Cancel();
            }
            return;
        }

        state.RequestedFilePath = null;
        _loadingPath = requestedPath;
        state.StatusMessage = $"Loading {Path.GetFileName(requestedPath)}...";

        // Cancel any in-progress star detection from previous image
        _starDetectionCts?.Cancel();
        _starDetectionCts?.Dispose();
        _starDetectionCts = null;

        // Fresh per-load cancellation token, linked to the app token. Cancelled if a later request
        // supersedes this load (see the in-flight branch above).
        _loadCts?.Dispose();
        _loadCts = CancellationTokenSource.CreateLinkedTokenSource(appToken);
        var loadToken = _loadCts.Token;

        var debayerAlgorithm = state.DebayerAlgorithm;
        var isSer = string.Equals(Path.GetExtension(requestedPath), ".ser", StringComparison.OrdinalIgnoreCase);

        _loadTask = Task.Run(async () =>
        {
            var previousRaw = _rawSource;
            var previousLive = _liveSource;

            // SER planetary video: a multi-frame sequence handled by SerPreviewSource, NOT the document
            // file loader. Stats are computed once from frame 0; the viewer auto-switches to playback mode.
            if (isSer)
            {
                try
                {
                    var serSource = await SerPreviewSource.OpenAsync(requestedPath, loadToken);
                    // The live rolling-window stack over the SAME file (its own SER reader). Constructed
                    // here, off the render thread; it stacks lazily once the user toggles to the stacked
                    // view. A failure to open it is non-fatal -- raw playback still works.
                    LiveStackPreviewSource? liveSource = null;
                    try { liveSource = LiveStackPreviewSource.Open(requestedPath); }
                    catch (Exception ex) { logger.LogWarning(ex, "Failed to open live-stack source for {FilePath}", requestedPath); }

                    if (loadToken.IsCancellationRequested)
                    {
                        // Superseded between OpenAsync returning and here -- drop the just-opened readers
                        // and leave the previous Source untouched (the newer request loads next frame).
                        serSource.Dispose();
                        liveSource?.Dispose();
                        return;
                    }
                    var wasSequence = state.IsSequence;
                    Document = null;
                    _rawSource = serSource;
                    _liveSource = liveSource;
                    state.ShowStacked = false; // a fresh file starts on the raw view (its stack has no master yet)
                    state.IsSequence = true;
                    state.FrameCount = serSource.FrameCount;
                    state.FrameIndex = 0;
                    state.IsPlaying = serSource.FrameCount > 1;
                    // Default to a comfortable review rate, NOT the file's native capture rate: planetary
                    // lucky-imaging runs at hundreds of fps (unviewable as playback, and it would race
                    // through the whole memory-mapped file, ballooning the working set). Cap at 30; the
                    // user raises it with Up / the transport. Native fps is still available via the file.
                    state.PlaybackFps = serSource.FramesPerSecond is { } fps and > 0 ? Math.Clamp((float)fps, 1f, 30f) : 30f;
                    // Nominal capture rate, shown in the transport as info (often hundreds of fps for
                    // planetary lucky-imaging -- unviewable, hence the display cap above).
                    state.SourceFps = serSource.FramesPerSecond is { } srcFps and > 0 ? (float)srcFps : null;
                    // Planetary SER is a bright disk on a near-black sky. The deep-sky MTF auto-stretch
                    // (Unlinked/Linked/Luma map the median to ~0.25) over-amplifies that dark background
                    // into colour speckle and blows the disk to white -- stable on some frames, runaway
                    // on others (exactly the "first frame ok, others broken" report). Match the standalone
                    // SER viewer instead: show the linear [0,1] frame (StretchMode.None); FillUnitFloat has
                    // already normalised the raw samples by the SER bit depth. Only reset the mode when
                    // entering sequence mode, so the user's pick is preserved while scrubbing SER->SER.
                    if (!wasSequence)
                    {
                        state.StretchMode = StretchMode.None;
                    }
                    state.HistogramLogScale = state.StretchMode is StretchMode.None;
                    state.NeedsTextureUpdate = true;
                    state.CursorImagePosition = null;
                    state.CursorPixelInfo = null;
                    state.StatusMessage = null;
                    FileLoaded?.Invoke(Path.GetFileName(requestedPath));
                }
                catch (OperationCanceledException) { logger.LogDebug("SER open cancelled (superseded or shutdown)"); }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to open SER file: {FilePath}", requestedPath);
                    state.StatusMessage = $"Failed to open: {Path.GetFileName(requestedPath)}";
                }

                StashForDispose(previousRaw, previousLive);
                return;
            }

            AstroImageDocument? newDoc;
            try
            {
                newDoc = await documentCache.GetOrLoadAsync(requestedPath, debayerAlgorithm, loadToken);
            }
            catch (OperationCanceledException)
            {
                logger.LogDebug("Image load cancelled (superseded or shutdown): {FilePath}", requestedPath);
                return;
            }

            if (loadToken.IsCancellationRequested)
            {
                // Superseded after the load completed; don't clobber the newer selection.
                return;
            }

            if (newDoc is not null)
            {
                Document = newDoc;
                _rawSource = newDoc;
                _liveSource = null;
                state.ShowStacked = false; // stacking is a sequence-only mode
                state.IsSequence = false;
                state.IsPlaying = false;
                state.FrameCount = 1;
                state.FrameIndex = 0;
                state.SourceFps = null;
                StashForDispose(previousRaw, previousLive);
                state.NeedsTextureUpdate = true;
                state.CursorImagePosition = null;
                state.CursorPixelInfo = null;
                state.StatusMessage = null;

                // Disable stretch for pre-stretched images, re-enable for linear images
                if (newDoc.IsPreStretched)
                {
                    state.StretchMode = StretchMode.None;
                }
                else if (state.StretchMode is StretchMode.Linked or StretchMode.Luma
                    && newDoc.UnstretchedImage.ChannelCount < 3)
                {
                    // Switch from color to mono — Linked/Luma need 3+ channels
                    state.StretchMode = StretchMode.Unlinked;
                }
                else if (state.StretchMode is StretchMode.None && !newDoc.IsPreStretched)
                {
                    state.StretchMode = StretchMode.Unlinked;
                }
                state.HistogramLogScale = state.StretchMode is StretchMode.None;

                if (newDoc.Wcs is { } wcs)
                {
                    logger.LogInformation("WCS: HasCD={HasCDMatrix}, Approx={IsApproximate}, Scale={PixelScale:F2}\"/px, RA={CenterRA:F4}h, Dec={CenterDec:F4}°",
                        wcs.HasCDMatrix, wcs.IsApproximate, wcs.PixelScaleArcsec, wcs.CenterRA, wcs.CenterDec);
                }

                FileLoaded?.Invoke(Path.GetFileName(requestedPath));

                // Kick off star detection in the background
                var sdCts = CancellationTokenSource.CreateLinkedTokenSource(appToken);
                _starDetectionCts = sdCts;
                _starDetectionTask = Task.Run(async () =>
                {
                    try
                    {
                        await newDoc.DetectStarsAsync(sdCts.Token);
                        logger.LogInformation("Detected {StarCount} stars in {Duration:F1}s (HFR={HFR:F2}, FWHM={FWHM:F2})",
                            newDoc.Stars?.Count ?? 0, newDoc.StarDetectionDuration.TotalSeconds, newDoc.AverageHFR, newDoc.AverageFWHM);
                        state.NeedsRedraw = true;
                    }
                    catch (OperationCanceledException) { logger.LogDebug("Star detection cancelled"); }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Star detection failed");
                        newDoc.Stars = StarList.Empty;
                        state.StatusMessage = "Star detection failed";
                        state.NeedsRedraw = true;
                    }
                }, sdCts.Token);
            }
            else
            {
                logger.LogWarning("Failed to open image file: {FilePath}", requestedPath);
                state.StatusMessage = $"Failed to open: {Path.GetFileName(requestedPath)}";
            }
        }, appToken);
    }

    /// <summary>
    /// Handles toolbar actions that require DI (Open, PlateSolve).
    /// Call after <see cref="ViewerActions.HandleToolbarAction"/> returns <c>false</c>.
    /// </summary>
    public void HandleToolbarAction(ToolbarAction action, bool reverse, CancellationToken appToken)
    {
        switch (action)
        {
            case ToolbarAction.Open:
                state.StatusMessage = "Opening file dialog...";
                _backgroundTask = Task.Run(async () =>
                {
                    var filters = AstroImageDocument.FileDialogFilters
                        .ToDictionary(f => f.Name, f => (IReadOnlyList<string>)f.Extensions);
                    var picked = await fileDialog.PickAsync(filters, combinedFilterName: "All supported images", title: "Open image").ConfigureAwait(false);
                    state.StatusMessage = null;
                    if (picked is null)
                    {
                        return;
                    }

                    if (Directory.Exists(picked))
                    {
                        ViewerActions.ScanFolder(state, picked);
                        if (state.ImageFileNames.Count > 0)
                        {
                            ViewerActions.SelectFile(state, 0);
                        }
                    }
                    else if (File.Exists(picked))
                    {
                        var dir = Path.GetDirectoryName(picked);
                        if (dir is not null)
                        {
                            ViewerActions.ScanFolder(state, dir, Path.GetFileName(picked));
                        }
                        state.RequestedFilePath = picked;
                    }
                }, appToken);
                break;

            case ToolbarAction.PlateSolve:
                if (Document is not null && !state.IsPlateSolving && !Document.IsPlateSolved)
                {
                    _backgroundTask = ViewerActions.PlateSolveAsync(Document, state, plateSolverFactory, appToken);
                }
                break;
        }
    }

    /// <summary>
    /// Advances SER sequence playback by one tick. Call from the render loop's <c>CheckNeedsRedraw</c>
    /// (it runs every loop iteration, including idle WaitEvent polls -- which is how playback stays paced
    /// without busy-spinning). All frame decode happens off the render thread; this only polls for a
    /// finished decode and kicks the next one ahead. Returns true when the loop should render this tick
    /// (a frame was published, or a seek is still resolving). Steady playback between frames returns
    /// false so the loop idles and the GPU/disk go quiet. No-op (false) for a still image.
    /// </summary>
    public bool TickPlayback()
    {
        // The RAW source is always the playback driver -- even while the stacked view is shown -- so the
        // playhead keeps advancing and the live stack can follow it. We never bind the player to the live
        // source (it is not seekable; it follows).
        var raw = _rawSource;
        if (!ReferenceEquals(raw, _playerBoundSource))
        {
            // Raw source changed (new SER opened, or cleared) -- reset playback timing. Done here on the
            // render thread, never on the background load thread, so the player is single-threaded.
            _player.Reset();
            _playerBoundSource = raw;
        }

        if (raw is not ISequencePlaybackSource seq || !state.IsSequence)
        {
            return false;
        }

        var rawPublished = _player.Tick(seq, state, _playbackClock.Elapsed.TotalSeconds);

        // Live rolling-window stack: consume any finished master first (so the just-completed result is
        // published before we kick the next one), then follow the current playhead. Only runs while the
        // stacked view is requested -- no CPU spent stacking when showing the raw frame.
        var masterPublished = false;
        if (_liveSource is { } live && state.ShowStacked)
        {
            // Push changed wavelet-sharpen params (null = off); the source re-sharpens the cached master
            // off-thread without re-stacking. Cheap no-op compare via the dirty flag, so this runs per tick.
            if (state.WaveletDirty)
            {
                live.SetSharpen(state.WaveletSharpenEnabled ? BuildWaveletOptions(state) : null);
                state.WaveletDirty = false;
            }
            masterPublished = live.TryPublishMaster();
            live.RequestFollow(state.FrameIndex);
        }

        // Upload whichever source is actually on screen. A raw frame advance only re-uploads when the raw
        // frame is shown; a new master always becomes the displayed image (it only publishes while stacked).
        var showingStacked = state.ShowStacked && _liveSource is { HasMaster: true };
        if (rawPublished && !showingStacked)
        {
            state.NeedsTextureUpdate = true;
        }
        if (masterPublished)
        {
            state.NeedsTextureUpdate = true;
        }

        // A raw advance still warrants a redraw while stacked (the transport playhead moved), just not a
        // texture re-upload. Also keep the loop awake while a stack/sharpen is in flight: its result is
        // published by TryPublishMaster on a later tick, but the render loop only ticks on input or a
        // timeout -- without this, a re-sharpen kicked by a slider drag would not be displayed until the
        // next mouse event (the "doesn't live adjust while paused" symptom). IsBusy self-clears on publish,
        // so this briefly spins for the ~task duration, then the loop idles again.
        return rawPublished || masterPublished || _player.SeekPending
            || (state.ShowStacked && _liveSource is { IsBusy: true });
    }

    /// <summary>
    /// Called from OnPostFrame to release completed task closures so captured documents can be GC'd.
    /// </summary>
    public void ReleaseCompletedTasks()
    {
        if (_loadTask is { IsCompleted: true }) _loadTask = null;
        if (_starDetectionTask is { IsCompleted: true }) _starDetectionTask = null;
        if (_backgroundTask is { IsCompleted: true }) _backgroundTask = null;

        // Dispose sources replaced by a newer load, post-frame, so no render still references them. Never
        // release a memory-mapped SER reader while a background decode (SerPreviewSource) or window stack
        // (LiveStackPreviewSource) is still reading it (use-after-free); leave those for a later frame --
        // their in-flight work runs to completion (sub-ms) and clears almost at once.
        for (var i = _pendingDispose.Count - 1; i >= 0; i--)
        {
            var stale = _pendingDispose[i];
            if (StillInUse(stale))
            {
                continue;
            }

            stale.Dispose();
            _pendingDispose.RemoveAt(i);
        }
    }

    private static bool StillInUse(IDisposable d)
        => d is ISequencePlaybackSource { IsDecoding: true } or LiveStackPreviewSource { IsBusy: true };

    // Builds the live-stack wavelet options from the panel sliders. Reuses the validated planetary fine-scale
    // denoise (the single source) so amplified gains don't pull up limb / sensor grain.
    private static WaveletSharpenOptions BuildWaveletOptions(ViewerState state)
        => new WaveletSharpenOptions
        {
            Gains = state.WaveletGains,
            DenoiseThresholds = WaveletSharpenOptions.PlanetaryDefault.DenoiseThresholds,
        };

    // Queues replaced sources for post-frame disposal (only disposable sources -- SerPreviewSource /
    // LiveStackPreviewSource hold a memory-mapped file; an AstroImageDocument has nothing unmanaged and is
    // left to the GC). Skips anything still wired as the current raw / live source.
    private void StashForDispose(params IPreviewSource?[] previous)
    {
        foreach (var p in previous)
        {
            if (p is IDisposable d && !ReferenceEquals(p, _rawSource) && !ReferenceEquals(p, _liveSource))
            {
                _pendingDispose.Add(d);
            }
        }
    }

    /// <summary>
    /// Awaits all in-flight tasks and disposes the star detection CTS. Call at app shutdown.
    /// </summary>
    public async Task ShutdownAsync()
    {
        _starDetectionCts?.Cancel();
        _starDetectionCts?.Dispose();

        // Cancel an in-flight load so it bails (and disposes its own half-opened reader) before we tear
        // down. Await it first, THEN dispose Source -- otherwise a load completing mid-shutdown could
        // assign a fresh Source after we already disposed the previous one, leaking the new reader.
        _loadCts?.Cancel();

        if (_loadTask is not null)
        {
            try { await _loadTask; } catch (OperationCanceledException) { logger.LogDebug("Load task cancelled during shutdown"); }
        }
        if (_backgroundTask is not null)
        {
            try { await _backgroundTask; } catch (OperationCanceledException) { logger.LogDebug("Background task cancelled during shutdown"); }
        }

        _loadCts?.Dispose();
        foreach (var d in _pendingDispose)
        {
            d.Dispose();
        }
        _pendingDispose.Clear();
        (_rawSource as IDisposable)?.Dispose();
        _liveSource?.Dispose();
    }
}
