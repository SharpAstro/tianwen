using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DIR.Lib;
using Microsoft.Extensions.Logging;
using TianWen.Lib.Astrometry.PlateSolve;
using TianWen.Lib.Devices;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Enhancement;

namespace TianWen.UI.Abstractions;

/// <summary>
/// Orchestrates file loading, star detection, and DI-dependent toolbar actions
/// for the FITS viewer. Owns document lifecycle and background task management.
/// Does not depend on SDL/Vulkan: display concerns stay in Program.cs.
/// </summary>
public sealed class ViewerController(
    ViewerState state,
    IDocumentCache documentCache,
    IFileDialogHelper fileDialog,
    IPlateSolverFactory plateSolverFactory,
    ITimeProvider timeProvider,
    BackgroundTaskTracker tracker,
    ILogger<ViewerController> logger)
{
    private Task? _loadTask;
    private Task? _starDetectionTask;
    private Task<AstroImageDocument?>? _enhanceTask;
    private CancellationTokenSource? _starDetectionCts;

    // Per-RUN enhance cancellation, so pressing the button while it computes stops THAT run. It used
    // to be started on the app token alone, which meant the only way to stop an enhance was to quit.
    private CancellationTokenSource? _enhanceCts;

    // The pre-enhance document, kept only when EnhanceRevertPolicy says it is worth the memory. Null
    // while Enhance is off, and null when the policy chose to re-read the file on revert instead.
    private AstroImageDocument? _preEnhanceDocument;

    // The file to re-open when reverting without a retained document.
    private string? _preEnhancePath;

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

    /// <summary>
    /// The AI sharpen pipeline used by the Enhance action, or null when no AI services are wired
    /// (e.g. a minimal viewer host). Set by the host after resolving it from DI. When null the
    /// Enhance toolbar button is hidden (the renderer's <c>EnhanceAvailable</c> flag) and the
    /// <see cref="ToolbarAction.Enhance"/> dispatch is a no-op.
    /// </summary>
    public SharpenPipeline? EnhancePipeline { get; set; }

    /// <summary>True while an AI enhance pass is in flight; used by the render loop's redraw gate.</summary>
    public bool IsEnhancePending => _enhanceTask is { IsCompleted: false };

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
        // A new file is not an enhanced view of the old one. Done here rather than on the completion
        // path so a load that is later superseded still clears the toggle it invalidated.
        ForgetEnhanceState();
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
                    var serSource = await SerPreviewSource.OpenAsync(requestedPath, timeProvider, loadToken);
                    // The live rolling-window stack over the SAME file (its own SER reader). Constructed
                    // here, off the render thread; it stacks lazily once the user toggles to the stacked
                    // view. A failure to open it is non-fatal -- raw playback still works.
                    LiveStackPreviewSource? liveSource = null;
                    try { liveSource = LiveStackPreviewSource.Open(requestedPath, timeProvider); }
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
                    // Stamped at ADOPTION, never at request: a superseded or failed load must not
                    // invalidate a comparison that is still valid for what is on screen.
                    state.NotifySourceReplaced();
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

            AstroImageDocument? newDoc = null;
            // Kept so the not-opened branch below can say WHY rather than just that it did not open.
            Exception? loadError = null;
            try
            {
                newDoc = await documentCache.GetOrLoadAsync(requestedPath, debayerAlgorithm, loadToken);
            }
            catch (OperationCanceledException)
            {
                logger.LogDebug("Image load cancelled (superseded or shutdown): {FilePath}", requestedPath);
                return;
            }
            catch (Exception ex)
            {
                // This ran with only the cancellation filter, and a loader that threw instead of
                // returning false therefore VANISHED: the lambda is inside a Task.Run, so the
                // exception became an unobserved task fault. No document, no log, no status message --
                // clicking the file simply did nothing, which is harder to diagnose than a crash.
                // Recorded rather than rethrown, so the branch that already reports a failed open
                // handles this too and gets the reason to show.
                loadError = ex;
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
                state.NotifySourceReplaced();
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
                    // Switch from color to mono: Linked/Luma need 3+ channels
                    state.StretchMode = StretchMode.Unlinked;
                }
                else if (state.StretchMode is StretchMode.None && !newDoc.IsPreStretched)
                {
                    state.StretchMode = ViewerActions.DefaultStretchMode;
                }
                state.HistogramLogScale = state.StretchMode is StretchMode.None;

                if (newDoc.Wcs is { } wcs)
                {
                    logger.LogInformation("WCS: HasCD={HasCDMatrix}, Approx={IsApproximate}, Scale={PixelScale:F2}\"/px, RA={CenterRA:F4}h, Dec={CenterDec:F4}°",
                        wcs.HasCDMatrix, wcs.IsApproximate, wcs.PixelScaleArcsec, wcs.CenterRA, wcs.CenterDec);
                }

                FileLoaded?.Invoke(Path.GetFileName(requestedPath));

                // Kick off star detection in the background
                StartStarDetection(newDoc, appToken);
            }
            else if (loadError is not null)
            {
                logger.LogWarning(loadError, "Failed to open image file: {FilePath}", requestedPath);
                // Through StatusText, so a parser's exception message cannot put raw bytes or a
                // multi-line dump in the status bar.
                state.StatusMessage =
                    $"Failed to open {Path.GetFileName(requestedPath)}: {StatusText.FromException(loadError)}";
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
                tracker.RunGuarded(async _ =>
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
                },
                appToken,
                logger,
                "Open file dialog",
                onError: ex => state.StatusMessage = $"Open failed: {StatusText.FromException(ex)}",
                onFinally: () => state.NeedsRedraw = true);
                break;

            // Save the raster AS DISPLAYED. The uniforms are recomputed here from the same state the
            // renderer hands its shader rather than reached for across the renderer:
            // ComputeStretchUniforms is documented as the single producer, so the same inputs give the
            // same answer, and the controller stays free of a renderer reference.
            case ToolbarAction.Save:
                if (Document is not { } saveDoc)
                {
                    state.StatusMessage = "Nothing to save";
                    break;
                }

                state.StatusMessage = "Saving...";
                tracker.RunGuarded(async token =>
                {
                    // PNG first, so it is the dialog's default and the extension appended to a name
                    // typed without one. It is also the only lossless option here.
                    var filters = new Dictionary<string, IReadOnlyList<string>>
                    {
                        ["PNG (16-bit)"] = [".png"],
                        ["JPEG"] = [".jpg", ".jpeg"],
                        ["TIFF (32-bit float)"] = [".tif", ".tiff"],
                    };

                    var stem = Path.GetFileNameWithoutExtension(saveDoc.FilePath);
                    var suggested = (stem.Length > 0 ? stem : "image") + ".png";
                    var target = await fileDialog.SaveAsync(filters, suggested, "Save image as displayed", token).ConfigureAwait(false);
                    if (target is null)
                    {
                        state.StatusMessage = null;
                        return;
                    }

                    var image = saveDoc.UnstretchedImage;
                    var uniforms = saveDoc.ComputeStretchUniforms(
                        state.StretchMode, state.StretchParameters,
                        bgNeutralizationStrength: state.BackgroundNeutralizationStrength,
                        manualWhiteBalance: state.ManualWhiteBalance,
                        applyColorCalibration: state.ColorCalibrationEnabled);

                    // The boost pivots on the POST-stretch background, exactly as the shader's
                    // curvesMidpoint does; passing the default 0.25 instead would move the curve.
                    var background = uniforms.ComputePostStretchBackground(
                        saveDoc.PerChannelBackground, saveDoc.LumaBackground);

                    await DisplayRasterExport.WriteAsync(
                        image, target,
                        DisplayRasterExport.FromExtension(target) ?? DisplayRasterFormat.Png16,
                        uniforms,
                        state.CurvesBoost, state.CurvesMode, state.CurveData, background,
                        state.HdrAmount, state.HdrKnee,
                        displayedChannel: state.ChannelView.DisplayedSourceChannel(image.ChannelCount),
                        debayerAlgorithm: state.DebayerAlgorithm,
                        cancellationToken: token).ConfigureAwait(false);

                    state.StatusMessage = $"Saved {Path.GetFileName(target)}";
                },
                appToken,
                logger,
                "Save display raster",
                onError: ex => state.StatusMessage = $"Save failed: {StatusText.FromException(ex)}",
                onFinally: () => state.NeedsRedraw = true);
                break;

            case ToolbarAction.PlateSolve:
                if (Document is { } solveDoc && !state.IsPlateSolving && !solveDoc.IsPlateSolved)
                {
                    // Claim the slot HERE, on the render thread, before the work leaves it. The flag is
                    // what stops a second press starting a second solve, and once the body runs on the
                    // pool the gap between testing it and setting it is wide enough for a double-click.
                    state.IsPlateSolving = true;
                    state.StatusMessage = "Plate solving...";

                    // Task.Run, not a bare call. HandleToolbarAction runs ON the render thread, and a
                    // solve is mostly synchronous CPU work: the catalog init alone (Tycho-2 bulk decode)
                    // is a one-off several hundred ms, and downsample / star detection / SIP fit all run
                    // inline -- only the two proximity-matching branches are on the pool. Awaiting that
                    // from the render thread froze the UI for the whole solve. Measured on the Bubble
                    // master: 4.8 s between the factory naming the file and it trying the first solver,
                    // then ~1.4 s solving.
                    //
                    // Which is more than sluggish. Frames stop being submitted while it blocks, and the
                    // renderer reads that as a GPU that has stopped answering -- the fence wait times
                    // out, recovery escalates, and a wedge is declared over work the GPU was never
                    // given. A crash was observed immediately after exactly this solve.
                    tracker.RunGuarded(
                        ct => ViewerActions.PlateSolveAsync(solveDoc, state, plateSolverFactory, logger, ct),
                        appToken,
                        logger,
                        "Plate solve",
                        onError: ex => state.StatusMessage = $"Plate solve error: {StatusText.FromException(ex)}",
                        onFinally: () =>
                        {
                            state.IsPlateSolving = false;
                            state.NeedsRedraw = true;
                        });
                }
                break;

            case ToolbarAction.Enhance:
                if (reverse)
                {
                    // Right-click cycles the preferred backend (Auto -> RC -> SAS -> N2N); no enhance
                    // is kicked. The button label reflects the new pick.
                    state.PreferredEnhanceBackend = state.PreferredEnhanceBackend switch
                    {
                        EnhanceBackend.Auto => EnhanceBackend.ForceRcAstro,
                        EnhanceBackend.ForceRcAstro => EnhanceBackend.ForceSas,
                        EnhanceBackend.ForceSas => EnhanceBackend.N2n,
                        _ => EnhanceBackend.Auto,
                    };
                    state.StatusMessage = $"Enhance backend: {state.PreferredEnhanceBackend}";
                    state.NeedsRedraw = true;
                    break;
                }
                // Pressing while a run is in flight CANCELS it. The pipeline threads the token all
                // the way into RcAstroCli, which kills the child process tree, so this stops the work
                // rather than merely abandoning it.
                if (state.IsEnhancing || _enhanceTask is not null)
                {
                    _enhanceCts?.Cancel();
                    state.StatusMessage = "Cancelling enhance...";
                    state.NeedsRedraw = true;
                    break;
                }

                // Pressing while enhanced turns it OFF, which is what makes a second press incapable
                // of stacking another pass onto the first one's output.
                if (state.IsEnhanced)
                {
                    RevertEnhance();
                    break;
                }

                if (Document is { } enhanceDoc && EnhancePipeline is { } pipeline
                    && _enhanceTask is null)
                {
                    state.IsEnhancing = true;
                    state.EnhanceProgressPct = 0f;
                    state.StatusMessage = "Enhancing...";
                    state.NeedsRedraw = true;
                    // Snapshot the backend preference into immutable options per click (no global).
                    var options = new EnhanceOptions(state.PreferredEnhanceBackend);
                    var debayer = state.DebayerAlgorithm;
                    // Off the render thread: ProcessAsync's sync prefix (sanitise + noise estimate) and the
                    // AI work all run on the pool, so the render loop never hitches (important on integrated
                    // GPUs where the AI work itself contends for the GPU -- we do NOT spin-render meanwhile).
                    // The ContinueWith wakes the loop once on completion so TryApplyPendingEnhance applies it.
                    // Decide the revert route BEFORE the run, while the source document is
                    // indisputably the original: afterwards Document is the enhanced one and the
                    // question cannot be asked again.
                    _preEnhancePath = enhanceDoc.FilePath;
                    var revert = EnhanceRevertPolicy.Decide(
                        enhanceDoc.UnstretchedImage, canReload: !string.IsNullOrEmpty(enhanceDoc.FilePath));
                    _preEnhanceDocument = revert is EnhanceRevert.Retained ? enhanceDoc : null;
                    logger.LogDebug("Enhance: revert route {Revert} ({Bytes} MB source)",
                        revert, EnhanceRevertPolicy.FootprintBytes(enhanceDoc.UnstretchedImage) / (1024 * 1024));

                    _enhanceCts?.Dispose();
                    _enhanceCts = CancellationTokenSource.CreateLinkedTokenSource(appToken);
                    var enhanceToken = _enhanceCts.Token;
                    _enhanceTask = Task.Run(
                        () => EnhanceActions.EnhanceAsync(enhanceDoc, state, pipeline, options, debayer, enhanceToken),
                        enhanceToken);
                    _ = _enhanceTask.ContinueWith(_ => state.NeedsRedraw = true, TaskScheduler.Default);
                }
                break;
        }
    }

    /// <summary>
    /// Turns Enhance off, restoring the pre-enhance view by whichever route
    /// <see cref="EnhanceRevertPolicy"/> chose when the run started.
    /// </summary>
    private void RevertEnhance()
    {
        state.IsEnhanced = false;
        state.EnhanceProgressPct = 0f;

        if (_preEnhanceDocument is { } retained)
        {
            // A reference swap: nothing is copied, and the document was never mutated by the run
            // (the pipeline consumes its own outputs, never the caller's buffers).
            _preEnhanceDocument = null;
            Document = retained;
            _rawSource = retained;
            _liveSource = null;
            state.IsSequence = false;
            state.NotifySourceReplaced();
            state.NeedsTextureUpdate = true;
            // Deliberately no status message. The upload path clears StatusMessage once the pixels
            // are on screen (that is how "Loading ..." is dismissed), so anything set here is wiped
            // by the very frame that draws the reverted image -- a line that reads as feedback and
            // delivers none. The swap is instant and visible, and the button's highlight going out
            // is the state, so there is nothing left to caption. The Reload branch below DOES set
            // one, because there the wait is real and the frame that clears it is seconds away.
            state.NeedsRedraw = true;
            return;
        }

        if (!string.IsNullOrEmpty(_preEnhancePath))
        {
            // Over the retain budget, so the original was not held. Ask for it through the SAME
            // request the file list uses -- HandleFileRequest already owns cancelling an in-flight
            // load, swapping the source, stats and star detection, none of which is worth a
            // second implementation for this one caller.
            state.StatusMessage = "Enhance off \u2014 reloading\u2026";
            state.NeedsRedraw = true;
            state.RequestedFilePath = _preEnhancePath;
            return;
        }

        // Neither route available. Say so rather than silently leaving the enhanced view up while the
        // button claims to be off.
        state.StatusMessage = "Cannot revert: the original is no longer available";
        state.IsEnhanced = true;
        state.NeedsRedraw = true;
    }

    /// <summary>
    /// Detects stars on <paramref name="document"/> in the background, superseding any detection
    /// already running.
    /// </summary>
    /// <remarks>
    /// Shared by the load path and the enhance path. An enhanced image needs its OWN detection: it is
    /// a different raster, and after a deblur the HFD and FWHM are exactly what changed -- so carrying
    /// the old list over would report the pre-deblur figures against post-deblur pixels. Before this
    /// existed nothing re-detected at all, which left Stars null on the enhanced document and silently
    /// disabled every control gated on it: Boost, Calibrate, SPCC and the star overlay all went dead
    /// after an enhance with nothing saying why.
    /// </remarks>
    private void StartStarDetection(AstroImageDocument document, CancellationToken appToken)
    {
        _starDetectionCts?.Cancel();
        _starDetectionCts?.Dispose();
        var sdCts = CancellationTokenSource.CreateLinkedTokenSource(appToken);
        _starDetectionCts = sdCts;
        _starDetectionTask = Task.Run(async () =>
        {
            try
            {
                await document.DetectStarsAsync(sdCts.Token);
                logger.LogInformation("Detected {StarCount} stars in {Duration:F1}s (HFR={HFR:F2}, FWHM={FWHM:F2})",
                    document.Stars?.Count ?? 0, document.StarDetectionDuration.TotalSeconds, document.AverageHFR, document.AverageFWHM);
                state.NeedsRedraw = true;
            }
            catch (OperationCanceledException) { logger.LogDebug("Star detection cancelled"); }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Star detection failed");
                document.Stars = StarList.Empty;
                state.StatusMessage = "Star detection failed";
                state.NeedsRedraw = true;
            }
        }, sdCts.Token);
    }

    /// <summary>
    /// Forgets the enhance toggle and anything retained for it. Called when the displayed image is
    /// replaced by something that is not an enhance result.
    /// </summary>
    /// <remarks>
    /// Without this, opening a second file and pressing Enhance-off would revert to the FIRST file --
    /// the retained document outlives the document it was the original of.
    /// </remarks>
    private void ForgetEnhanceState()
    {
        state.IsEnhanced = false;
        state.EnhanceProgressPct = 0f;
        _preEnhanceDocument = null;
        _preEnhancePath = null;
    }

    /// <summary>
    /// Applies a finished AI-enhance result on the render thread: swaps the displayed document for the
    /// enhanced one (<see cref="ViewerState.NeedsTextureUpdate"/> triggers the GPU re-upload next frame)
    /// and clears the in-progress flag. Poll every frame from OnRender, mirroring the SER-playback /
    /// sky-map async-result hand-offs. No-op until the background enhance task completes; on cancel /
    /// failure it just clears the flag -- <see cref="EnhanceActions"/> already wrote the reason to the
    /// status line.
    /// </summary>
    /// <remarks>
    /// Also starts star detection on the enhanced raster and, on any non-success path, drops whatever
    /// the revert route retained -- a run that produced no document leaves nothing to revert TO.
    /// </remarks>
    /// <param name="appToken">Ties the star detection it starts to the app's lifetime.</param>
    public void TryApplyPendingEnhance(CancellationToken appToken = default)
    {
        if (_enhanceTask is not { IsCompleted: true } task)
        {
            return;
        }
        _enhanceTask = null;
        state.IsEnhancing = false;
        _enhanceCts?.Dispose();
        _enhanceCts = null;

        // A run that did not produce a document leaves nothing to revert TO, so the retained original
        // is just memory held for a state that never happened. Cleared on every non-success path
        // below; a cancelled enhance is the common one now that the button can cancel.
        if (!task.IsCompletedSuccessfully || task.Result is null)
        {
            _preEnhanceDocument = null;
            _preEnhancePath = null;
        }

        if (task.IsCompletedSuccessfully && task.Result is { } enhancedDoc)
        {
            state.IsEnhanced = true;
            // The colour calibration measured on the ORIGINAL stars travels with the enhance; without
            // this the auto-retrigger re-fitted SPCC on the enhanced pixels and the frame took on a new
            // cast the moment that fit landed. See AstroImageDocument.InheritColorCalibration.
            if (Document is { } original)
            {
                enhancedDoc.InheritColorCalibration(original);
            }
            Document = enhancedDoc;
            _rawSource = enhancedDoc;
            _liveSource = null;
            state.IsSequence = false;
            state.NotifySourceReplaced();
            // Keep the pre-enhance pixels for the before/after split. Requested rather than done
            // here: this runs on the render thread but OUTSIDE the texture path, and the pixels are
            // still resident only until the next upload overwrites them -- which is where the renderer
            // consumes this. Costs no copy at all (see VkFitsImagePipeline.TryRetainChannelsAsBefore).
            state.RetainBeforePixelsRequested = true;
            state.NeedsTextureUpdate = true;

            // The enhanced raster is a different image, so it needs its own star list -- and after a
            // deblur the HFD/FWHM it reports are the point. Without this every star-gated control
            // (Boost, Calibrate, SPCC, the overlay) went dead the moment an enhance finished.
            StartStarDetection(enhancedDoc, appToken);
        }
        else if (task.IsFaulted)
        {
            // EnhanceActions catches its own exceptions; a fault here is an unexpected escape.
            state.StatusMessage = $"Enhance failed: {task.Exception?.GetBaseException().Message}";
        }
        state.NeedsRedraw = true;
    }

    /// <summary>
    /// Advances the BLINK comparison by one tick: steps the file-list selection at
    /// <see cref="ViewerState.BlinkFps"/>, wrapping at the end of the folder. Call from the render
    /// loop's <c>CheckNeedsRedraw</c> beside <see cref="TickPlayback"/>, and on every iteration for the
    /// same reason -- it is what paces the blink without a busy-spin. Returns true when a step was
    /// requested this tick.
    /// </summary>
    /// <remarks>
    /// A blink is the SER transport pointed at the file list (P19), and it only means anything because
    /// the frames share one display mapping: see <see cref="DisplayCarry"/>. The renderer stops it when
    /// a frame arrives that the run's anchor cannot describe.
    /// </remarks>
    public bool TickBlink()
    {
        if (!state.IsBlinking)
        {
            _blinkDueAt = null;
            return false;
        }

        // A sequence has its own transport and owns Space; a folder of one file has nothing to blink
        // between. Both clear the flag rather than idling, so the status readout cannot claim a blink
        // that is not running.
        if (state.IsSequence || state.ImageFileNames.Count < 2)
        {
            state.IsBlinking = false;
            _blinkDueAt = null;
            return false;
        }

        // Never queue a step behind a load. A folder that opens slower than the interval would
        // otherwise build a backlog of requests and keep stepping after the user stopped -- and the
        // wait is not wasted, since the next due time is measured from when the frame actually landed.
        if (IsLoadPending || state.RequestedFilePath is not null)
        {
            return false;
        }

        var now = _playbackClock.Elapsed.TotalSeconds;
        var interval = 1.0 / Math.Clamp(state.BlinkFps, MinBlinkFps, MaxBlinkFps);
        if (_blinkDueAt is { } due && now < due)
        {
            return false;
        }

        _blinkDueAt = now + interval;
        var next = state.SelectedFileIndex + 1;
        ViewerActions.SelectFile(state, next >= state.ImageFileNames.Count ? 0 : next);
        return true;
    }

    /// <summary>Slowest and fastest blink rates, in files per second. The floor keeps a stored zero from
    /// dividing by nothing; the ceiling keeps the blink slower than the disk.</summary>
    private const float MinBlinkFps = 0.25f;
    private const float MaxBlinkFps = 10f;

    // When the next blink step is due, in the playback clock's monotonic seconds; null when not blinking.
    private double? _blinkDueAt;

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
                live.SetSharpen(state.BuildWaveletOptions());
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
        // Everything the tracker holds, in one await. It already swallows and logs, so a failed
        // background operation cannot take the shutdown down with it.
        await tracker.DrainAsync();
        if (_enhanceTask is not null)
        {
            try { await _enhanceTask; } catch (OperationCanceledException) { logger.LogDebug("Enhance task cancelled during shutdown"); }
        }

        _loadCts?.Dispose();
        foreach (var d in _pendingDispose)
        {
            d.Dispose();
        }
        _pendingDispose.Clear();
        // Prefer the async drain (SerPreviewSource / LiveStackPreviewSource hold a memory-mapped reader and
        // await any in-flight decode/stack before releasing it -- bounded, non-blocking). Falls back to the
        // sync Dispose (which itself defers the release to a continuation rather than blocking) for any
        // source that is only IDisposable.
        switch (_rawSource)
        {
            case IAsyncDisposable rawAsync: await rawAsync.DisposeAsync(); break;
            case IDisposable rawSync: rawSync.Dispose(); break;
        }
        if (_liveSource is { } live)
        {
            await live.DisposeAsync();
        }
    }
}
