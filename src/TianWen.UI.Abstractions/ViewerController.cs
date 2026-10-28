using System;
using TianWen.Lib.Geometry;
using System.Collections.Generic;
using System.Collections.Immutable;
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
using TianWen.Lib.Imaging.Planetary;
using TianWen.Lib;

namespace TianWen.UI.Abstractions;

/// <summary>
/// Orchestrates file loading, star detection, and DI-dependent toolbar actions
/// for the FITS viewer. Owns document lifecycle and background task management.
/// Does not depend on SDL/Vulkan: display concerns stay in Program.cs.
/// </summary>
public sealed class ViewerController(
    ViewerState state,
    IDocumentCache documentCache,
    IFileDialogs fileDialogs,
    IPlateSolverFactory plateSolverFactory,
    ITimeProvider timeProvider,
    BackgroundTaskTracker tracker,
    ILogger<ViewerController> logger,
    TianWen.Lib.Devices.IExternal? external = null)
{
    private Task? _loadTask;
    private Task? _starDetectionTask;
    private Task<AstroImageDocument?>? _enhanceTask;

    /// <summary>
    /// The pending auto-crop scan. Held as a Task rather than written into state from the pool: the
    /// result carries a <see cref="PixelRect"/>, whose write is wider than a pointer and so not atomic,
    /// and the Task IS the synchronisation primitive (see the concurrency rules in CLAUDE.md).
    /// </summary>
    private Task<ViewerActions.CropScan>? _cropTask;

    /// <summary>
    /// The last rectangle a crop settled on, kept after the crop is switched off so switching it back
    /// on restores it instead of scanning again.
    /// </summary>
    /// <remarks>
    /// <b>Re-scanning is not equivalent, and on an ENHANCED frame it cannot work at all.</b> Both tiers
    /// look for evidence the enhancers destroy: the coverage plane belongs to the file on disk, the
    /// absent-pixel test needs exact zeros, and the edge walk needs a noise step -- and a deblur,
    /// gradient correction and denoise leave none of the three. A fresh scan there answers "covered edge
    /// to edge", so without this the toggle is one-way: switch the crop off after an enhance and it can
    /// never come back. Verified live 2026-09-09 on the Sagittarius Triplet master, which is also the
    /// session that showed the crop itself survives an enhance untouched.
    /// </remarks>
    private PixelRect? _rememberedCrop;

    /// <summary>
    /// The crop to reapply once an Enhance-revert reload (<see cref="RevertEnhance"/>'s no-retained-
    /// document branch) finishes, paired with the path it belongs to so a load superseded by an
    /// unrelated file open never applies someone else's rectangle. Consumed once, in
    /// <see cref="HandleFileRequest"/>.
    /// </summary>
    private (string Path, PixelRect? Crop)? _pendingRevertCrop;

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

    // The file whose display is remembered (#1814) and what that display was when it opened (restored, or the defaults). Leaving the
    // file saves the display only when it differs from this, so a file only looked at leaves no record.
    private string? _displayPath;
    private ViewerDisplayRecord? _displayAtOpen;

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
    /// host does not offer Enhance (the renderer's <see cref="ToolbarOffer"/>), so its button is hidden,
    /// and the <see cref="ToolbarAction.Enhance"/> dispatch is a no-op.
    /// </summary>
    public SharpenPipeline? EnhancePipeline { get; set; }

    /// <summary>
    /// The catalog the object overlay draws from, or null in a host that wires none. Set by the host
    /// after resolving it, the same way <see cref="EnhancePipeline"/> is; only the ANNOTATED save
    /// reads it, and null there simply omits that one overlay -- which is also what the on-screen
    /// renderer does before the catalog has finished loading.
    /// </summary>
    public DotNext.Threading.AsyncLazy<TianWen.Lib.Astrometry.Catalogs.ICelestialObjectDB>? CelestialObjectDB { get; set; }

    /// <summary>True while an AI enhance pass is in flight; used by the render loop's redraw gate.</summary>
    public bool IsEnhancePending => _enhanceTask is { IsCompleted: false };

    // The stacked view's Derive: the derived sharpening's gains for the master on show, seeded into the wavelet sliders (#1159).
    private readonly WaveletDerivation _derivation = new WaveletDerivation();

    // Where a finished Auto stack's sharpened master was written, how it was sharpened, the capture it stacked, what its view shows it
    // through (#1314 part 2: the run's layer when its gains were derived, else its sharpened master as written), and what the capture was
    // identified as. The render thread takes both images, or releases them.
    private sealed record AutoStackOutcome(string SharpenedPath, string How, string CapturePath, BestStackLayer? Layer, Image? Sharpened,
        PlanetaryIdentity Identity, int Kept, int Graded);

    // The Auto view of the SER on screen, the whole capture identified and stacked (#1314 part 2; A4, #1391; Best folded in, #817), kept
    // so switching back to it stacks nothing again. One run at a time, its result handed back through the task, its progress a percent the
    // stack's thread writes and the render thread reads (an int, so the read is never torn); once done, the run's master behind the same
    // sharpening layer, the capture it belongs to, and the dials it keeps while another view is on show. Render thread only but for the
    // percent: dropped there once its capture is not the one on show, never from a load.
    private sealed class BatchView
    {
        public Task<AutoStackOutcome>? Task { get; set; }
        public CancellationTokenSource? Cts { get; set; }

        // A field, so the stack's thread can write it with Volatile.Write.
        public int Percent;

        // The stage (a PlanetaryStackStage), written the same way, -1 before the run reports one.
        public int Stage = -1;

        // What a finished run says (where it was written and how it was sharpened), held until its view is on screen: the view's texture
        // upload clears the status line, so set at completion it would never be read. Keyed by the capture it stacked.
        public (string Path, string Message)? Note { get; set; }
        public LiveStackPreviewSource? Layer { get; set; }
        public string? Capture { get; set; }
        public ViewDials? Dials { get; set; }

        public string Name => "Full stack";
    }

    private readonly BatchView _auto = new BatchView();

    // Whose dials the state holds: a batch view's while it is on show, else (null) the live view's, kept here while one is.
    private BatchView? _dialsOnShow;
    private ViewDials? _liveDials;

    // What a view's sharpening dials hold, kept while another view is on show.
    private sealed record ViewDials(ImmutableArray<float> Gains, bool Derived, PlanetaryLiveLimb? Limb, DerivedGains? DerivedGains, bool Enabled, string? Note);

    /// <summary>How many Auto stacks this controller has started; the switch between views starts none (#1314 rule 7). For tests.</summary>
    internal int AutoStacksStarted { get; private set; }

    /// <summary>A task the next Auto run awaits before it starts its work, so a test can hold the run pending (#1875). For tests.</summary>
    internal Task? AutoRunGate { get; set; }

    /// <summary>How many derivations the sharpening layer has started (#1314 rule 7). For tests.</summary>
    internal int DerivationsStarted => _derivation.Started;

    /// <summary>The balance saturation the last Derive started with (#1880). For tests.</summary>
    internal double? DerivationSaturation => _derivation.StartedAtSaturation;

    /// <summary>The live view's rolling stack and the Auto view's layer, for tests (#1314 part 2, A4 #1391).</summary>
    internal (LiveStackPreviewSource? Live, LiveStackPreviewSource? Auto) ViewLayers => (_liveSource, _auto.Layer);

    /// <summary>True while the Auto stack is running.</summary>
    public bool IsAutoStackPending => _auto.Task is { IsCompleted: false };

    /// <summary>
    /// Whether the Auto stack needs a frame: one was asked for, one finished, or its progress moved past what the panel shows. No side
    /// effect, so the loop's redraw gate may ask it every iteration; <see cref="TickAutoStack"/> acts on it between frames.
    /// </summary>
    public bool AutoStackWantsFrame
        => state.AutoStackRequested
            || state.AutoViewRequested
            || WantsFrame(_auto)
            || (_auto.Task is not null && (Volatile.Read(ref _auto.Percent) / 100.0 != state.AutoStackProgress
                || StageOf(_auto) != state.AutoStackStage));

    private static PlanetaryStackStage? StageOf(BatchView batch)
        => Volatile.Read(ref batch.Stage) is >= 0 and var stage ? (PlanetaryStackStage)stage : null;

    private static bool WantsFrame(BatchView batch) => batch.Note is not null || batch.Task is { IsCompleted: true };

    // The raw source: the document (still) or the SerPreviewSource (SER). The playback driver -- the
    // SequencePlayer advances THIS even while the live stacked view is shown, so the playhead keeps moving and
    // the live stack can follow it; not while the Auto view is up, a still picture (#1875).
    private IPreviewSource? _rawSource;

    // The live rolling-window stack over the same SER (null for stills / non-stacked). Built lazily; shown
    // only once it has produced its first master (HasMaster).
    private LiveStackPreviewSource? _liveSource;
    private bool _autoAwaitedOnShow;

    /// <summary>
    /// The source the renderer previews. The raw frame normally; the sharpening layer's master when it is on show
    /// (<see cref="ViewerState.SharpenLayerOnShow"/>: the live rolling-window stack, or a planetary master's layer with its sharpening
    /// on, #1314) AND it has a master to show (otherwise the raw frame keeps showing while the first is built). For a still image this
    /// is the same object as <see cref="Document"/>; for a SER the raw source is a sequence source and <see cref="Document"/> is null.
    /// </summary>
    public IPreviewSource? Source => BatchLayerOnShow is { } batch ? batch
        : AutoAwaitsMaster ? _rawSource
        : (state.ShowAuto ? state.ShowStacked : state.SharpenLayerOnShow) && _liveSource is { HasMaster: true } ? _liveSource : _rawSource;

    // The Auto view holds the picture still (#1875) only while it has something to show for it: a run asked for, pending or just ended
    // and not yet taken in, or its master. A run cancelled or failed lets go, and the view under it (Frames or Live) plays on.
    private bool AutoHolds => state.ShowAuto && state.IsSequence && (state.AutoViewRequested || _auto.Task is not null || _auto.Layer is not null);

    // The Auto view holds and this capture has no Auto master yet: the raw frame at the held playhead shows, never the live
    // rolling stack or an older view as if it were this run's result (#1875).
    private bool AutoAwaitsMaster => AutoHolds && BatchLayerOnShow is null;

    // The Auto view's layer, once that view is chosen and its master is built (#1314 part 2); until then the view under it stays.
    private LiveStackPreviewSource? BatchLayerOnShow => state.ShowAuto && state.IsSequence && _auto.Layer is { HasMaster: true } layer ? layer : null;

    /// <summary>
    /// The document on show, which a Save writes (#1314): the sharpening layer's master while it is on show, else the still opened.
    /// Null for a SER's raw playback, which is no document.
    /// </summary>
    internal AstroImageDocument? ShownDocument => Source is LiveStackPreviewSource { Document: { } shown } ? shown : Document;

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
    /// Whether an auto-crop scan has been started and not yet handed to <see cref="TryApplyPendingCrop"/>.
    /// Lets a host or a test tell "the scan is still running" from "the scan ran and found nothing to
    /// crop", which leave <see cref="ViewerState.DisplayCrop"/> equally null.
    /// </summary>
    public bool IsCropScanPending => _cropTask is not null;

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

        // The file on show is about to go: its display is captured now, on this thread, and written before the next file's record is read
        // (the next one may be the same file).
        var leavingSave = SaveLeavingDisplayAsync();

        state.RequestedFilePath = null;
        _loadingPath = requestedPath;
        // A new file is not an enhanced view of the old one. Done here rather than on the completion
        // path so a load that is later superseded still clears the toggle it invalidated.
        ForgetEnhanceState();
        // Consumed (and cleared) here, once, regardless of which path below actually finishes -- a
        // superseded or failed load must not leave someone else's revert crop waiting for the NEXT
        // unrelated file that happens to load. Applied only if it still names THIS request below.
        var pendingRevertCrop = _pendingRevertCrop;
        _pendingRevertCrop = null;
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
            await leavingSave;
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
                    try { liveSource = LiveStackPreviewSource.Open(requestedPath, timeProvider); liveSource.Capture = serSource.Capture; }
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
                    state.SequencePath = requestedPath;
                    (state.MasterPlanet, state.MasterPath) = (null, null);
                    // The limb a derivation fitted belongs to the capture it was fitted on (#1201).
                    state.ForgetDerivation(switchOff: false);
                    // Stamped at ADOPTION, never at request: a superseded or failed load must not
                    // invalidate a comparison that is still valid for what is on screen.
                    state.NotifySourceReplaced();
                    state.ShowStacked = false; // a fresh file starts on the raw view (its stack has no master yet)
                    state.ShowAuto = false; // and its Auto stack, if it gets one, is its own (the render thread drops another capture's)
                    // What the last capture was taken of and through is not this one's (A4, #1391): its planet and filter go back to the
                    // panel's Auto, read from this capture, its telescope is no longer one set on it (#817), and the Auto view's identity
                    // is read again.
                    (state.PlanetaryBody, state.PlanetaryFilterNm, state.AutoIdentity) = (null, null, null);
                    state.AutoFrames = null;
                    state.PlanetaryTelescopeSetHere = false;
                    // And the camera it names, which a telescope the panel is set to is remembered for.
                    state.SequenceCamera = PlanetaryIdentification.CameraOf(requestedPath);
                    state.IsSequence = true;
                    // A capture that says which telescope took it (a TianWen recording's header, #1179) shows it in the panel, as the
                    // Auto stack will read it; one that says none leaves the panel's own.
                    if (PlanetaryCaptureName.Telescope(serSource.Telescope) is { ApertureMm: { } apertureMm } telescope)
                    {
                        state.PlanetaryApertureMm = apertureMm;
                        if (telescope.Design is not OpticalDesign.Unknown)
                        {
                            state.PlanetaryDesign = telescope.Design;
                        }
                    }
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
                    // And STF turns on the planetary stretch, the one planetary stack's preview renders with, never the deep-sky
                    // default the last frame left (#1440): a SER is a planet's, the Moon's or the Sun's.
                    if (!wasSequence)
                    {
                        state.StretchMode = StretchMode.None;
                        state.StretchModeBeforeLinear = StretchMode.Planetary;
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

            // What the file was last shown with (#1814), read here on the load's own thread so the restore below is part of the
            // swap and nothing paints the defaults first. A held run reads none (the anchor's display is the run's), and a planet's
            // master or a pre-stretched file is not a deep-sky display.
            ViewerDisplayRecord? remembered = null;
            if (newDoc is not null && RemembersFile(newDoc) && PlanetaryCaptureName.Named(newDoc.UnstretchedImage.ImageMeta.ObjectName) is null
                && !state.CarryDisplayAcrossFrames)
            {
                remembered = await LoadRememberedDisplayAsync(requestedPath, loadToken);
            }

            if (loadToken.IsCancellationRequested)
            {
                // Superseded after the load completed; don't clobber the newer selection.
                return;
            }

            if (newDoc is not null)
            {
                // A planetary master opened as a file gets the sharpening layer the stacked view has (#1314): the same source, over its one
                // master, so the dials, Derive, its stops and the colour look have one path. It opens with its sharpening off, as the
                // file holds it (it may be a sharpened master already).
                var masterPlanet = PlanetaryCaptureName.Named(newDoc.UnstretchedImage.ImageMeta.ObjectName);
                LiveStackPreviewSource? masterLayer = null;
                if (masterPlanet is not null)
                {
                    try
                    {
                        using var pin = await newDoc.EnsurePlanesAsync(loadToken);
                        masterLayer = new LiveStackPreviewSource(new FixedMaster(newDoc.UnstretchedImage.Clone()), requestedPath, timeProvider, logger);
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Failed to give the planetary master {FilePath} its sharpening layer", requestedPath);
                    }
                }

                Document = newDoc;
                LogStretchBasis("opened", newDoc);
                _rawSource = newDoc;
                _liveSource = masterLayer;
                state.SequencePath = null;
                (state.MasterPlanet, state.MasterPath) = masterLayer is null ? (null, null) : (masterPlanet, requestedPath);
                if (masterLayer is not null)
                {
                    // The limb and the stops a derivation read belong to the master they were read on (#1201, #1314).
                    state.ForgetDerivation(switchOff: true);
                }

                // A crop is a property of the FRAME it was scanned against, not of the viewer, so a
                // new document starts uncropped -- neither field carried a document identity to check
                // itself against, so an ordinary file switch (no Enhance involved at all) silently kept
                // showing the PREVIOUS file's rectangle whenever the new one happened to share its
                // dimensions, e.g. two exports of one stacked session. Overridden immediately below for
                // the one case that legitimately wants a crop restored: the reload half of an Enhance
                // revert.
                state.DisplayCrop = null;
                _rememberedCrop = null;

                // This load is the reload half of an Enhance revert (RevertEnhance stashed it
                // alongside the exact path it belongs to) -- put the crop that was active before
                // Enhance ran back on, mirroring the retained-document revert branch. A path
                // mismatch means a later, unrelated open won the race and this crop is not its to
                // apply, so it is silently dropped rather than obeyed.
                var revertCropApplied = false;
                if (pendingRevertCrop is { } pending
                    && string.Equals(pending.Path, requestedPath, StringComparison.OrdinalIgnoreCase))
                {
                    state.DisplayCrop = ViewerState.ResolveDisplayCrop(pending.Crop,
                        newDoc.UnstretchedImage.Width, newDoc.UnstretchedImage.Height);
                    _rememberedCrop = state.DisplayCrop;
                    revertCropApplied = true;
                }

                state.NotifySourceReplaced();
                state.ShowStacked = false; // stacking is a sequence-only mode
                state.ShowAuto = false;
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

                // The selection named an object in the frame that just went away, so it goes with the
                // cursor readout above and for the same reason. Cleared HERE rather than on every
                // source replacement: an enhance replaces the document too and its sky is the same
                // one, so a selection made before it is still about the object on screen.
                state.SelectedObject = null;
                state.SelectedStar = null;

                // No stretch for a pre-stretched image, the planetary stretch for a planet's frame (StretchMode.ForFrame, which the
                // Explorer thumbnail asks too), and the auto-stretch back for a deep-sky frame after either.
                var planetsFrame = StretchMode.ForFrame(newDoc.UnstretchedImage.ImageMeta, StretchMode.None) is StretchMode.Planetary;
                if (newDoc.IsPreStretched)
                {
                    state.StretchMode = StretchMode.None;
                }
                else if (planetsFrame)
                {
                    state.StretchMode = StretchMode.Planetary;
                }
                else if (state.StretchMode is StretchMode.Linked or StretchMode.Luma
                    && newDoc.UnstretchedImage.ChannelCount < 3)
                {
                    // Switch from color to mono: Linked/Luma need 3+ channels
                    state.StretchMode = StretchMode.Unlinked;
                }
                else if (state.StretchMode is StretchMode.None or StretchMode.Planetary)
                {
                    state.StretchMode = ViewerActions.DefaultStretchMode;
                }
                // Nor does STF turn on the planetary stretch a SER or a planet's master left, for a frame that is not a planet's (#1440).
                if (!planetsFrame && state.StretchModeBeforeLinear is StretchMode.Planetary)
                {
                    state.StretchModeBeforeLinear = ViewerActions.DefaultStretchMode;
                }
                state.HistogramLogScale = state.StretchMode is StretchMode.None;

                // The file's remembered display goes on over the defaults just set (#1814), unless this open is an Enhance revert's
                // reload, which brings its own crop back.
                _displayPath = null;
                _displayAtOpen = null;
                if (masterLayer is null && RemembersFile(newDoc) && !revertCropApplied)
                {
                    if (remembered is not null)
                    {
                        ViewerDisplayMemory.Apply(remembered, state);
                        state.DisplayCrop = ViewerState.ResolveDisplayCrop(remembered.DisplayCrop,
                            newDoc.UnstretchedImage.Width, newDoc.UnstretchedImage.Height);
                        _rememberedCrop = state.DisplayCrop;
                        state.HistogramLogScale = state.StretchMode is StretchMode.None;
                    }

                    _displayPath = requestedPath;
                    _displayAtOpen = ViewerDisplayMemory.Capture(state);
                }

                if (newDoc.Findings.Wcs is { } wcs)
                {
                    logger.LogInformation("WCS: HasCD={HasCDMatrix}, Approx={IsApproximate}, Scale={PixelScale:F2}\"/px, RA={CenterRA:F4}h, Dec={CenterDec:F4}°",
                        wcs.HasCDMatrix, wcs.IsApproximate, wcs.PixelScaleArcsec, wcs.CenterRA, wcs.CenterDec);
                }

                FileLoaded?.Invoke(Path.GetFileName(requestedPath));

                // Kick off star detection in the background; the stars it finds grade the frame (LightGrading).
                StartStarDetection(newDoc, appToken, grade: true);
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
    /// Saves the displayed image, with or without the overlays drawn over it. Asks for a path first,
    /// then renders and writes on the background tracker.
    /// </summary>
    /// <remarks>
    /// <para><b>Both variants render at the IMAGE's resolution, not the window's</b>, and neither is a
    /// screenshot: a 9576x6388 master saves at 9576x6388 from a 1280-pixel-wide window. The clean one
    /// goes through <see cref="DisplayRasterExport"/> and can be 16-bit; the annotated one goes through
    /// <see cref="AnnotatedRasterExport"/>, which runs the viewer itself over a CPU surface and is
    /// 8-bit because the overlay rasteriser is.</para>
    /// <para>The uniforms are recomputed here from the same state the renderer hands its shader rather
    /// than reached for across the renderer: <c>ComputeStretchUniforms</c> is documented as the single
    /// producer, so the same inputs give the same answer and the controller stays free of a renderer
    /// reference.</para>
    /// </remarks>
    /// <param name="withOverlays">Draw the WCS grid, star markers and object labels over the raster.</param>
    /// <param name="pngDepth">
    /// Which PNG a <c>.png</c> path means. It also names the dialog's first filter, so the dialog
    /// restates the row's choice instead of carrying it silently. Ignored for the annotated variant,
    /// which is 8-bit by construction.
    /// </param>
    /// <param name="appToken">The host's lifetime token.</param>
    /// <param name="annotation">
    /// The caller-supplied sky annotation the renderer is holding, so a plate-solve verification or a
    /// polar-alignment overlay lands in the file too. Ignored when <paramref name="withOverlays"/> is
    /// false.
    /// </param>
    public void SaveImage(bool withOverlays, PngDepth pngDepth, CancellationToken appToken,
        Overlays.WcsAnnotation annotation = default)
    {
        // What is on show (#1314): the sharpening layer's master while it is, never the file under it.
        if (ShownDocument is not { } saveDoc)
        {
            state.StatusMessage = "Nothing to save";
            return;
        }

        state.StatusMessage = "Saving...";
        tracker.RunGuarded(async token =>
        {
            // PNG first, so it is the dialog's default and the extension appended to a name typed
            // without one. It is also the lossless option in both lists.
            //
            // The clean list's first entry is named for the DEPTH the menu row chose, which is the
            // whole reason a caller passes one: having picked "8-bit" in a menu that then vanishes,
            // seeing "PNG (8-bit)" in the dialog is what confirms the save is the one asked for. The
            // names come from DisplayRasterFormat.DisplayName so the dialog and the status line can
            // never drift into calling the same file two things.
            var pngFormat = pngDepth.Png();
            FileDialogFilter[] filters = withOverlays
                ? [new("PNG", "png"), new("JPEG", "jpg;jpeg")]
                :
                [
                    new(pngFormat.DisplayName(), "png"),
                    new(DisplayRasterFormat.Jpeg.DisplayName(), "jpg;jpeg"),
                    new(DisplayRasterFormat.TiffFloat.DisplayName(), "tif;tiff"),
                ];

            var stem = Path.GetFileNameWithoutExtension(saveDoc.FilePath);
            if (stem.Length == 0)
            {
                stem = "image";
            }

            // The annotated variant suggests its own name, because the two files are of the same
            // picture and land in the same folder: saving one over the other is the mistake worth
            // designing out, and it is the file NAME that has to say which is which. The plain save says how the picture
            // was rendered too, so two saves of one frame under different settings do not land on each other either.
            var suggested = (withOverlays ? stem + "-annotated" : SuggestedDisplayedFileName(saveDoc, state, stem)) + ".png";
            var title = withOverlays ? "Save image with overlays" : "Save image as displayed";

            // The dialog appends the first filter's extension to a name typed without one (IFileDialogs' contract),
            // and the format is decided from the path's extension below, never the filter the user highlighted:
            // most platforms do not report it.
            var answer = await fileDialogs.SaveAsync(new FileDialogRequest
            {
                Title = title,
                Filters = filters,
                SuggestedFileName = suggested,
                InitialDirectory = SaveDialogDirectory(saveDoc.FilePath),
            }, token).ConfigureAwait(false);
            if (answer.Path is not { } target)
            {
                state.StatusMessage = answer.Error is { } error ? $"The save dialog failed: {error}" : null;
                return;
            }

            // Timed from the dialog closing, so the number is the WAIT: everything between the user
            // committing to a name and the file being on disk, and nothing they were not waiting for.
            // A full-frame save is seconds of work with no progress indication, and until this line
            // existed the only way to find out where those seconds went was to reproduce it in a
            // benchmark and hope the benchmark resembled the real thing. Twice today it did not.
            var saveStarted = System.Diagnostics.Stopwatch.StartNew();

            if (withOverlays)
            {
                AnnotatedRasterFormat annotatedFormat;
                if (AnnotatedRasterExport.FromExtension(target) is { } askedFor)
                {
                    annotatedFormat = askedFor;
                }
                else
                {
                    annotatedFormat = AnnotatedRasterFormat.Png;
                    target = RenamedToPng(target);
                }

                await AnnotatedRasterExport.WriteAsync(
                    saveDoc, state, target,
                    annotatedFormat,
                    CelestialObjectDB,
                    annotation,
                    cancellationToken: token).ConfigureAwait(false);

                LogSaved(target, annotatedFormat.DisplayName(), saveStarted);
                state.StatusMessage = $"Saved {Path.GetFileName(target)}";
                return;
            }

            DisplayRasterFormat format;
            if (DisplayRasterExport.FromExtension(target, pngDepth) is { } named)
            {
                format = named;
            }
            else
            {
                format = pngFormat;
                target = RenamedToPng(target);
            }

            await WriteDisplayedRasterAsync(saveDoc, state, target, format, token).ConfigureAwait(false);

            LogSaved(target, format.DisplayName(), saveStarted);
            state.StatusMessage = $"Saved {Path.GetFileName(target)}";
        },
        appToken,
        logger,
        withOverlays ? "Save annotated raster" : "Save display raster",
        onError: ex => state.StatusMessage = $"Save failed: {StatusText.FromException(ex)}",
        onFinally: () => state.NeedsRedraw = true);
    }

    /// <summary>
    /// Record what a save cost, in the shape the rest of this log already uses for a slow operation
    /// ("Detected 2203 stars in 0.3s").
    /// </summary>
    /// <remarks>
    /// The megapixels and the megabytes are here because the seconds mean nothing without them: the
    /// same picture is 121 MB at 16-bit and 16 MB at 8-bit, and those are different jobs rather than
    /// the same job going at different speeds.
    /// </remarks>
    private void LogSaved(string path, string format, System.Diagnostics.Stopwatch elapsed)
    {
        var megapixels = Document is { } document
            ? document.UnstretchedImage.Shape.Width * (long)document.UnstretchedImage.Shape.Height / 1e6
            : 0d;
        var megabytes = File.Exists(path) ? new FileInfo(path).Length / (1024.0 * 1024) : 0d;

        logger.LogInformation("Saved {File} as {Format}: {Megapixels:F1} MP, {Megabytes:F0} MB in {Seconds:F2}s",
            Path.GetFileName(path), format, megapixels, megabytes, elapsed.Elapsed.TotalSeconds);
    }

    /// <summary>
    /// "Save image as displayed": <paramref name="saveDoc"/> rendered as the screen shows it, through the rendition the
    /// renderer draws with (<see cref="DisplayRendition.FromState"/>, so the Tone finish travels in the stretch) and, on a
    /// star split, the starless plate with its stars screened over it at the Stars and Colour dials, as the shader and the
    /// annotated save compose it. The plain save used to stretch the recombined frame with neither, so on an enhanced frame
    /// its file showed the stars through the picture's curve and none of the finish.
    /// </summary>
    public static async Task WriteDisplayedRasterAsync(
        AstroImageDocument saveDoc, ViewerState state, string target, DisplayRasterFormat format, CancellationToken cancellationToken)
    {
        using var pin = await saveDoc.EnsurePlanesAsync(cancellationToken).ConfigureAwait(false);
        var image = saveDoc.UnstretchedImage;
        var rendition = DisplayedRendition(saveDoc, state);

        // The boost pivots on the POST-stretch background, exactly as the shader's curvesMidpoint
        // does; passing the default 0.25 instead would move the curve.
        var background = rendition.Stretch.ComputePostStretchBackground(
            saveDoc.PerChannelBackground, saveDoc.LumaBackground);

        var displayedChannel = state.ChannelView.DisplayedSourceChannel(image.ChannelCount);
        var split = ShownStarSplit(saveDoc, state);

        await DisplayRasterExport.WriteAsync(
            split?.Starless ?? image, target,
            format,
            rendition.Stretch,
            rendition.CurvesBoost, rendition.CurvesMode, rendition.CurveData, background,
            rendition.HdrAmount, rendition.HdrKnee,
            displayedChannel: displayedChannel,
            debayerAlgorithm: state.DebayerAlgorithm.ResolveAuto(image),
            crop: ViewerState.ResolveDisplayCrop(state.DisplayCrop, image.Width, image.Height),
            starScreen: split?.ScreenFor(rendition),
            text: RenditionText(saveDoc, state, rendition.Stretch.Mode, split is not null),
            cancellationToken: cancellationToken);
    }

    /// <summary>What a displayed save carries: the program, the source file and every display setting
    /// (<see cref="RenditionMetadata"/>), so the file says how it was made whatever it is later called.</summary>
    internal static DisplayRasterText RenditionText(
        AstroImageDocument saveDoc, ViewerState state, StretchMode shownMode, bool starSplitShown)
        => new(RenditionMetadata.Software,
            Path.GetFileName(saveDoc.FilePath) is { Length: > 0 } source ? source : null,
            RenditionMetadata.Json(state, shownMode, saveDoc.IsEnhanceResult, starSplitShown));

    /// <summary>The rendition <paramref name="saveDoc"/> is drawn with under <paramref name="state"/>, through the one producer
    /// of its uniforms (<c>ComputeStretchUniforms</c>), so the save and the name it suggests agree on the stretch shown.</summary>
    internal static DisplayRendition DisplayedRendition(AstroImageDocument saveDoc, ViewerState state)
        => DisplayRendition.FromState(saveDoc.ComputeStretchUniforms(
            state.StretchMode, state.StretchParameters,
            bgNeutralizationStrength: state.BackgroundNeutralizationStrength,
            manualWhiteBalance: state.ManualWhiteBalance,
            applyColorCalibration: state.ColorCalibrationEnabled), state);

    /// <summary>The star split composed into the picture: the split composes only in the composite view, as on screen; a
    /// single channel shows the recombined frame.</summary>
    internal static StarSplit? ShownStarSplit(AstroImageDocument saveDoc, ViewerState state)
        => state.ChannelView.DisplayedSourceChannel(saveDoc.UnstretchedImage.ChannelCount) is null ? saveDoc.StarSplit : null;

    /// <summary>
    /// The file name "Save image as displayed" suggests: the document's stem and how it is rendered
    /// (<see cref="RenditionFileName.Suffix"/>), so a second save under other settings does not land on the first.
    /// </summary>
    internal static string SuggestedDisplayedFileName(AstroImageDocument saveDoc, ViewerState state, string stem)
        => stem + RenditionFileName.Suffix(state, DisplayedRendition(saveDoc, state).Stretch.Mode,
            enhanced: saveDoc.IsEnhanceResult, starSplitShown: ShownStarSplit(saveDoc, state) is not null);

    /// <summary>
    /// <paramref name="path"/> with a <c>.png</c> extension, for when the writer fell back to PNG
    /// because the chosen name asked for a container it cannot produce.
    /// </summary>
    /// <remarks>
    /// <para><b>A native save dialog returns what was TYPED</b>, its filter list notwithstanding, so a
    /// path can name a format neither writer has an encoder for: <c>.tif</c> for an annotated save,
    /// which is PNG or JPEG only, and anything at all for either -- <c>.bmp</c>, <c>.gif</c>, a
    /// deliberate <c>.txt</c>. Both paths used to write PNG bytes under the typed name, and that is
    /// the worse half of the failure: the file is fine, the NAME is a lie, and it surfaces later as
    /// something that will not open rather than now as something that did not save.</para>
    /// <para>Renaming rather than refusing, because the intent is unambiguous -- the container was the
    /// only unavailable part -- and the status line reports the file that was actually written, so
    /// the correction is visible at the moment it happens rather than discovered afterwards.</para>
    /// </remarks>
    private static string RenamedToPng(string path) =>
        path.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? path : Path.ChangeExtension(path, ".png");

    /// <summary>
    /// The folder a Save dialog opens in: the one the open document was read from, or <c>null</c> for
    /// the platform's own default when the document came from no file (a live frame) or its folder is
    /// no longer there.
    /// </summary>
    /// <remarks>
    /// Where the file already lives is where its exports belong (P35): the suggested name is the
    /// file's own stem, and the annotated variant's name is chosen so the two can sit beside it. A
    /// folder that has gone is not offered, because the dialog would open on an error or on a
    /// fallback of its own choosing instead of the platform's.
    /// </remarks>
    internal static string? SaveDialogDirectory(string? documentPath)
    {
        if (string.IsNullOrWhiteSpace(documentPath))
        {
            return null;
        }

        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(documentPath));
            return directory is { Length: > 0 } && Directory.Exists(directory) ? directory : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    /// <summary>
    /// Logs the statistics a document's auto-stretch is solved FROM, once per document rather than
    /// per frame.
    /// </summary>
    /// <remarks>
    /// <para>Written for the enhance colour cast (viewer-prerelease-fixes P30): an enhanced frame
    /// renders as a flat colour field in <see cref="StretchMode.Auto"/>, and three different faults
    /// produce that same screen. <b>All three are visible in these inputs</b>, which is why the
    /// derived uniforms are not logged here -- reproducing the renderer's full argument list just to
    /// print it invites the log and the screen disagreeing:</para>
    /// <list type="bullet">
    /// <item>pedestal ~= median -- the zero-pedestal case, where the shadow subtraction leaves nearly
    /// nothing and the rescale explodes (the trap <c>MasterPreviewRenderer.WithZeroPedestal</c> exists
    /// for on the stacking side).</item>
    /// <item>medians far apart, having been close before -- the enhancer moved the colour balance, so
    /// the answer is inheritance rather than re-solving neutralisation on flattened pixels.</item>
    /// <item>MAD collapsing toward zero -- rescale goes as 1/(k*MAD), so it explodes on a flattened
    /// background whatever the pedestal does, and wants a MAD floor.</item>
    /// </list>
    /// <para>Logged at Debug on document REPLACEMENT only (an open, and an enhance landing), so the
    /// before/after pair a diagnosis needs is in the log with no per-frame cost. The viewer had no
    /// logging on this path at all, so the numbers were previously only readable by eye off the info
    /// panel.</para>
    /// </remarks>
    private void LogStretchBasis(string occasion, AstroImageDocument? document)
    {
        if (document is not { } doc || !logger.IsEnabled(LogLevel.Debug))
        {
            return;
        }

        // What the SHADER gets, not what this document measured -- those differ whenever a display
        // anchor is held, and an anchor surviving an enhance is itself one of P30's candidates.
        var stats = doc.BasisPerChannelStats;
        var own = doc.PerChannelStats;
        var bg = doc.PerChannelBackground;
        // MaxValue is in here because it becomes the shader's NormFactor (1/MaxValue): every value
        // is scaled by it, so a flattened frame whose observed PEAK collapsed would render bright
        // with a perfectly healthy-looking set of medians. The calibration and neutralisation flags
        // are here because they gate multipliers applied BESIDE the per-channel curve, and the curve
        // alone was measured to render this frame's background neutral -- so what is left is what
        // sits beside it.
        logger.LogDebug(
            "Stretch basis [{Occasion}]: anchored={Anchored} channels={Channels} imageMax={Max:G6} imageMin={Min:G6} mode={Mode} calibrated={Calibrated} neutralised={Neutralised} wbInherited={Wb}",
            occasion, doc.HasDisplayAnchor, stats.Length,
            doc.UnstretchedImage.MaxValue, doc.UnstretchedImage.MinValue,
            state.StretchMode, state.ColorCalibrationEnabled, state.BackgroundNeutralizationEnabled,
            doc.ColorCalibration is { } cc ? $"{cc.R:G6}/{cc.G:G6}/{cc.B:G6}" : "none");

        for (var c = 0; c < stats.Length; c++)
        {
            logger.LogDebug(
                "Stretch basis [{Occasion}] ch{Channel}: pedestal={Pedestal:G6} median={Median:G6} mad={Mad:G6} bg={Bg:G6}",
                occasion, c, stats[c].Pedestal, stats[c].Median, stats[c].Mad,
                c < bg.Length ? bg[c] : float.NaN);

            // Only when they disagree, so a normal open logs nothing extra and the line that does
            // appear is the finding rather than noise.
            if (doc.HasDisplayAnchor && c < own.Length && own[c] != stats[c])
            {
                logger.LogDebug(
                    "Stretch basis [{Occasion}] ch{Channel}: THIS document measured pedestal={Pedestal:G6} median={Median:G6} mad={Mad:G6} -- the curve is solved from the anchor above, not from these",
                    occasion, c, own[c].Pedestal, own[c].Median, own[c].Mad);
            }
        }

        if (doc.LumaStats is { } luma)
        {
            logger.LogDebug(
                "Stretch basis [{Occasion}] luma: pedestal={Pedestal:G6} median={Median:G6} mad={Mad:G6}",
                occasion, luma.Pedestal, luma.Median, luma.Mad);
        }
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
                    var answer = await fileDialogs.OpenAsync(new FileDialogRequest
                    {
                        Title = "Open image",
                        Filters = AstroImageDocument.FileDialogFilters,
                    }).ConfigureAwait(false);
                    state.StatusMessage = answer.Error is { } error ? $"The open dialog failed: {error}" : null;
                    if (answer.Path is not { } picked)
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
                // Right-click, or a host with no dropdown: the clean raster, one click, as before.
                SaveImage(withOverlays: false, PngDepth.SixteenBit, appToken);
                break;

            case ToolbarAction.AutoCrop:
                if (state.DisplayCrop is not null)
                {
                    // Off is immediate and needs no scan: the crop only ever hid pixels that were there
                    // the whole time. The rectangle is REMEMBERED on the way out -- see _rememberedCrop
                    // for why switching it back on must not mean scanning again.
                    _rememberedCrop = state.DisplayCrop;
                    state.DisplayCrop = null;
                    state.StatusMessage = "Showing the whole frame";
                    state.NeedsRedraw = true;
                }
                else if (Document is { } rememberedDoc
                    && ViewerState.ResolveDisplayCrop(_rememberedCrop,
                        rememberedDoc.UnstretchedImage.Width, rememberedDoc.UnstretchedImage.Height) is { } remembered)
                {
                    state.DisplayCrop = remembered;
                    var rememberedImage = rememberedDoc.UnstretchedImage;
                    var rememberedKept = 100.0 * remembered.Width * remembered.Height
                        / (rememberedImage.Width * (double)rememberedImage.Height);
                    state.StatusMessage =
                        $"Cropped to {remembered.Width}x{remembered.Height} ({rememberedKept:F1}% of the frame, remembered)";
                    state.NeedsRedraw = true;
                }
                else if (Document is { } cropDoc && _cropTask is null)
                {
                    // Off the render thread: the absent-pixel scan reads every channel of every pixel
                    // (52.6 ms on a 3073 x 3085 x 3 master), a coverage sidecar decodes a second
                    // full-frame FITS, and the edge walk profiles four bands on top of that -- 0.5 to
                    // 2.3 s measured in Debug on the corpus. Any of those is dropped frames on a press.
                    state.StatusMessage = "Finding the covered area...";
                    var image = cropDoc.UnstretchedImage;
                    var path = cropDoc.FilePath;
                    _cropTask = Task.Run(async () =>
                    {
                        using var pin = await cropDoc.EnsurePlanesAsync(appToken).ConfigureAwait(false);
                        return ViewerActions.ScanForCrop(image, path, logger);
                    }, appToken);
                    _ = _cropTask.ContinueWith(_ => state.NeedsRedraw = true, TaskScheduler.Default);
                }
                break;

            case ToolbarAction.PlateSolve:
                if (Document is { } solveDoc && !state.IsPlateSolving && !solveDoc.Findings.IsPlateSolved)
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
                // The backend is chosen in the Enhance popover (one way to choose), never by a press here, so a
                // right press (reverse) is a no-op: it used to cycle the backend and must not fall through to a run.
                if (reverse)
                {
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

                // On a planet's way the deep-sky models do not serve the frame, and the popover shows the planetary
                // half, which has no progress or Cancel for this job: E and the button run nothing there.
                if (!state.EnhancesAsPlanet && Document is { } enhanceDoc && EnhancePipeline is { } pipeline
                    && _enhanceTask is null)
                {
                    state.IsEnhancing = true;
                    state.EnhanceProgressPct = 0f;
                    state.StatusMessage = "Enhancing...";
                    state.NeedsRedraw = true;
                    // Snapshot the backend preference into immutable options per click (no global).
                    var options = new EnhanceOptions(state.PreferredEnhanceBackend);
                    var debayer = state.DebayerAlgorithm.ResolveAuto(enhanceDoc.UnstretchedImage);
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
                    // The crop goes to the PIPELINE, not just the display: see EnhanceActions for why a
                    // spatial model must not be shown the canvas ring at all.
                    var enhanceCrop = ViewerState.ResolveDisplayCrop(state.DisplayCrop,
                        enhanceDoc.UnstretchedImage.Width, enhanceDoc.UnstretchedImage.Height);
                    _enhanceTask = Task.Run(
                        () => EnhanceActions.EnhanceAsync(enhanceDoc, state, pipeline, options, debayer, enhanceCrop, enhanceToken),
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

        // The AUTHORITATIVE crop to restore is the one the enhanced document itself was cropped to
        // (Document is still that document here, whichever route fires below) -- never _rememberedCrop.
        // That field is sticky across an unrelated file switch (nothing clears it when a new document
        // loads), so opening a cropped file, switching to an UNCROPPED one, enhancing it and reverting
        // used to restore the FIRST file's leftover rectangle onto the second: a real crop, just the
        // wrong one, which reads as "the crop doesn't cover enough of this frame" rather than "no crop
        // at all". Null here when this enhance never had a crop to begin with, which resolves to no
        // crop below instead of reawakening someone else's.
        var cropToRestore = Document?.SourceCrop;

        if (_preEnhanceDocument is { } retained)
        {
            // A reference swap: nothing is copied, and the document was never mutated by the run
            // (the pipeline consumes its own outputs, never the caller's buffers).
            _preEnhanceDocument = null;
            Document = retained;
            LogStretchBasis("reverted", retained);
            _rawSource = retained;
            DropMasterLayer();
            state.IsSequence = false;
            // The full frame is back, so the crop that was taken off when the enhance baked it in goes
            // back on: reverting means returning to the view the enhance was launched from, and the
            // border is once again there to hide. Nothing happens when there was no crop, and a
            // rectangle that does not fit the restored frame resolves to null rather than being obeyed.
            state.DisplayCrop = ViewerState.ResolveDisplayCrop(cropToRestore,
                retained.UnstretchedImage.Width, retained.UnstretchedImage.Height);
            _rememberedCrop = state.DisplayCrop;
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
            // second implementation for this one caller. HandleFileRequest has no idea a reload is an
            // enhance-revert rather than an ordinary open, so the crop has to travel alongside the
            // path it belongs to -- see _pendingRevertCrop; a plain "restore the last remembered crop"
            // in the request handler would suffer the same cross-file leak fixed above, for every open.
            state.StatusMessage = "Enhance off \u2014 reloading\u2026";
            state.NeedsRedraw = true;
            _pendingRevertCrop = (_preEnhancePath, cropToRestore);
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
    /// <param name="grade">Whether the stars found grade the frame (<see cref="LightGrading"/>): an opened file's, never an
    /// enhanced raster's, which has the original's path and would replace the grade of the frame as it was taken.</param>
    private void StartStarDetection(AstroImageDocument document, CancellationToken appToken, bool grade = false)
    {
        _starDetectionCts?.Cancel();
        _starDetectionCts?.Dispose();
        var sdCts = CancellationTokenSource.CreateLinkedTokenSource(appToken);
        _starDetectionCts = sdCts;
        _starDetectionTask = Task.Run(async () =>
        {
            try
            {
                // The grade reads the planes, so it runs inside the detection, before the planes are released
                // (LightGrading.Grade is synchronous and a released image refuses a read).
                await document.DetectStarsAsync(sdCts.Token, whileResident: (image, stars) =>
                {
                    if (grade && document.FilePath.Length > 0 && LightGrading.Grade(image, stars) is { } frameGrade)
                    {
                        state.RecordFrameGrade(document.FilePath, frameGrade);
                    }
                });
                var found = document.Findings;
                logger.LogInformation("Detected {StarCount} stars in {Duration:F1}s (HFD={HFD:F2}, FWHM={FWHM:F2})",
                    found.Stars?.Count ?? 0, document.StarDetectionDuration.TotalSeconds, found.MedianHfd, found.MedianFwhm);
                state.NeedsRedraw = true;
            }
            catch (OperationCanceledException) { logger.LogDebug("Star detection cancelled"); }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Star detection failed");
                document.RecordStars(StarList.Empty);
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
    /// <summary>
    /// Applies a finished auto-crop scan, if one is pending. Call from the frame loop beside
    /// <see cref="TryApplyPendingEnhance"/>.
    /// </summary>
    /// <remarks>
    /// A crop that would keep the whole frame is reported as such and NOT set, so the button says
    /// "nothing to crop" rather than appearing to do nothing: a frame with no ring is the common case for
    /// anything that is not a stacked master.
    /// </remarks>
    public void TryApplyPendingCrop()
    {
        if (_cropTask is not { IsCompleted: true } task)
        {
            return;
        }

        _cropTask = null;
        if (!task.IsCompletedSuccessfully)
        {
            state.StatusMessage = "Auto-crop failed";
            state.NeedsRedraw = true;
            return;
        }

        var scan = task.Result;
        var rect = scan.Rect;
        var image = Document?.UnstretchedImage;
        if (image is null)
        {
            return;
        }

        if (rect.Width <= 0 || rect.Height <= 0)
        {
            state.StatusMessage = "Nothing covered by every sub";
        }
        else if (rect.Width >= image.Width && rect.Height >= image.Height)
        {
            state.StatusMessage = "Nothing to crop: the frame is covered edge to edge";
        }
        else
        {
            // The zoom and the pan are left exactly as they were: a crop takes a border away, and
            // re-fitting on top of that is a second, unasked-for change to the view -- the picture
            // jumps and grows at the moment the user was looking at its edge. Fit remains one keypress
            // away (F), and with a crop in force it fits the CROP, which is the whole point of it.
            state.DisplayCrop = rect;
            _rememberedCrop = rect;
            var kept = 100.0 * rect.Width * rect.Height / (image.Width * (double)image.Height);
            // Which tier answered is worth a word: one is the master's own record of what it covered, the
            // other is measured off the noise and can be short of the mark. "Edge held" names the case
            // where an edge was left alone without being clean: its noise had not settled, or it settled
            // deeper than the loss cap allows -- CoverageEdgeTrim.Declined, see CoverageEdgeWalk.
            var how = scan.FromCoverage ? ", by coverage" : scan.Declined ? ", edge held" : "";
            state.StatusMessage = $"Cropped to {rect.Width}x{rect.Height} ({kept:F1}% of the frame{how})";
        }

        state.NeedsRedraw = true;
    }

    // The colour look on a master on show (#1277): the balanced master it goes on, its looked copy and the look that made it, the run
    // making one and what it is for, and a look that could not be made for that master (not tried again until either changes). A
    // rendering: the file and the master read from it are never touched, and "True colour" gives the master back.
    private AstroImageDocument? _lookBase;
    private (AstroImageDocument Document, ColourLook Look)? _looked;
    private Task<(AstroImageDocument? Document, string? Refusal)>? _lookTask;
    private (AstroImageDocument Base, ColourLook Look)? _lookRunFor;
    private (AstroImageDocument Base, ColourLook Look)? _lookRefusedFor;
    private CancellationTokenSource? _lookCts;

    /// <summary>True while a colour look is being made for the master on show.</summary>
    public bool IsPlanetaryLookPending => _lookTask is { IsCompleted: false };

    /// <summary>
    /// The colour look on the master on show (#1277, <see cref="ViewerState.PlanetaryLook"/>): the master as balanced, or a copy of it with
    /// the look, made off the render thread by <see cref="PlanetaryColourLook.OnMaster"/> (the routine <c>planetary look</c> runs) and kept,
    /// so "True colour" and back is a swap, not a run. Only a still whose <c>OBJECT</c> names a planet; the live stacked view draws the look
    /// on each master itself. A new master on show drops both copies; an enhance result is left as it is. Render thread, between frames;
    /// true when the document on show changed.
    /// </summary>
    public bool TickPlanetaryLook(CancellationToken appToken = default)
    {
        // A finished run: its copy kept, or its refusal said and remembered, while it is for the master still on show.
        if (_lookTask is { IsCompleted: true } done && _lookRunFor is { } runFor)
        {
            (_lookTask, _lookRunFor) = (null, null);
            _lookCts?.Dispose();
            _lookCts = null;
            if (ReferenceEquals(runFor.Base, _lookBase))
            {
                if (done.IsCompletedSuccessfully && done.Result.Document is { } made)
                {
                    _looked = (made, runFor.Look);
                    state.PlanetaryLookNote = null;
                }
                else
                {
                    _lookRefusedFor = runFor;
                    state.PlanetaryLookNote = done.IsCompletedSuccessfully ? done.Result.Refusal : "the look could not be made";
                    if (done.Exception is { } fault)
                    {
                        logger.LogWarning(fault, "Colour look failed on {Path}", runFor.Base.FilePath);
                    }
                }
                state.NeedsRedraw = true;
            }
        }

        // The sharpening layer puts the look on its own masters (LiveStackPreviewSource.SetSharpen), the stacked view's and a planetary
        // master's alike (#1314).
        if (state.SharpenLayerOnShow || Document is not { } shown)
        {
            return false;
        }

        // A master on show that is neither the base nor its look is a new one (a file opened, a best stack finished): both copies go.
        if (_lookBase is not { } baseDoc
            || !(ReferenceEquals(shown, baseDoc) || (_looked is { } kept && ReferenceEquals(shown, kept.Document))))
        {
            if (shown.IsEnhanceResult)
            {
                return false;
            }
            ForgetPlanetaryLook();
            _lookBase = baseDoc = shown;
        }

        AstroImageDocument wanted;
        if (state.PlanetaryLook is not { } look)
        {
            wanted = baseDoc;
        }
        else if (_looked is { } ready && ready.Look.Equals(look))
        {
            wanted = ready.Document;
        }
        else
        {
            // Shown as it is until the look is made: a run for this master and look, unless one is under way or this one was refused.
            wanted = shown;
            var refused = _lookRefusedFor is { } no && ReferenceEquals(no.Base, baseDoc) && no.Look.Equals(look);
            if (_lookTask is null && !refused)
            {
                StartPlanetaryLook(baseDoc, look, appToken);
            }
        }

        if (ReferenceEquals(wanted, shown))
        {
            return false;
        }
        Document = wanted;
        _rawSource = wanted;
        state.NotifySourceReplaced();
        state.NeedsTextureUpdate = true;
        return true;
    }

    // A run making the look's copy of a master: its planet from OBJECT and its instant from the header, as `planetary look` reads them.
    private void StartPlanetaryLook(AstroImageDocument master, ColourLook look, CancellationToken appToken)
    {
        var image = master.UnstretchedImage;
        if (PlanetaryCaptureName.Named(image.ImageMeta.ObjectName) is not { } planet)
        {
            return;
        }
        if (PlanetaryBestStack.InstantOf(image, epoch: null) is not { } instant)
        {
            _lookRefusedFor = (master, look);
            state.PlanetaryLookNote = "no time in its header to place the planet at";
            return;
        }
        _lookCts = CancellationTokenSource.CreateLinkedTokenSource(appToken);
        var token = _lookCts.Token;
        var (path, wcs) = (master.FilePath, master.Findings.Wcs);
        _lookRunFor = (master, look);
        state.PlanetaryLookNote = null;
        _lookTask = Task.Run(async () =>
        {
            using var pin = await master.EnsurePlanesAsync(token).ConfigureAwait(false);
            // A master left in the camera's colours is balanced through its camera's own matrix, as the stack balances one (#1279).
            var camera = await PlanetaryColourBalance.CameraColourAsync(image.ImageMeta.Instrument, token).ConfigureAwait(false);
            var (looked, refusal) = PlanetaryColourLook.OnMaster(image, planet, instant, look, camera);
            if (looked is null)
            {
                return (null as AstroImageDocument, refusal);
            }
            var document = await AstroImageDocument.AdoptImageAsync(looked, DebayerAlgorithm.None, wcs, path, cancellationToken: token).ConfigureAwait(false);
            return (document, null);
        }, token);
        _ = _lookTask.ContinueWith(_ => state.NeedsRedraw = true, TaskScheduler.Default);
    }

    // Both copies dropped, and a run under way cancelled: its result is for a master no longer on show.
    private void ForgetPlanetaryLook()
    {
        _lookCts?.Cancel();
        (_lookBase, _looked, _lookRefusedFor) = (null, null, null);
        state.PlanetaryLookNote = null;
    }

    /// <summary>
    /// The Auto stack of the SER on screen (#1159, A4 #1391, #817; <see cref="PlanetaryAuto"/>, the routine <c>planetary stack --auto</c>
    /// runs): started when asked (<see cref="ViewerState.AutoStackRequested"/>), or the running one cancelled; its progress copied into
    /// <see cref="ViewerState.AutoStackProgress"/>; a finished run's masters written beside the capture as <c>planetary stack --auto</c>
    /// writes them, its view shown while that capture is still the one on screen. Render thread, between frames
    /// (<see cref="AutoStackWantsFrame"/> asks for them); true when something on screen changed.
    /// </summary>
    public bool TickAutoStack(CancellationToken appToken = default)
    {
        var changed = false;
        // Said once its view is on screen and uploaded (#1314 part 2): the upload clears the status line.
        if (_auto.Note is { } note && !state.NeedsTextureUpdate && BatchLayerOnShow is not null
            && string.Equals(_auto.Capture, note.Path, StringComparison.OrdinalIgnoreCase))
        {
            _auto.Note = null;
            state.StatusMessage = note.Message;
            changed = true;
        }
        // Another capture's Auto stack is not this one's (#1314 part 2).
        changed |= DropIfAnotherCapture(_auto);
        if (state.AutoStackRequested)
        {
            state.AutoStackRequested = false;
            changed = true;
            if (_auto.Task is { IsCompleted: false })
            {
                _auto.Cts?.Cancel();
            }
            else if (state.SequencePath is null)
            {
                state.StatusMessage = "Full stack: open a SER capture first";
            }
            else
            {
                state.ChoosePlanetaryView(PlanetaryView.Auto);
            }
        }
        if (state.AutoViewRequested)
        {
            state.AutoViewRequested = false;
            changed = true;
            // Shown at once where the capture has one; started where it has none and none runs (#1314 rule 7: a switch stacks nothing).
            if (_auto.Layer is null && _auto.Task is not { IsCompleted: false } && state.SequencePath is { } capture)
            {
                StartBatch(_auto, capture, appToken);
            }
        }

        changed |= TickBatch(_auto);
        var shownProgress = _auto.Task is { IsCompleted: false } ? Volatile.Read(ref _auto.Percent) / 100.0 : (double?)null;
        var shownStage = _auto.Task is { IsCompleted: false } ? StageOf(_auto) : null;
        if (state.AutoStackProgress != shownProgress || state.AutoStackStage != shownStage)
        {
            (state.AutoStackProgress, state.AutoStackStage) = (shownProgress, shownStage);
            changed = true;
        }
        var framesShown = AutoAwaitsMaster;
        if (state.AutoShowsFrames != framesShown)
        {
            state.AutoShowsFrames = framesShown;
            changed = true;
        }
        return changed;
    }

    // A batch view's stack and layer dropped once the capture on show is another.
    private bool DropIfAnotherCapture(BatchView batch)
    {
        if (batch.Capture is null || string.Equals(batch.Capture, state.SequencePath, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        DropBatch(batch);
        return true;
    }

    // A batch view's finished run taken in: shown behind the layer as its view (#1314 part 2), never opened as a file, the capture staying
    // on screen; said again once the view is on screen, whose upload clears the line. True when it finished.
    private bool TickBatch(BatchView batch)
    {
        if (batch.Task is not { IsCompleted: true } task)
        {
            return false;
        }
        batch.Task = null;
        batch.Cts?.Dispose();
        batch.Cts = null;
        if (task.IsCompletedSuccessfully)
        {
            var outcome = task.Result;
            var message = $"{batch.Name}: {Path.GetFileName(outcome.SharpenedPath)} ({outcome.How})";
            state.StatusMessage = message;
            if (string.Equals(state.SequencePath, outcome.CapturePath, StringComparison.OrdinalIgnoreCase))
            {
                AdoptBatch(batch, outcome);
                batch.Note = (outcome.CapturePath, message);
                (state.AutoIdentity, state.AutoFrames) = (outcome.Identity, (outcome.Kept, outcome.Graded));
                // The panel's telescope, where none was set on this capture, shows the one the run read, which the live view's Derive
                // then takes too; not saved, since nobody set it.
                if (!state.PlanetaryTelescopeSetHere && outcome.Identity is { ApertureMm: { } usedMm } used)
                {
                    (state.PlanetaryApertureMm, state.PlanetaryDesign) = (usedMm, used.Design);
                }
            }
            else
            {
                outcome.Layer?.Master.Release();
                outcome.Sharpened?.Release();
            }
        }
        else if (task.IsCanceled)
        {
            state.StatusMessage = $"{batch.Name} cancelled";
        }
        else
        {
            logger.LogWarning(task.Exception?.GetBaseException(), "{Stack} failed", batch.Name);
            state.StatusMessage = $"{batch.Name} failed: {task.Exception?.GetBaseException().Message}";
        }
        state.NeedsRedraw = true;
        return true;
    }

    // The capture stacked off the render thread, both masters written beside it under AUTO's names (PlanetaryAuto.OutputPaths), as
    // planetary stack --auto writes them: the capture identified (PlanetaryIdentification), the panel's planet and filter given where they
    // are chosen and its telescope where it was set on this capture, the viewer's last telescope where nothing names one (as the Best stack
    // took it, #817), then stacked by the routine --auto runs (PlanetaryAuto.OptionsFor) at the panel's strength. One writer,
    // PlanetaryAuto.Write.
    private void StartBatch(BatchView batch, string capture, CancellationToken appToken)
    {
        batch.Cts = CancellationTokenSource.CreateLinkedTokenSource(appToken);
        var token = batch.Cts.Token;
        AutoStacksStarted++;
        Volatile.Write(ref batch.Percent, 0);
        Volatile.Write(ref batch.Stage, (int)PlanetaryStackStage.Stacking);
        (state.AutoStackProgress, state.AutoStackStage) = (0, PlanetaryStackStage.Stacking);
        // The stage by name (#1879), and a percent that is Stacking's own, held at 99 through the stages after it, 100 when the last ends.
        // The percent first, so a tick that sees the new stage never sees the old stage's percent.
        var progress = new SynchronousProgress<PlanetaryStackProgress>(p =>
        {
            if (p.Stage != PlanetaryStackStage.Derotating)
            {
                Volatile.Write(ref batch.Percent, p.Stage == PlanetaryStackStage.Stacking ? (int)Math.Min(99, p.Fraction * 100) : p.Fraction >= 1 ? 100 : 99);
            }
            Volatile.Write(ref batch.Stage, (int)p.Stage);
        });
        var directory = Path.GetDirectoryName(Path.GetFullPath(capture)) ?? ".";
        // Read here, on the render thread, before the run takes them off it.
        var setHere = state.PlanetaryTelescopeSetHere;
        var given = new PlanetaryIdentityGiven(state.PlanetaryBody, state.PlanetaryFilterNm, setHere ? state.PlanetaryApertureMm : null,
            setHere ? state.PlanetaryDesign : OpticalDesign.Unknown);
        var (lastMm, lastDesign, strength, saturation) = (state.PlanetaryApertureMm, state.PlanetaryDesign, state.PlanetaryStrength, state.PlanetaryColourSaturation);
        var gate = AutoRunGate;
        state.StatusMessage = "Full stack running: the capture identified, then stacked at the measured defaults";
        batch.Task = Task.Run(async () =>
        {
            if (gate is not null)
            {
                await gate.WaitAsync(token);
            }
            var identity = await PlanetaryIdentification.IdentifyAsync(capture, given, external, token);
            // Nothing the capture says names a telescope: the viewer's last one, as the Best stack took it.
            if (identity.ApertureMm is null && lastMm is { } mm)
            {
                identity = identity with { ApertureMm = mm, Design = lastDesign, TelescopeFrom = "the viewer's last telescope" };
            }
            using var stream = SerFrameStream.Open(capture);
            var result = await PlanetaryBestStack.RunAsync(stream, AutoOptions(identity, strength, saturation), progress, token);
            progress.Report(new PlanetaryStackProgress(PlanetaryStackStage.Writing, 0));
            return Written(result, PlanetaryAuto.OutputPaths(directory, Path.GetFileNameWithoutExtension(capture)), capture, identity);
        }, token);
    }

    // The Auto stack's options: the identity's, with the panel's strength and balance saturation stops (#1880).
    internal static PlanetaryBestStackOptions AutoOptions(PlanetaryIdentity identity, double strength, double saturation)
        => PlanetaryAuto.OptionsFor(identity) with { Strength = strength, ColourSaturation = saturation };

    // How a finished batch stack was made, in the words its status line shows: the sharpening, the balance when there was one, and the
    // alignment-point cap when it cut the grid, which is silent otherwise (#1412).
    internal static string BatchHow(PlanetaryBestStackResult result)
    {
        var how = result.Balance is null ? result.HowSharpened : $"{result.HowSharpened}; {result.HowBalanced}";
        return FeatureDetector.CapInWords(result.Stack.AlignmentPoints, result.Stack.AlignmentPointCandidates) is { } cap ? $"{how}; {cap}" : how;
    }

    // A finished run's masters written, and handed on to the render thread with the outcome: the layer's master and, where there is no
    // layer, the sharpened master. Released here if the write throws.
    private static AutoStackOutcome Written(PlanetaryBestStackResult result, (string Master, string Sharpened) paths, string capture, PlanetaryIdentity identity)
    {
        var handedOn = false;
        try
        {
            PlanetaryAuto.Write(result, paths);
            handedOn = true;
            return new AutoStackOutcome(paths.Sharpened, BatchHow(result), capture, result.Layer, result.Layer is null ? result.Sharpened : null, identity,
                result.Stack.FramesUsed, result.Stack.FramesGraded);
        }
        finally
        {
            result.Stack.Master.Release();
            if (!handedOn || result.Layer is not null)
            {
                result.Sharpened.Release();
            }
            if (!handedOn)
            {
                result.Layer?.Master.Release();
            }
        }
    }

    // Which backends serve the frame on show, asked once per (document, pipeline) while the Enhance popover is open. The
    // roles may probe a licence (a process), so the ask runs on the pool and the render thread polls the Task.
    private Task<ImmutableArray<EnhanceChoice>>? _enhanceChoicesTask;
    private AstroImageDocument? _enhanceChoicesDocument;
    private SharpenPipeline? _enhanceChoicesPipeline;

    private void TickEnhanceChoices()
    {
        if (_enhanceChoicesTask is { IsCompleted: true } done)
        {
            _enhanceChoicesTask = null;
            var choices = done.IsCompletedSuccessfully ? done.Result : [new EnhanceChoice(EnhanceBackend.Auto, "Auto", [])];
            if (ReferenceEquals(_enhanceChoicesDocument, Document) && ReferenceEquals(_enhanceChoicesPipeline, EnhancePipeline))
            {
                state.EnhanceChoices = choices;
                // A preference the frame cannot be served with is not a choice on screen, so it falls back to Auto.
                if (!choices.Any(c => c.Backend == state.PreferredEnhanceBackend))
                {
                    state.PreferredEnhanceBackend = EnhanceBackend.Auto;
                }
                state.NeedsRedraw = true;
            }
        }

        if (!state.EnhancePopover.IsOpen || state.EnhancesAsPlanet || _enhanceChoicesTask is not null
            || Document is not { } doc || EnhancePipeline is not { } pipeline
            || (ReferenceEquals(_enhanceChoicesDocument, doc) && ReferenceEquals(_enhanceChoicesPipeline, pipeline)))
        {
            return;
        }

        _enhanceChoicesDocument = doc;
        _enhanceChoicesPipeline = pipeline;
        var image = doc.UnstretchedImage;
        _enhanceChoicesTask = Task.Run(() => EnhanceChoices.For(pipeline, image));
    }

    public void TryApplyPendingEnhance(CancellationToken appToken = default)
    {
        TickEnhanceChoices();
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
            // cast the moment that fit landed. See AstroImageDocument.InheritColorCalibration. A result whose balance
            // went INTO its pixels (the original's broadband SPCC, applied before the enhance) already shows it, and
            // inheriting the multiplier as well would apply it twice.
            if (Document is { } original && !enhancedDoc.IsColourInPixels)
            {
                enhancedDoc.InheritColorCalibration(original);
            }
            // ... and it must not be anchored to the frame it came from. DisplayCarry compares frame
            // SHAPE, which an enhance result matches exactly, so without this it took the pre-enhance
            // statistics as its display basis and the flattened background was neutralised twice --
            // a colour shift one frame after the enhance landed. See AstroImageDocument.IsEnhanceResult.
            enhancedDoc.MarkAsEnhanceResult();
            // The enhanced pixels ARE the crop, so the display crop has to come off -- left on, it would
            // crop the crop. It is remembered (as every crop is), which is what puts it back on revert.
            if (enhancedDoc.SourceCrop is not null)
            {
                state.DisplayCrop = null;
            }
            Document = enhancedDoc;
            // The "after" half of the pair P30 needs: same file, same panel, one enhance apart.
            LogStretchBasis("enhanced", enhancedDoc);
            _rawSource = enhancedDoc;
            DropMasterLayer();
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

        // Nor step off a frame before its stars have graded it: a blink is the grading pass (LightGrading), and a step
        // cancels the detection running on the frame it leaves. Only the first time round: a frame graded already
        // steps at the blink's own pace, so a second pass over a graded folder runs as fast as it always did.
        if (_starDetectionTask is { IsCompleted: false }
            && state.SelectedFileIndex >= 0 && state.SelectedFileIndex < state.ImageFileNames.Count
            && state.GradeOf(state.ImageFileNames[state.SelectedFileIndex]) is null)
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
        var count = state.ImageFileNames.Count;
        var next = state.SelectedFileIndex + (state.BlinkStep < 0 ? -1 : 1);
        // Wrapping both ways, so a backward blink off the top lands on the last file rather than
        // stalling at index 0 -- and an unselected list (-1) still enters at an end.
        ViewerActions.SelectFile(state, next < 0 ? count - 1 : next >= count ? 0 : next);
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
    /// <remarks>
    /// The Auto view is a still picture and costs a still frame (#1875): while <see cref="ViewerState.ShowAuto"/> is up, its run going on
    /// or its master shown, the player neither advances nor decodes (the playhead holds and resumes when another view is chosen, or when the
    /// run is cancelled or fails, which leaves the view under it playing) and the
    /// live rolling stack does not follow or re-stack. The loop is then asked for a frame only when a frame's content changed: a master
    /// published, the on-show layer re-sharpening after a dial change, or the Auto progress label (<see cref="AutoStackWantsFrame"/>,
    /// the one per-progress wake); never merely because a run is busy.
    /// </remarks>
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

        if (!state.IsSequence && state.IsPlanetaryMaster && _liveSource is { } layer)
        {
            return TickMasterLayer(layer);
        }

        if (raw is not ISequencePlaybackSource seq || !state.IsSequence)
        {
            return false;
        }

        var autoUp = AutoHolds;
        var rawPublished = !autoUp && _player.Tick(seq, state, _playbackClock.Elapsed.TotalSeconds);

        // The strip's statistics follow the shown frame once it settles: measured off-thread, taken here, and the loop kept awake meanwhile.
        // Only while the transport stands still: a playing SER's shown frame never settles, so polling then would keep the loop from
        // idling between frames and measure frames nobody reads.
        var readoutChanged = false;
        var readoutPending = false;
        if (raw is SerPreviewSource ser && !state.IsPlaying && !_player.SeekPending)
        {
            readoutChanged = ser.PollShownFrameReadout();
            readoutPending = ser.ReadoutPending;
            if (readoutChanged)
            {
                state.NeedsRedraw = true;
            }
        }

        // The chosen batch view's layer builds and re-sharpens its one master (#1314 part 2); once it is on show the dials are its own,
        // swapped with the other views' as it comes and goes, and the live stack stops following.
        var masterPublished = false;
        var batch = state.ShowAuto ? _auto : null;
        if (batch?.Layer is { } chosenLayer)
        {
            masterPublished = chosenLayer.TryPublishMaster();
            chosenLayer.RequestFollow(0);
        }
        var batchLayer = BatchLayerOnShow;
        var dialsOwner = batchLayer is null ? null : _auto;
        if (dialsOwner != _dialsOnShow)
        {
            SwapDials(dialsOwner);
            state.NeedsTextureUpdate = true;
        }
        var bestOnShow = batchLayer is not null;

        // Choosing Auto swaps the picture under it for the held raw frame until the master lands (#1875), and leaving it swaps back.
        var awaiting = AutoAwaitsMaster;
        if (awaiting != _autoAwaitedOnShow)
        {
            _autoAwaitedOnShow = awaiting;
            state.NeedsTextureUpdate = true;
        }

        // Live rolling-window stack: consume any finished master first (so the just-completed result is
        // published before we kick the next one), then follow the current playhead. Only runs while the
        // stacked view is requested -- no CPU spent stacking when showing the raw frame.
        LiveStackPreviewSource? sharpening = batchLayer ?? (state.ShowStacked && !autoUp ? _liveSource : null);
        if (sharpening is not null)
        {
            // Push changed wavelet-sharpen params (null = off); the source re-sharpens the cached master
            // off-thread without re-stacking. Cheap no-op compare via the dirty flag, so this runs per tick.
            // A finished derivation seeds the sliders before their params are pushed; a Derive asked for starts one.
            _derivation.Tick(state, sharpening, state.SequencePath, timeProvider.GetUtcNow(), logger);
            if (state.WaveletDirty)
            {
                sharpening.SetSharpen(state.BuildWaveletOptions(), state.WaveletLimb, state.PlanetaryLook);
                state.WaveletDirty = false;
            }
            if (!bestOnShow)
            {
                masterPublished |= sharpening.TryPublishMaster();
                sharpening.RequestFollow(state.FrameIndex);
            }
        }

        // Upload whichever source is actually on screen. A raw frame advance only re-uploads when the raw
        // frame is shown; a new master always becomes the displayed image (it only publishes while stacked).
        var showingStacked = bestOnShow || (!awaiting && state.ShowStacked && _liveSource is { HasMaster: true });
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
        // so this briefly spins for the ~task duration, then the loop idles again. Only a layer on show counts: an Auto run
        // still building has no master to publish, and its progress wakes the loop through AutoStackWantsFrame (#1875).
        return rawPublished || masterPublished || readoutChanged || readoutPending || (!autoUp && _player.SeekPending)
            || (!autoUp && state.ShowStacked && _liveSource is { IsBusy: true }) || batchLayer is { IsBusy: true };
    }

    // The dials follow the view on show (#1314 part 2): the one leaving keeps what it had, the one coming (a batch view, or null for the
    // live view) takes what it kept, a batch view first its run's own derivation, and a derivation's dials take the stop the panel is on.
    private void SwapDials(BatchView? coming)
    {
        var leaving = new ViewDials(state.WaveletGains, state.WaveletDerived, state.WaveletLimb, state.DerivedWaveletGains,
            state.WaveletSharpenEnabled, state.WaveletDeriveNote);
        if (_dialsOnShow is { } leavingBatch)
        {
            leavingBatch.Dials = leaving;
        }
        else
        {
            _liveDials = leaving;
        }
        _dialsOnShow = coming;
        if ((coming is null ? _liveDials : coming.Dials) is { } dials)
        {
            (state.WaveletGains, state.WaveletDerived, state.WaveletLimb, state.DerivedWaveletGains, state.WaveletSharpenEnabled, state.WaveletDeriveNote) =
                (dials.Gains, dials.Derived, dials.Limb, dials.DerivedGains, dials.Enabled, dials.Note);
            if (dials.Derived)
            {
                state.ChooseStrength(state.PlanetaryStrength);
            }
        }
        state.WaveletDirty = true;
    }

    // A batch stack's master shown behind its view's layer, and the dials it opens with: the run's own derivation where its gains were
    // derived (no Derive needed), else its sharpened master as written with the sharpening off (#1314 part 2).
    private void AdoptBatch(BatchView batch, AutoStackOutcome outcome)
    {
        DropBatch(batch);
        var (master, derived) = outcome.Layer is { } layer ? (layer.Master, layer.Derived) : (outcome.Sharpened, null);
        if (master is null)
        {
            return;
        }
        // A batch stack names its planet (OBJECT), so it is shown in the planetary stretch, as opening it as a file did (StretchMode.ForFrame).
        if (StretchMode.ForFrame(master.ImageMeta, StretchMode.None) is StretchMode.Planetary)
        {
            state.StretchMode = StretchMode.Planetary;
        }
        // The capture's own facts are the SER on show's, cached at its open; the layer shows them too.
        batch.Layer = new LiveStackPreviewSource(new FixedMaster(master), outcome.CapturePath, timeProvider, logger)
        {
            Capture = string.Equals(state.SequencePath, outcome.CapturePath, StringComparison.OrdinalIgnoreCase) ? _rawSource?.Capture : null,
        };
        batch.Capture = outcome.CapturePath;
        batch.Dials = derived is { } d
            ? new ViewDials(d.Gains, true, d.Limb, d, true, $"Gains {d.How}")
            : new ViewDials(state.WaveletGains, false, null, null, false, null);
    }

    // A batch view's layer goes, disposed once nothing renders it, and with it its dials (another capture, or a new run).
    private void DropBatch(BatchView batch)
    {
        if (batch.Layer is { } layer)
        {
            batch.Layer = null;
            StashForDispose(layer);
        }
        if (_dialsOnShow == batch)
        {
            // Its dials leave with it: the live view's come back.
            SwapDials(null);
        }
        (batch.Capture, batch.Dials) = (null, null);
    }

    // An enhance result or its revert is shown as it is: a planetary master's sharpening layer goes, disposed once nothing renders it
    // (#1314). The live source is only ever a master's layer here, as an enhance runs on a still.
    private void DropMasterLayer()
    {
        var layer = _liveSource;
        _liveSource = null;
        StashForDispose(layer);
        (state.MasterPlanet, state.MasterPath) = (null, null);
    }

    // Whether the planetary master's layer was on show at the last tick, so a switch between it and the file as it is re-uploads.
    private bool _masterLayerWasOnShow;

    /// <summary>
    /// A planetary master's sharpening layer, one tick (#1314): the stacked view's own steps over the one master. A finished or asked
    /// for derivation, the dials pushed when they change, the master built and re-sharpened off the render thread. Kept warm while
    /// its sharpening is off, so turning it on shows a master at once.
    /// </summary>
    private bool TickMasterLayer(LiveStackPreviewSource layer)
    {
        _derivation.Tick(state, layer, state.MasterPath, timeProvider.GetUtcNow(), logger);
        if (state.WaveletDirty)
        {
            layer.SetSharpen(state.BuildWaveletOptions(), state.WaveletLimb, state.PlanetaryLook);
            state.WaveletDirty = false;
        }
        var published = layer.TryPublishMaster();
        layer.RequestFollow(0);

        var onShow = state.SharpenLayerOnShow && layer.HasMaster;
        if ((published && onShow) || onShow != _masterLayerWasOnShow)
        {
            state.NeedsTextureUpdate = true;
        }
        _masterLayerWasOnShow = onShow;
        return published || layer.IsBusy;
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

    // A plain image whose display is remembered per file (#1814): not an enhance result, which is the same file after a transform, and
    // not a pre-stretched one. A SER and a planetary master are left out by the callers (no document, a sharpening layer).
    private bool RemembersFile(AstroImageDocument document) => external is not null && !document.IsEnhanceResult && !document.IsPreStretched;

    private async Task<ViewerDisplayRecord?> LoadRememberedDisplayAsync(string path, CancellationToken token)
    {
        try
        {
            return external is null ? null : await ViewerDisplayMemory.LoadAsync(external, path, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not read the remembered display of {FilePath}", path);
            return null;
        }
    }

    /// <summary>
    /// Writes the display of the file on show, when the user changed it since it opened (#1814). The record is captured on the calling
    /// thread. A follower of a held run keeps nothing: its display is the anchor's, not its own, and neither does an enhance result,
    /// which carries its source's path but shows the enhanced view's display (and drops its crop): the source's record stays as it was.
    /// </summary>
    private Task SaveLeavingDisplayAsync()
    {
        if (external is null
            || _displayPath is not { } path
            || _displayAtOpen is not { } atOpen
            || Document is not { } document
            || !string.Equals(document.FilePath, path, StringComparison.OrdinalIgnoreCase)
            || document.IsEnhanceResult
            || (state.CarryDisplayAcrossFrames && document.DisplayAnchor is not null))
        {
            return Task.CompletedTask;
        }

        var now = ViewerDisplayMemory.Capture(state);
        if (now == atOpen)
        {
            return Task.CompletedTask;
        }

        _displayAtOpen = now;
        return SaveDisplayAsync(external, path, now);
    }

    private async Task SaveDisplayAsync(IExternal store, string path, ViewerDisplayRecord record)
    {
        try
        {
            await ViewerDisplayMemory.SaveAsync(store, path, record, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not remember the display of {FilePath}", path);
        }
    }

    /// <summary>
    /// Reset display (#1814): every display setting back to its default for the file on show, its crop dropped, and its remembered
    /// record forgotten, so the next open shows the defaults too.
    /// </summary>
    public void ResetDisplay()
    {
        if (Document is not { } document)
        {
            return;
        }

        ViewerDisplayMemory.Apply(ViewerDisplayMemory.Capture(new ViewerState()), state);
        state.StretchMode = document.IsPreStretched ? StretchMode.None
            : StretchMode.ForFrame(document.UnstretchedImage.ImageMeta, StretchMode.None) is StretchMode.Planetary ? StretchMode.Planetary
            : document.UnstretchedImage.ChannelCount < 3 && ViewerActions.DefaultStretchMode is StretchMode.Linked or StretchMode.Luma ? StretchMode.Unlinked
            : ViewerActions.DefaultStretchMode;
        state.HistogramLogScale = state.StretchMode is StretchMode.None;
        state.ManualWhiteBalanceBeforeCalibration = null;
        state.DisplayCrop = null;
        _rememberedCrop = null;

        if (external is not null && _displayPath is { } path)
        {
            ViewerDisplayMemory.Forget(external, path);
            _displayAtOpen = ViewerDisplayMemory.Capture(state);
        }
    }

    /// <summary>
    /// Awaits all in-flight tasks and disposes the star detection CTS. Call at app shutdown.
    /// </summary>
    public async Task ShutdownAsync()
    {
        // The file on show keeps its display, before anything below lets go of it.
        await SaveLeavingDisplayAsync();
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
        // The Auto stack told to stop, then waited on.
        _auto.Cts?.Cancel();
        if (_auto.Task is { } running)
        {
            try { await running; } catch (OperationCanceledException) { logger.LogDebug("{Stack} cancelled during shutdown", _auto.Name); }
        }
        _derivation.Dispose();

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
        if (_auto.Layer is { } autoLayer)
        {
            await autoLayer.DisposeAsync();
        }
    }
}
